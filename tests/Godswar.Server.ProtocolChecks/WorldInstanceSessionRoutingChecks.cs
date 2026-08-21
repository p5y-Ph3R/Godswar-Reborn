using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldInstanceSessionRoutingChecks
{
    public const string CheckName =
        "B18B instance-aware session routing";

    private const byte SharedMapId = 40;
    private const int FirstAccountId = 801;
    private const int FirstCharacterId = 8_001;
    private const int SecondAccountId = 802;
    private const int SecondCharacterId = 8_002;
    private const int DefaultAccountId = 803;
    private const int DefaultCharacterId = 8_003;
    private const uint FirstObjectId = 0x7901;
    private const uint SecondObjectId = 0x7902;
    private const uint DefaultObjectId = 0x7903;

    private static readonly DateTimeOffset TestTime =
        new(2026, 7, 31, 6, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await using var firstSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var secondSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var defaultSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = new GameSessionRegistry(
            worldInstanceOptions: new WorldInstanceRuntimeOptions
            {
                RealmId = 1,
                MaximumRuntimes = 8,
                MaximumPlayerAssignments = 16,
                MaximumRetiredInstanceIds = 64,
                DefaultOpenWorldPlayerCapacity = 8,
                MailboxCapacity = 16,
                OwnerInvocationTimeoutMilliseconds = 2_000,
                ShutdownDrainTimeoutMilliseconds = 2_000,
                MaximumFanoutConcurrency = 2
            });

        Check.Throws<InvalidOperationException>(
            () => registry.CreateLocalWorldInstanceAsync(
                    RealmId.Dwargon,
                    new WorldMapId(SharedMapId),
                    InstanceKind.Dungeon,
                    playerCapacity: 4)
                .AsTask()
                .GetAwaiter()
                .GetResult(),
            "one-realm process rejects cross-realm local creation");

        var firstInstanceId =
            await CreateDungeonAsync(registry);
        var secondInstanceId =
            await CreateDungeonAsync(registry);
        Check.True(
            firstInstanceId != secondInstanceId,
            "same-content dungeons receive distinct instance IDs");

        var firstCharacter = CreateCharacter(
            FirstCharacterId,
            FirstAccountId,
            "FirstRouteHero");
        var secondCharacter = CreateCharacter(
            SecondCharacterId,
            SecondAccountId,
            "SecondRouteHero");
        var defaultCharacter = CreateCharacter(
            DefaultCharacterId,
            DefaultAccountId,
            "DefaultRouteHero");

        var wrongRealmCharacter = CreateCharacter(
            FirstCharacterId,
            FirstAccountId,
            "WrongRealmRouteHero");
        wrongRealmCharacter.RealmId = RealmId.Dwargon;
        Check.Throws<InvalidOperationException>(
            () => registry.JoinWorldInstance(
                firstSocket.Session,
                FirstAccountId,
                wrongRealmCharacter,
                FirstObjectId,
                firstInstanceId,
                worldReady: true,
                joinedAt: TestTime),
            "explicit world join rejects a cross-realm character");

        registry.JoinWorldInstance(
            firstSocket.Session,
            FirstAccountId,
            firstCharacter,
            FirstObjectId,
            firstInstanceId,
            worldReady: true,
            joinedAt: TestTime);
        Check.Throws<InvalidOperationException>(
            () => registry.UpdateCharacter(
                firstSocket.Session,
                wrongRealmCharacter),
            "active character update cannot switch realms");
        registry.JoinWorldInstance(
            secondSocket.Session,
            SecondAccountId,
            secondCharacter,
            SecondObjectId,
            secondInstanceId,
            worldReady: true,
            joinedAt: TestTime);
        registry.JoinMap(
            defaultSocket.Session,
            DefaultAccountId,
            defaultCharacter,
            DefaultObjectId,
            worldReady: true,
            joinedAt: TestTime);

        AssertOnlySession(
            registry.GetWorldInstanceSessions(firstInstanceId),
            firstSocket.Session,
            "first dungeon session list is isolated");
        AssertOnlySession(
            registry.GetWorldInstanceSessions(secondInstanceId),
            secondSocket.Session,
            "second dungeon session list is isolated");
        Check.True(
            registry.TryGetCurrentWorldSessionByCharacterId(
                firstSocket.Session,
                SharedMapId,
                FirstCharacterId,
                out var firstSelfContext) &&
            ReferenceEquals(
                firstSelfContext.Session,
                firstSocket.Session),
            "self-validation resolves the routed dungeon, not the default");
        Check.Equal(
            1,
            registry.GetWorldInstancePopulation(firstInstanceId),
            "first dungeon population is isolated");
        Check.Equal(
            1,
            registry.GetWorldInstancePopulation(secondInstanceId),
            "second dungeon population is isolated");

        var legacySessions =
            registry.GetMapSessions(SharedMapId);
        AssertOnlySession(
            legacySessions,
            defaultSocket.Session,
            "legacy map lookup resolves only the Tempest default");
        var defaultInstanceId =
            legacySessions.Single().WorldInstanceId;
        Check.True(
            defaultInstanceId != firstInstanceId &&
            defaultInstanceId != secondInstanceId,
            "legacy projection has its own open-world identity");
        Check.Equal(
            1,
            registry.GetMapPopulation(SharedMapId),
            "legacy population excludes same-content dungeons");

        var firstMonster = CreateMonster(
            0x7B01,
            "FirstDungeonMonster",
            x: 11f,
            z: 12f);
        var secondMonster = CreateMonster(
            0x7B02,
            "SecondDungeonMonster",
            x: 21f,
            z: 22f);
        var defaultMonster = CreateMonster(
            0x7B03,
            "DefaultWorldMonster",
            x: 31f,
            z: 32f);
        Check.Equal(
            1,
            registry.InitializeMapMonsters(
                firstSocket.Session,
                SharedMapId,
                [firstMonster],
                TestTime),
            "first dungeon bootstrap initializes its exact owner");
        Check.Equal(
            1,
            registry.InitializeMapMonsters(
                secondSocket.Session,
                SharedMapId,
                [secondMonster],
                TestTime),
            "second dungeon bootstrap initializes its exact owner");
        Check.Equal(
            1,
            registry.InitializeMapMonsters(
                SharedMapId,
                [defaultMonster],
                TestTime),
            "legacy bootstrap still initializes the Tempest default");
        AssertOnlyMonster(
            registry.GetMapMonsterSnapshots(
                firstSocket.Session,
                SharedMapId),
            firstMonster.ObjectId,
            "first dungeon monster bootstrap is isolated");
        AssertOnlyMonster(
            registry.GetMapMonsterSnapshots(
                secondSocket.Session,
                SharedMapId),
            secondMonster.ObjectId,
            "second dungeon monster bootstrap is isolated");
        AssertOnlyMonster(
            registry.GetMapMonsterSnapshots(SharedMapId),
            defaultMonster.ObjectId,
            "legacy monster bootstrap remains default-world only");

        await AssertInstanceBroadcastAsync(
            registry,
            firstInstanceId,
            PacketBuilder.RemoveWorldObjects(0x7A01),
            firstSocket,
            secondSocket,
            defaultSocket);
        await AssertInstanceBroadcastAsync(
            registry,
            secondInstanceId,
            PacketBuilder.RemoveWorldObjects(0x7A02),
            secondSocket,
            firstSocket,
            defaultSocket);
        await AssertLegacyBroadcastAsync(
            registry,
            PacketBuilder.RemoveWorldObjects(0x7A03),
            defaultSocket,
            firstSocket,
            secondSocket);

        Check.True(
            registry.TryGetSessionWorldInstanceId(
                firstSocket.Session,
                out var capturedSourceInstanceId) &&
            capturedSourceInstanceId == firstInstanceId,
            "transfer captures the exact source instance before mutation");
        Check.True(
            registry.TryTransferWorldInstance(
                firstSocket.Session,
                firstInstanceId,
                secondInstanceId,
                targetX: 15f,
                targetZ: 16f),
            "explicit same-content instance transfer succeeds");
        Check.True(
            registry.TryGetSessionWorldInstanceId(
                firstSocket.Session,
                out var transferredInstanceId) &&
            transferredInstanceId == secondInstanceId &&
            capturedSourceInstanceId == firstInstanceId,
            "source identity remains usable after the session route moves");
        Check.Equal(
            0,
            registry.GetWorldInstancePopulation(firstInstanceId),
            "transfer removes exactly one source owner");
        Check.Equal(
            2,
            registry.GetWorldInstancePopulation(secondInstanceId),
            "transfer adds exactly one destination owner");
        Check.Equal(
            0,
            registry.GetWorldInstanceSessions(firstInstanceId).Count,
            "transferred session is never visible in its source");
        AssertOnlySession(
            registry.GetWorldInstanceSessions(secondInstanceId),
            secondSocket.Session,
            "hidden transfer is not prematurely visible in destination");
        AssertOnlySession(
            registry.GetMapSessions(SharedMapId),
            defaultSocket.Session,
            "instance transfer cannot leak into legacy projection");
        Check.True(
            !registry.TryTransferWorldInstance(
                firstSocket.Session,
                firstInstanceId,
                secondInstanceId,
                targetX: 20f,
                targetZ: 21f),
            "stale source instance cannot transfer twice");

        Check.True(
            !registry.TryMarkWorldReady(
                firstSocket.Session,
                new Dictionary<uint, long>(),
                out var unseenPlayers,
                TestTime.AddSeconds(1)),
            "destination waits for its existing visible player");
        Check.Equal(
            1,
            unseenPlayers.Count,
            "destination reports exactly one hydration prerequisite");
        Check.True(
            ReferenceEquals(
                secondSocket.Session,
                unseenPlayers.Single().Session),
            "hydration prerequisite belongs to destination instance");
        var knownRevisions = unseenPlayers.ToDictionary(
            context => context.ObjectId,
            context => context.WorldRevision);
        Check.True(
            registry.TryMarkWorldReady(
                firstSocket.Session,
                knownRevisions,
                out var remainingPlayers,
                TestTime.AddSeconds(2)),
            "transferred session becomes visible after hydration");
        Check.Equal(
            0,
            remainingPlayers.Count,
            "completed hydration has no unseen players");
        var destinationSessions =
            registry.GetWorldInstanceSessions(secondInstanceId);
        Check.Equal(
            2,
            destinationSessions.Count,
            "destination contains both distinct sessions once ready");
        Check.Equal(
            2,
            destinationSessions
                .Select(context => context.Session)
                .Distinct()
                .Count(),
            "destination has no duplicate session visibility");

        registry.Remove(firstSocket.Session);
        AssertOnlySession(
            registry.GetWorldInstanceSessions(secondInstanceId),
            secondSocket.Session,
            "remove releases only the transferred session");
        Check.Equal(
            1,
            registry.GetWorldInstancePopulation(secondInstanceId),
            "remove keeps destination placement and map population coherent");

        registry.JoinWorldInstance(
            firstSocket.Session,
            FirstAccountId,
            firstCharacter,
            FirstObjectId,
            firstInstanceId,
            worldReady: true,
            joinedAt: TestTime.AddSeconds(3));
        AssertOnlySession(
            registry.GetWorldInstanceSessions(firstInstanceId),
            firstSocket.Session,
            "released placement permits a clean explicit rejoin");
        AssertOnlySession(
            registry.GetWorldInstanceSessions(secondInstanceId),
            secondSocket.Session,
            "rejoin does not disturb the other instance");

        registry.Remove(firstSocket.Session);
        registry.Remove(secondSocket.Session);
        registry.Remove(defaultSocket.Session);
        Check.Equal(
            0,
            registry.GetWorldInstancePopulation(firstInstanceId),
            "first placement is empty after final remove");
        Check.Equal(
            0,
            registry.GetWorldInstancePopulation(secondInstanceId),
            "second placement is empty after final remove");
        Check.Equal(
            0,
            registry.GetMapPopulation(SharedMapId),
            "default placement is empty after final remove");

        AssertSettledOwner(
            registry.GetWorldInstanceOwnerSnapshot(firstInstanceId),
            "first dungeon owner");
        AssertSettledOwner(
            registry.GetWorldInstanceOwnerSnapshot(secondInstanceId),
            "second dungeon owner");
        AssertSettledOwner(
            registry.GetWorldInstanceOwnerSnapshot(defaultInstanceId),
            "default open-world owner");
    }

    private static async Task<WorldInstanceId> CreateDungeonAsync(
        GameSessionRegistry registry)
    {
        var result = await registry.CreateLocalWorldInstanceAsync(
            RealmId.Tempest,
            new WorldMapId(SharedMapId),
            InstanceKind.Dungeon,
            playerCapacity: 8);
        var runtime = result.Runtime ??
            throw new InvalidOperationException(
                "Dungeon creation returned no runtime.");
        Check.True(
            result.Status ==
                WorldInstanceRuntimeDirectoryStatus.Created,
            "dungeon runtime creation succeeds");
        return runtime.InstanceId;
    }

    private static async Task AssertInstanceBroadcastAsync(
        GameSessionRegistry registry,
        WorldInstanceId instanceId,
        byte[] packet,
        RuntimePolicySessionSocket expected,
        params RuntimePolicySessionSocket[] excluded)
    {
        Check.Equal(
            1,
            await registry.BroadcastToWorldInstanceAsync(
                instanceId,
                packet,
                CancellationToken.None,
                label: "InstanceRouting"),
            "instance broadcast has one recipient");
        var received =
            await expected.ReadPacketAsync(packet.Length);
        Check.True(
            packet.SequenceEqual(received),
            "instance broadcast reaches its exact recipient");
        foreach (var socket in excluded)
        {
            Check.Equal(
                0,
                socket.Available,
                "instance broadcast does not cross an instance boundary");
        }
    }

    private static async Task AssertLegacyBroadcastAsync(
        GameSessionRegistry registry,
        byte[] packet,
        RuntimePolicySessionSocket expected,
        params RuntimePolicySessionSocket[] excluded)
    {
        Check.Equal(
            1,
            await registry.BroadcastToMapAsync(
                SharedMapId,
                packet,
                CancellationToken.None,
                label: "LegacyRouting"),
            "legacy map broadcast has one default-world recipient");
        var received =
            await expected.ReadPacketAsync(packet.Length);
        Check.True(
            packet.SequenceEqual(received),
            "legacy broadcast reaches the Tempest default");
        foreach (var socket in excluded)
        {
            Check.Equal(
                0,
                socket.Available,
                "legacy broadcast does not enter same-content dungeons");
        }
    }

    private static void AssertOnlySession(
        IReadOnlyList<GameSessionContext> contexts,
        Godswar.Server.Networking.ClientSession expected,
        string message)
    {
        Check.True(
            contexts.Count == 1 &&
            ReferenceEquals(contexts[0].Session, expected),
            message);
    }

    private static void AssertOnlyMonster(
        IReadOnlyList<MonsterRuntimeSnapshot> monsters,
        uint expectedObjectId,
        string message)
    {
        Check.True(
            monsters.Count == 1 &&
            monsters[0].ObjectId == expectedObjectId,
            message);
    }

    private static void AssertSettledOwner(
        SingleOwnerMailboxSnapshot snapshot,
        string owner)
    {
        Check.True(
            snapshot.State == SingleOwnerMailboxState.Accepting,
            $"{owner} remains accepting");
        Check.True(
            snapshot.Processed > 0 &&
            snapshot.Accepted == snapshot.Processed,
            $"{owner} processed every admitted command");
        Check.True(
            snapshot.Depth == 0 &&
            snapshot.Queued == 0 &&
            snapshot.Active == 0,
            $"{owner} has no unfinished command");
        Check.Equal(
            1,
            snapshot.HighWaterDepth,
            $"{owner} processed sequential commands one at a time");
        Check.True(
            snapshot.Rejected == 0 &&
            snapshot.CommandFaults == 0 &&
            snapshot.WorkerFaults == 0 &&
            snapshot.Abandoned == 0,
            $"{owner} has healthy mailbox accounting");
    }

    private static GameCharacter CreateCharacter(
        int characterId,
        int accountId,
        string name) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            Name = name,
            CreatedUtc = TestTime.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = SharedMapId,
            PositionX = 5f,
            PositionZ = 6f,
            Level = 40,
            CurrentHp = 4_000,
            MaxHp = 4_000,
            CurrentMp = 2_000,
            MaxMp = 2_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

}
