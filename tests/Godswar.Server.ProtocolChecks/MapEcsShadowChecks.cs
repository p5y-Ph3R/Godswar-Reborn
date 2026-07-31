using System.Net;
using System.Net.Sockets;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MapEcsShadowChecks
{
    private const int AccountId = 71;
    private const int CharacterId = 731;
    private const uint PlayerObjectId = 0x6401;

    public static async Task RunAsync()
    {
        await CheckPlayerLifecycleAsync();
        CheckAuthoritativeNpcLifecycle();
    }

    private static async Task CheckPlayerLifecycleAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptedTask = listener.AcceptTcpClientAsync(timeout.Token);
        var outbound = new TcpClient();
        await outbound.ConnectAsync(
            IPAddress.Loopback,
            port,
            timeout.Token);
        using var accepted = await acceptedTask;
        await using var session = new ClientSession(new RawTcpLegacyTransport(outbound));

        var sparta = new MapInstance(0);
        var character = CreateCharacter(0, 100f, -100f);
        var joined = CreateContext(
            sparta,
            session,
            character,
            worldReady: false,
            worldRevision: 0);

        sparta.AddOrUpdate(joined);

        Check.Equal(1, sparta.Population, "legacy population after join");
        Check.True(
            ReferenceEquals(joined, sparta.Snapshot().Single()),
            "legacy Snapshot keeps returning the authoritative context");
        var joinedShadow = sparta.SnapshotEcsShadow();
        Check.Equal(1, joinedShadow.Players.Count, "shadow player count after join");
        Check.True(
            !joinedShadow.Players[0].WorldReady,
            "non-ready join is mirrored");
        Check.Equal(
            100f,
            joinedShadow.Players[0].Player.Transform.X,
            "join position is hydrated");
        Check.True(
            sparta.DiagnoseEcsShadow().IsMatch,
            "join parity diagnostics");
        Check.True(
            sparta.TryGetShadowPlayerEntity(session, out var joinedEntity),
            "join exposes a generation-safe entity");
        Check.True(
            sparta.TryGetShadowEntityByObjectId(
                PlayerObjectId,
                out var joinedObjectEntity) &&
            joinedObjectEntity == joinedEntity,
            "stable player object ID maps to the current entity");

        var movedWithinMapCharacter = CreateCharacter(0, 125f, -75f);
        var movedWithinMap = joined with
        {
            Character = movedWithinMapCharacter,
            WorldRevision = 1
        };
        sparta.AddOrUpdate(movedWithinMap);

        Check.True(
            sparta.TryGetShadowPlayerEntity(session, out var updatedEntity),
            "updated player entity is addressable");
        Check.True(
            updatedEntity != joinedEntity,
            "update atomically replaces the entity");
        Check.True(
            !sparta.IsShadowEntityAlive(joinedEntity),
            "pre-update handle is stale");
        Check.Equal(
            125f,
            sparta.SnapshotEcsShadow().Players[0].Player.Transform.X,
            "updated position replaces all player components");
        Check.True(
            sparta.DiagnoseEcsShadow().IsMatch,
            "ordinary update parity diagnostics");

        var worldReady = movedWithinMap with
        {
            WorldReady = true,
            WorldRevision = 2
        };
        sparta.AddOrUpdate(worldReady);

        Check.True(
            sparta.TryGetShadowPlayerEntity(session, out var readyEntity),
            "world-ready player entity is addressable");
        Check.True(
            readyEntity != updatedEntity &&
            !sparta.IsShadowEntityAlive(updatedEntity),
            "world-ready replacement invalidates the old handle");
        Check.True(
            sparta.SnapshotEcsShadow().Players.Single().WorldReady,
            "world-ready state is mirrored");
        Check.True(
            sparta.DiagnoseEcsShadow().IsMatch,
            "world-ready parity diagnostics");

        var invalidReplacement = worldReady with { MapId = 1 };
        var replacementRejected = false;
        try
        {
            sparta.AddOrUpdate(invalidReplacement);
        }
        catch (InvalidOperationException)
        {
            replacementRejected = true;
        }

        Check.True(
            replacementRejected &&
            ReferenceEquals(worldReady, sparta.Snapshot().Single()),
            "rejected ECS replacement retains the published map context");
        Check.True(
            sparta.TryGetShadowPlayerEntity(session, out var retainedEntity) &&
            retainedEntity == readyEntity &&
            sparta.IsShadowEntityAlive(readyEntity),
            "rejected ECS replacement retains the current entity generation");
        Check.True(
            sparta.DiagnoseEcsShadow().IsMatch,
            "rejected ECS replacement leaves parity clean");

        var athens = new MapInstance(1);
        var athensCharacter = CreateCharacter(1, -45f, 80f);
        var changedMap = worldReady with
        {
            MapId = 1,
            Character = athensCharacter,
            WorldRevision = 3
        };
        Check.True(
            sparta.Remove(session, out var removed) &&
            ReferenceEquals(worldReady, removed),
            "legacy map move removes the authoritative old context");
        athens.AddOrUpdate(changedMap);

        Check.Equal(0, sparta.Population, "old map population after move");
        Check.True(
            !sparta.IsShadowEntityAlive(readyEntity),
            "map leave invalidates the old-map handle");
        Check.True(
            sparta.DiagnoseEcsShadow().IsMatch,
            "old map parity after move");
        Check.Equal(1, athens.Population, "new map population after move");
        Check.True(
            athens.DiagnoseEcsShadow().IsMatch,
            "new map parity after move");
        Check.True(
            athens.TryGetShadowPlayerEntity(session, out var athensEntity),
            "new map owns a current player handle");
        Check.Equal(
            (byte)1,
            athens.SnapshotEcsShadow().Players.Single().Player.Transform.MapId,
            "new map transform is hydrated");

        Check.True(
            athens.Remove(session, out var left) &&
            ReferenceEquals(changedMap, left),
            "legacy removal result is unchanged");
        Check.Equal(0, athens.Snapshot().Count, "legacy snapshot after removal");
        Check.Equal(
            0,
            athens.SnapshotEcsShadow().Players.Count,
            "shadow snapshot after removal");
        Check.True(
            !athens.IsShadowEntityAlive(athensEntity),
            "removed player handle is stale");
        Check.True(
            athens.DiagnoseEcsShadow().IsMatch,
            "removal parity diagnostics");

        var invalidCharacter = CreateCharacter(1, 0f, 0f);
        var invalidForMap = CreateContext(
            sparta,
            session,
            invalidCharacter,
            worldReady: true,
            worldRevision: 4) with
        {
            MapId = 0
        };
        var rejected = false;
        try
        {
            sparta.AddOrUpdate(invalidForMap);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Check.True(
            rejected && sparta.Population == 0 &&
            sparta.Snapshot().Count == 0,
            "authoritative ECS rejection does not publish a transport session");
        Check.True(
            sparta.DiagnoseEcsShadow().IsMatch,
            "authoritative ECS rejection leaves no partial entity or fault");

        var legacySparta = new MapInstance(
            0,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Legacy);
        legacySparta.AddOrUpdate(invalidForMap);
        Check.True(
            ReferenceEquals(
                invalidForMap,
                legacySparta.Snapshot().Single()),
            "legacy rollback mode retains the prior transport write");
        var invalidDiagnostics = legacySparta.DiagnoseEcsShadow();
        Check.True(
            !invalidDiagnostics.IsMatch &&
            invalidDiagnostics.ActiveFaults.Count == 1,
            "legacy rollback exposes shadow rejection as instrumentation");
        Check.True(
            legacySparta.Remove(session, out _),
            "invalid legacy context remains removable");
        Check.True(
            legacySparta.DiagnoseEcsShadow().IsMatch,
            "removal clears the rejected-session diagnostic");

        Check.Equal(
            0,
            accepted.Available,
            "shadow lifecycle emits no network packets");
    }

    private static void CheckAuthoritativeNpcLifecycle()
    {
        var map = new MapInstance(0);
        var definitions = NpcSpawnDefinitionFactory.Create(
            mapId: 0,
            capturedSpawns: [],
            capturedAppearanceFallbacks: [],
            referenceDefinitions: []);
        var gearMentor = definitions.Single(static definition =>
            definition.NpcKey == "Sparta_070") with
        {
            Detail10077 = [1, 2, 3],
            Detail10080 = [4, 5, 6]
        };
        var forger = definitions.Single(static definition =>
            definition.NpcKey == "Sparta_085");

        var canonicalDefinitions =
            map.ObserveNpcDefinitions([gearMentor, forger]);

        var initial = map.SnapshotEcsShadow();
        Check.Equal(2, initial.Npcs.Count, "authoritative NPC hydration count");
        Check.Equal(
            2,
            canonicalDefinitions.Count,
            "ECS returns the canonical packet-source NPC set");
        Check.True(
            canonicalDefinitions[0].ObjectId <
            canonicalDefinitions[1].ObjectId,
            "canonical NPC definitions use stable object-ID order");
        Check.Equal(0, map.Population, "NPC observation does not affect population");
        Check.True(
            map.Snapshot().Count == 0,
            "NPC observation does not alter legacy player Snapshot");
        Check.True(
            map.DiagnoseEcsShadow().IsMatch,
            "authoritative NPC parity diagnostics");
        Check.True(
            map.TryGetShadowNpcEntity(
                gearMentor.ObjectId,
                out var mentorEntity),
            "Gear Mentor object ID maps to an entity");
        Check.True(
            map.TryGetShadowNpcEntity(
                forger.ObjectId,
                out var forgerEntity),
            "Forger object ID maps to an entity");

        gearMentor.Detail10077[0] = byte.MaxValue;
        var copiedMentor = map.SnapshotEcsShadow().Npcs
            .Single(npc => npc.Npc.Identity.ObjectId == gearMentor.ObjectId);
        Check.Equal(
            (byte)1,
            copiedMentor.Npc.Detail10077[0],
            "authoritative NPC detail bytes are copied");

        var equivalentMentor = gearMentor with
        {
            Detail10077 = [1, 2, 3],
            Detail10080 = [4, 5, 6]
        };
        var equivalentForger = forger with
        {
            Detail10077 = forger.Detail10077.ToArray(),
            Detail10080 = forger.Detail10080.ToArray()
        };
        map.ObserveNpcDefinitions([equivalentMentor, equivalentForger]);
        Check.True(
            map.TryGetShadowNpcEntity(
                equivalentMentor.ObjectId,
                out var sameMentorEntity) &&
            sameMentorEntity == mentorEntity,
            "equivalent NPC observation is idempotent");

        var relocatedMentor = equivalentMentor with
        {
            X = equivalentMentor.X + 3f
        };
        map.ObserveNpcDefinitions([relocatedMentor, equivalentForger]);
        Check.True(
            map.TryGetShadowNpcEntity(
                relocatedMentor.ObjectId,
                out var relocatedMentorEntity) &&
            relocatedMentorEntity != mentorEntity,
            "changed NPC definition atomically replaces its entity");
        Check.True(
            !map.IsShadowEntityAlive(mentorEntity),
            "replaced NPC handle is stale");
        Check.True(
            map.TryGetShadowNpcEntity(
                equivalentForger.ObjectId,
                out var retainedForgerEntity) &&
            retainedForgerEntity == forgerEntity,
            "unchanged NPC keeps its stable entity handle");
        Check.Equal(
            relocatedMentor.X,
            map.SnapshotEcsShadow().Npcs
                .Single(npc =>
                    npc.Npc.Identity.ObjectId == relocatedMentor.ObjectId)
                .Npc.Transform.X,
            "relocated NPC transform is hydrated");

        map.ObserveNpcDefinitions([relocatedMentor]);
        Check.True(
            !map.IsShadowEntityAlive(forgerEntity),
            "removed authoritative NPC handle is stale");
        Check.Equal(
            1,
            map.SnapshotEcsShadow().Npcs.Count,
            "authoritative NPC removal is mirrored");
        Check.True(
            map.DiagnoseEcsShadow().IsMatch,
            "NPC replacement/removal parity diagnostics");
    }

    private static GameSessionContext CreateContext(
        MapInstance map,
        ClientSession session,
        GameCharacter character,
        bool worldReady,
        long worldRevision) =>
        new(
            session,
            AccountId,
            CharacterId,
            character.Name,
            map.RealmId,
            map.WorldInstanceId,
            character.CurrentMap,
            PlayerObjectId,
            character,
            worldReady,
            worldRevision);

    private static GameCharacter CreateCharacter(
        byte mapId,
        float x,
        float z) =>
        new()
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "MapEcsHero",
            CreatedUtc =
                new DateTime(2026, 7, 23, 1, 2, 3, DateTimeKind.Utc),
            Camp = mapId == 0
                ? GameDefaults.SpartaCamp
                : GameDefaults.AthensCamp,
            CurrentMap = mapId,
            PositionX = x,
            PositionZ = z,
            Level = 20,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };
}
