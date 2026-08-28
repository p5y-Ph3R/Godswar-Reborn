using System.Net;
using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking.Backhaul;
using Godswar.Server.State;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaInstanceAdmissionChecks
{
    public const string CheckName =
        "Medusa admitted-character world-membership gate";

    private static readonly DateTimeOffset StartedAt = new(
        2026,
        8,
        22,
        12,
        0,
        0,
        TimeSpan.Zero);

    public static async Task RunAsync()
    {
        CheckBoundAndUnboundDecisions();
        await CheckMapJoinAndExistingMembershipAsync();
        await CheckStagedTransferBoundaryAsync();
        await CheckLegacyStagingFencesBindingAsync();
        await CheckRegistryPreservesUnboundJoinsAsync();
        await CheckDefaultReconnectFailsClosedAsync();
        await CheckStaticGatewayRoutesFailClosedAsync();
    }

    private static void CheckBoundAndUnboundDecisions()
    {
        var unbound = CreateActiveMap(bound: false);
        var bound = CreateActiveMap(bound: true);
        var ordinary = unbound.CheckMedusaCharacterAdmission(999);
        var admitted = bound.CheckMedusaCharacterAdmission(101);
        var foreign = bound.CheckMedusaCharacterAdmission(999);

        Check.True(
            ordinary.Outcome ==
                MedusaInstanceCharacterAdmissionOutcome.InstanceUnbound &&
            ordinary.MayEnter &&
            !ordinary.IsBound &&
            admitted.Outcome ==
                MedusaInstanceCharacterAdmissionOutcome
                    .CharacterAdmitted &&
            admitted.MayEnter &&
            admitted.IsBound &&
            foreign.Outcome ==
                MedusaInstanceCharacterAdmissionOutcome
                    .CharacterNotAdmitted &&
            !foreign.MayEnter &&
            foreign.IsBound,
            "only a bound Medusa roster restricts character admission");
    }

    private static async Task CheckMapJoinAndExistingMembershipAsync()
    {
        var map = CreateActiveMap(bound: true);
        await using var admittedSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var foreignSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var admitted = CreateContext(
            map,
            admittedSocket.Session,
            characterId: 101,
            objectId: 0x7901);

        map.AddOrUpdate(admitted);
        map.AddOrUpdate(admitted with { WorldRevision = 2 });
        Check.True(
            map.Population == 1 &&
            map.Snapshot().Single().CharacterId == 101,
            "an admitted reconnect/update keeps one existing membership");

        var replacement = CreateContext(
            map,
            admittedSocket.Session,
            characterId: 999,
            objectId: admitted.ObjectId);
        Check.Throws<InvalidOperationException>(
            () => map.AddOrUpdate(replacement),
            "a foreign character cannot replace an admitted session");

        var foreign = CreateContext(
            map,
            foreignSocket.Session,
            characterId: 999,
            objectId: 0x7902);
        Check.Throws<InvalidOperationException>(
            () => map.AddOrUpdate(foreign),
            "a foreign character cannot join a bound Medusa map");
        Check.True(
            map.Population == 1 &&
            map.Snapshot().Single().CharacterId == 101,
            "rejected joins cannot disturb existing admitted membership");
        Check.True(
            map.Remove(admittedSocket.Session, out var removed) &&
            removed?.CharacterId == 101 &&
            map.Population == 0,
            "the admitted member remains removable through normal egress");
        map.AddOrUpdate(admitted with { WorldRevision = 3 });
        Check.True(
            map.Population == 1 &&
            map.Snapshot().Single().CharacterId == 101 &&
            map.Remove(admittedSocket.Session, out _),
            "an admitted character may reconnect after normal egress");
    }

    private static async Task CheckStagedTransferBoundaryAsync()
    {
        var bound = CreateActiveMap(bound: true);
        await using var admittedSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var foreignSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var foreign = CreateContext(
            bound,
            foreignSocket.Session,
            characterId: 999,
            objectId: 0x7912);

        Check.Throws<InvalidOperationException>(
            () => bound.StagePlayerTransfer(foreign),
            "a foreign staged transfer is rejected before ECS mutation");
        Check.True(
            bound.Population == 0 &&
            bound.Snapshot().Count == 0,
            "rejected staged transfer leaves no shadow membership");

        var admitted = CreateContext(
            bound,
            admittedSocket.Session,
            characterId: 101,
            objectId: 0x7911);
        using (var transfer = bound.StagePlayerTransfer(admitted))
        {
            Check.True(
                bound.Population == 1 &&
                bound.Snapshot().Count == 0,
                "an admitted transfer may stage before registry publication");
            transfer.Commit(static () => { });
        }
        Check.True(
            bound.Snapshot().Single().CharacterId == 101 &&
            bound.Remove(admittedSocket.Session, out _) &&
            bound.Population == 0,
            "an admitted staged transfer commits and exits normally");

        var unbound = CreateActiveMap(bound: false);
        using (var ordinaryTransfer =
               unbound.StagePlayerTransfer(foreign with
               {
                   RealmId = unbound.RealmId,
                   WorldInstanceId = unbound.WorldInstanceId,
                   MapId = unbound.MapId
               }))
        {
            Check.True(
                unbound.Population == 1,
                "unbound maps preserve ordinary staged transfers");
        }
        Check.True(
            unbound.Population == 0,
            "disposing an ordinary staged transfer rolls it back");
    }

    private static async Task CheckRegistryPreservesUnboundJoinsAsync()
    {
        await using var registry = new GameSessionRegistry(
            worldInstanceOptions: new WorldInstanceRuntimeOptions
            {
                RealmId = RealmId.Tempest.Value,
                MaximumRuntimes = 4,
                MaximumPlayerAssignments = 20,
                MaximumRetiredInstanceIds = 16,
                DefaultOpenWorldPlayerCapacity = 20,
                MailboxCapacity = 16,
                OwnerInvocationTimeoutMilliseconds = 2_000,
                ShutdownDrainTimeoutMilliseconds = 2_000,
                MaximumFanoutConcurrency = 2
            });
        var created = await registry.CreateLocalWorldInstanceAsync(
            RealmId.Tempest,
            new WorldMapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            CancellationToken.None);
        var runtime = created.Runtime ??
            throw new InvalidOperationException(
                "Unbound dungeon creation returned no runtime.");
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var character = CreateCharacter(999, runtime.MapId);

        registry.JoinWorldInstance(
            socket.Session,
            character.AccountId,
            character,
            objectId: 0x7921,
            runtime.InstanceId,
            worldReady: true,
            joinedAt: StartedAt);
        registry.JoinWorldInstance(
            socket.Session,
            character.AccountId,
            character,
            objectId: 0x7921,
            runtime.InstanceId,
            worldReady: true,
            joinedAt: StartedAt.AddSeconds(1));

        Check.True(
            registry.IsSessionInWorldInstance(
                socket.Session,
                runtime.InstanceId) &&
            registry.GetWorldInstancePopulation(runtime.InstanceId) == 1,
            "registry joins and same-instance reconnects remain unchanged for unbound maps");
        registry.Remove(socket.Session);
        Check.True(
            registry.GetWorldInstancePopulation(runtime.InstanceId) == 0,
            "unbound registry membership retains normal egress");
    }

    private static async Task CheckDefaultReconnectFailsClosedAsync()
    {
        await using var registry = new GameSessionRegistry();
        foreach (var mapId in new byte[] { 200, 204 })
        {
            await using var socket =
                await RuntimePolicySessionSocket.CreateAsync();
            var character = CreateCharacter(
                checked(1_000 + mapId),
                mapId);

            Check.Throws<InvalidOperationException>(
                () => registry.JoinPlayerMap(
                    socket.Session,
                    character.AccountId,
                    character,
                    worldReady: true,
                    joinedAt: StartedAt),
                $"saved Medusa map {mapId} cannot reconnect through an unbound default runtime");
            Check.Throws<InvalidOperationException>(
                () => registry.GetRequiredPlayerObjectId(socket.Session),
                $"rejected Medusa map {mapId} fallback leaves no partial world membership");
        }
    }

    private static async Task CheckStaticGatewayRoutesFailClosedAsync()
    {
        var assignedWorld = WorldInstanceId.New();
        var options = new WorldInstanceRuntimeOptions
        {
            RealmId = RealmId.Tempest.Value,
            ServerNodeId = "medusa-route-worker",
            StaticOpenWorldInstances =
            [
                new StaticOpenWorldInstanceOptions
                {
                    RealmId = RealmId.Tempest.Value,
                    MapId = 40,
                    WorldInstanceId = assignedWorld.Value.ToString("D")
                }
            ],
            RequireStaticOpenWorldOwnership = true
        };
        await using var registry = new GameSessionRegistry(
            worldInstanceOptions: options);
        var optionsField = typeof(GameSessionRegistry).GetField(
            "_worldInstanceOptions",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "GameSessionRegistry world options field was not found.");
        var pinned = (WorldInstanceRuntimeOptions?)optionsField.GetValue(
            registry)
            ?? throw new InvalidOperationException(
                "GameSessionRegistry world options were not pinned.");
        var route = pinned.StaticOpenWorldInstances.Single();

        foreach (var mapId in new short[] { 200, 204 })
        {
            route.MapId = mapId;
            await using var socket =
                await RuntimePolicySessionSocket.CreateAsync();
            var character = CreateCharacter(
                checked(1_300 + mapId),
                checked((byte)mapId));
            character.RealmId = RealmId.Tempest;
            var admission = new GatewayWorldAdmission(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                character.AccountId,
                character.Id,
                character.Name,
                RealmId.Tempest,
                new WorldMapId(mapId),
                assignedWorld,
                new ServerNodeId("medusa-route-worker"),
                StartedAt,
                StartedAt.AddSeconds(30),
                new IPEndPoint(IPAddress.Loopback, 31_000));

            Check.True(
                !registry.AcceptsGatewayAdmission(admission),
                $"gateway rejects a hostile static Medusa map {mapId} route even after options validation");
            Check.Throws<InvalidOperationException>(
                () => registry.JoinGatewayWorld(
                    socket.Session,
                    character.AccountId,
                    character,
                    objectId: checked((uint)(0x7A00 + mapId)),
                    admission,
                    worldReady: true,
                    joinedAt: StartedAt),
                $"gateway cannot materialize a static Medusa map {mapId} runtime");
            Check.Throws<InvalidOperationException>(
                () => registry.GetRequiredPlayerObjectId(socket.Session),
                $"rejected gateway map {mapId} leaves no partial membership");
        }
    }

    private static async Task CheckLegacyStagingFencesBindingAsync()
    {
        var descriptor = WorldInstanceDescriptor.Create(
            RealmId.Tempest,
            WorldInstanceId.New(),
            new WorldMapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            StartedAt);
        var map = new MapInstance(
            descriptor,
            playerRuntimeMode: PlayerRuntimeMode.Legacy);
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var foreign = CreateContext(
            map,
            socket.Session,
            characterId: 999,
            objectId: 0x7931);
        var staged = map.StagePlayerTransfer(foreign);

        var rejected = map.BindMedusaEncounter(
            MedusaEncounterDifficulty.Enhanced,
            [101],
            MedusaRunRuntimeCheckFixture.Spawns(
                MedusaEncounterDifficulty.Enhanced));
        Check.True(
            map.Population == 0 &&
            rejected.Outcome ==
                MedusaInstanceBindOutcome.RuntimeNotEmpty &&
            !map.TryGetMedusaOwnershipSnapshot(out _),
            "Legacy mode counts an unpublished ECS transfer reservation when binding");

        staged.Dispose();
        var rebound = map.BindMedusaEncounter(
            MedusaEncounterDifficulty.Enhanced,
            [101],
            MedusaRunRuntimeCheckFixture.Spawns(
                MedusaEncounterDifficulty.Enhanced));
        Check.True(
            rebound.IsBound,
            "rolling back the staged reservation reopens the unconsumed bind");
    }

    private static MapInstance CreateActiveMap(bool bound)
    {
        var descriptor = WorldInstanceDescriptor.Create(
            RealmId.Tempest,
            WorldInstanceId.New(),
            new WorldMapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            StartedAt);
        var map = new MapInstance(descriptor);
        if (bound)
        {
            var binding = map.BindMedusaEncounter(
                MedusaEncounterDifficulty.Enhanced,
                [101, 102],
                MedusaRunRuntimeCheckFixture.Spawns(
                    MedusaEncounterDifficulty.Enhanced));
            Check.True(
                binding.IsBound,
                "admission fixture binds an authored Medusa roster");
        }

        map.BindDescriptor(descriptor.TransitionTo(
            WorldInstanceLifecycleState.Active,
            StartedAt));
        return map;
    }

    private static GameSessionContext CreateContext(
        MapInstance map,
        Godswar.Server.Networking.ClientSession session,
        int characterId,
        uint objectId)
    {
        var character = CreateCharacter(characterId, map.MapId);
        return new(
            session,
            character.AccountId,
            character.Id,
            character.Name,
            map.RealmId,
            map.WorldInstanceId,
            map.MapId,
            objectId,
            character,
            WorldReady: true,
            WorldRevision: 1);
    }

    private static GameCharacter CreateCharacter(
        int characterId,
        byte mapId) => new()
    {
        Id = characterId,
        AccountId = checked(10_000 + characterId),
        Name = $"Admission{characterId}",
        CreatedUtc = StartedAt.UtcDateTime,
        Camp = GameDefaults.SpartaCamp,
        CurrentMap = mapId,
        PositionX = 1,
        PositionZ = 1,
        Level = 120,
        CurrentHp = 10_000,
        MaxHp = 10_000,
        CurrentMp = 10_000,
        MaxMp = 10_000,
        Equipment = string.Empty,
        KitBag = string.Empty
    };
}
