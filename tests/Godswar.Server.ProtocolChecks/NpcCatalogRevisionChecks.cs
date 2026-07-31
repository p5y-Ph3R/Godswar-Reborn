using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class NpcCatalogRevisionChecks
{
    private const byte MapId = 0;
    private const uint PlayerObjectId = 0x6501;

    private static readonly DateTimeOffset TestTime =
        new(2026, 7, 23, 5, 6, 7, TimeSpan.Zero);

    private static readonly MethodInfo InstallCatalogMethod =
        RequiredMethod("InstallNpcCatalog");
    private static readonly MethodInfo StartUpdatesMethod =
        RequiredMethod("StartNpcCatalogUpdates");
    private static readonly MethodInfo StopUpdatesMethod =
        RequiredMethod("StopNpcCatalogUpdatesAsync");
    private static readonly MethodInfo ResolveNpcMethod =
        RequiredMethod("TryResolveMapNpc");
    private static readonly FieldInfo CharacterField =
        RequiredField("_character");
    private static readonly FieldInfo VisibilityField =
        RequiredField("_npcVisibility");
    private static readonly FieldInfo CharacterStateGateField =
        RequiredField("_characterStateGate");
    private static readonly FieldInfo CatalogRevisionField =
        RequiredField("_npcCatalogRevision");

    public static async Task RunAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();

        await CheckOnlineRevisionUpdateAsync(socket);
        CheckMonsterCollisionRollback();
        CheckPlayerCollisionRollback(socket.Session);
    }

    private static async Task CheckOnlineRevisionUpdateAsync(
        RuntimePolicySessionSocket socket)
    {
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode: PlayerRuntimeMode.Ecs);
        var character = CreateCharacter(MapId);
        var removedNpc = CreateNpc(
            MapId,
            objectId: 8_101,
            interactionId: 9_101,
            facing: 1f);
        var replacedNpc = CreateNpc(
            MapId,
            objectId: 8_102,
            interactionId: 9_102,
            facing: 1f);
        var initial = await registry.PublishMapNpcDefinitionsAsync(
            MapId,
            [removedNpc, replacedNpc],
            originSession: null,
            CancellationToken.None);

        var handler = new GameClientHandler(
            socket.Session,
            new NoopStore(),
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty);
        CharacterField.SetValue(handler, character);
        InstallCatalogMethod.Invoke(handler, [initial]);
        var visibility =
            (WorldSectorVisibilityTracker<NpcSpawnDefinition>)
            (VisibilityField.GetValue(handler) ??
             throw new InvalidOperationException(
                 "Initial NPC visibility tracker was not installed."));
        Check.True(
            visibility.TryCalculate(
                character.PositionX,
                character.PositionZ,
                out var initialDelta),
            "initial NPC catalog visibility is calculable");
        visibility.Commit(initialDelta);
        StartUpdatesMethod.Invoke(handler, null);

        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            PlayerObjectId,
            worldReady: true,
            joinedAt: TestTime);
        try
        {
            Check.True(
                TryResolveNpc(
                    handler,
                    removedNpc.InteractionId,
                    out _),
                "initial visible NPC resolves before replacement");

            var replacement = replacedNpc with
            {
                InteractionId = 9_202,
                Facing = 2f
            };
            var characterStateGate =
                (SemaphoreSlim)
                (CharacterStateGateField.GetValue(handler) ??
                 throw new InvalidOperationException(
                     "Character state gate was not found."));
            await characterStateGate.WaitAsync();
            Task<MapNpcCatalogSnapshot> publicationTask;
            try
            {
                publicationTask =
                    registry.PublishMapNpcDefinitionsAsync(
                        MapId,
                        [replacement],
                        originSession: null,
                        CancellationToken.None);
                await WaitUntilAsync(
                    () => registry.IsCanonicalMapNpcCatalog(
                        MapId,
                        initial.Revision + 1),
                    "replacement catalog commit");

                Check.True(
                    !TryResolveNpc(
                        handler,
                        removedNpc.InteractionId,
                        out _),
                    "removed NPC fails closed before callback acquires handler gate");
                Check.True(
                    !TryResolveNpc(
                        handler,
                        replacedNpc.InteractionId,
                        out _),
                    "replaced NPC definition fails closed before local cache update");
            }
            finally
            {
                characterStateGate.Release();
            }

            var published = await publicationTask;
            var expectedRemoval = PacketBuilder.RemoveWorldObjects(
                removedNpc.ObjectId,
                replacedNpc.ObjectId);
            var actualRemoval = await socket.ReadPacketAsync(
                expectedRemoval.Length);
            Check.True(
                expectedRemoval.SequenceEqual(actualRemoval),
                "catalog callback removes stale objects before spawning replacements");

            var expectedSpawn = PacketBuilder.NpcSpawns([replacement]);
            var actualSpawn = await socket.ReadPacketAsync(
                expectedSpawn.Length);
            Check.True(
                expectedSpawn.SequenceEqual(actualSpawn),
                "catalog callback sends the replacement NPC after removals");
            Check.Equal(
                published.Revision,
                (long)(CatalogRevisionField.GetValue(handler) ?? -1L),
                "online handler stores the canonical NPC revision");
            Check.True(
                !TryResolveNpc(
                    handler,
                    removedNpc.InteractionId,
                    out _),
                "removed NPC interaction is absent after callback");
            Check.True(
                !TryResolveNpc(
                    handler,
                    replacedNpc.InteractionId,
                    out _),
                "old replacement interaction is absent after callback");
            Check.True(
                TryResolveNpc(
                    handler,
                    replacement.InteractionId,
                    out var resolved) &&
                NpcCatalogDefinitions.Equals(replacement, resolved),
                "new replacement interaction resolves canonical definition");
            Check.Equal(
                0,
                socket.Available,
                "NPC revision callback emits no unexpected packets");
        }
        finally
        {
            registry.Remove(socket.Session);
            var stopTask = StopUpdatesMethod.Invoke(handler, null) as Task
                ?? throw new InvalidOperationException(
                    "StopNpcCatalogUpdatesAsync returned no task.");
            await stopTask;
        }
    }

    private static void CheckMonsterCollisionRollback()
    {
        const uint monsterObjectId = 8_201;
        var map = new MapInstance(
            MapId,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        var monster = CreateMonster(
            MapId,
            monsterObjectId,
            "CollisionMonster");
        map.InitializeMonsters([monster], TestTime);
        var retainedNpc = CreateNpc(
            MapId,
            objectId: 8_202,
            interactionId: 9_202,
            facing: 1f);
        var initial = map.PublishNpcDefinitions([retainedNpc]).Snapshot;
        Check.True(
            map.TryGetShadowNpcEntity(
                retainedNpc.ObjectId,
                out var retainedEntity),
            "initial NPC entity exists before collision");
        var monsterBefore = map.SnapshotMonsters().Single();

        var collidingNpc = CreateNpc(
            MapId,
            monsterObjectId,
            interactionId: 9_201,
            facing: 1f);
        Check.Throws<InvalidOperationException>(
            () => map.PublishNpcDefinitions(
                [retainedNpc, collidingNpc]),
            "later NPC replacement rejects live monster object ID");

        var retained = map.SnapshotNpcCatalog();
        Check.Equal(
            initial.Revision,
            retained.Revision,
            "failed NPC replacement preserves catalog revision");
        Check.True(
            NpcCatalogDefinitions.SetEquals(
                initial.Definitions,
                retained.Definitions),
            "failed NPC replacement preserves canonical definitions");
        Check.True(
            map.TryGetShadowNpcEntity(
                retainedNpc.ObjectId,
                out var currentEntity) &&
            currentEntity == retainedEntity &&
            map.IsShadowEntityAlive(retainedEntity),
            "failed NPC replacement preserves prior ECS entity");
        var monsterAfter = map.SnapshotMonsters().Single();
        AssertMonsterUnchanged(
            monsterBefore,
            monsterAfter,
            "failed NPC replacement");

        const uint npcObjectId = 8_301;
        var inverseMap = new MapInstance(
            MapId,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        var canonicalNpc = CreateNpc(
            MapId,
            npcObjectId,
            interactionId: 9_301,
            facing: 1f);
        var inverseCatalog =
            inverseMap.PublishNpcDefinitions([canonicalNpc]).Snapshot;
        Check.Throws<InvalidOperationException>(
            () => inverseMap.InitializeMonsters(
                [CreateMonster(
                    MapId,
                    npcObjectId,
                    "InverseCollisionMonster")],
                TestTime),
            "initial monster runtime rejects canonical NPC object ID");
        Check.Equal(
            0,
            inverseMap.SnapshotMonsters().Count,
            "failed monster initialization publishes no partial runtime");
        Check.Equal(
            inverseCatalog.Revision,
            inverseMap.SnapshotNpcCatalog().Revision,
            "failed monster initialization preserves NPC catalog");

        var safeRuntime = inverseMap.InitializeMonsters(
            [CreateMonster(
                MapId,
                objectId: 8_302,
                templateKey: "SafeMonster")],
            TestTime);
        Check.Equal(
            1,
            safeRuntime.Count,
            "collision failure does not poison later safe runtime initialization");
    }

    private static void CheckPlayerCollisionRollback(
        ClientSession session)
    {
        const byte mapId = 3;
        const uint npcObjectId = 8_401;
        const uint playerObjectId = 8_402;
        var map = new MapInstance(
            mapId,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Legacy);
        var npc = CreateNpc(
            mapId,
            npcObjectId,
            interactionId: 9_401,
            facing: 1f);
        var initial = map.PublishNpcDefinitions([npc]).Snapshot;
        Check.True(
            map.TryGetShadowNpcEntity(
                npcObjectId,
                out var retainedEntity),
            "legacy-mode canonical NPC entity exists");
        var character = CreateCharacter(mapId);
        var context = new GameSessionContext(
            session,
            character.AccountId,
            character.Id,
            character.Name,
            map.RealmId,
            map.WorldInstanceId,
            mapId,
            playerObjectId,
            character,
            WorldReady: true,
            WorldRevision: 0);
        map.AddOrUpdate(context);

        var playerCollision = CreateNpc(
            mapId,
            playerObjectId,
            interactionId: 9_402,
            facing: 1f);
        Check.Throws<InvalidOperationException>(
            () => map.PublishNpcDefinitions(
                [npc, playerCollision]),
            "canonical publication rejects live transport player in legacy mode");
        var retained = map.SnapshotNpcCatalog();
        Check.Equal(
            initial.Revision,
            retained.Revision,
            "player collision preserves NPC catalog revision");
        Check.True(
            NpcCatalogDefinitions.SetEquals(
                initial.Definitions,
                retained.Definitions) &&
            map.TryGetShadowNpcEntity(
                npcObjectId,
                out var currentEntity) &&
            currentEntity == retainedEntity,
            "player collision preserves prior NPC set and entity");

        Check.Throws<InvalidOperationException>(
            () => map.AddOrUpdate(
                context with { ObjectId = npcObjectId }),
            "legacy transport player cannot claim canonical NPC object ID");
        Check.Equal(
            playerObjectId,
            map.Snapshot().Single().ObjectId,
            "failed player update preserves prior transport membership");
        map.Remove(session, out _);
    }

    private static bool TryResolveNpc(
        GameClientHandler handler,
        uint interactionId,
        out NpcSpawnDefinition npc)
    {
        object?[] arguments = [interactionId, null];
        var resolved = (bool)
            (ResolveNpcMethod.Invoke(handler, arguments) ?? false);
        npc = resolved
            ? (NpcSpawnDefinition)arguments[1]!
            : default!;
        return resolved;
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        string description)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timeout.Elapsed >= TimeSpan.FromSeconds(5))
            {
                throw new InvalidOperationException(
                    $"Timed out waiting for {description}.");
            }

            await Task.Delay(1);
        }
    }

    private static void AssertMonsterUnchanged(
        MonsterRuntimeSnapshot expected,
        MonsterRuntimeSnapshot actual,
        string description)
    {
        Check.Equal(
            expected.ObjectId,
            actual.ObjectId,
            $"{description} monster object ID");
        Check.Equal(
            expected.CurrentHealth,
            actual.CurrentHealth,
            $"{description} monster health");
        Check.Equal(
            expected.SpawnGeneration,
            actual.SpawnGeneration,
            $"{description} monster generation");
        Check.Equal(
            expected.HealthRevision,
            actual.HealthRevision,
            $"{description} monster health revision");
    }

    private static NpcSpawnDefinition CreateNpc(
        byte mapId,
        uint objectId,
        uint interactionId,
        float facing) =>
        new(
            mapId,
            $"Map{mapId}",
            $"Npc_{objectId}",
            $"Npc_{objectId}_Male1",
            objectId,
            X: 4f,
            Z: 5f,
            interactionId,
            NpcSpawnDefinitionFactory.DefaultAppearanceType,
            facing,
            [],
            []);

    private static CapturedMonsterSpawn CreateMonster(
        byte mapId,
        uint objectId,
        string templateKey)
    {
        const uint maximumHealth = 237;
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            maximumHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            maximumHealth);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28, 4),
            4f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36, 4),
            5f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            mapId,
            $"Map{mapId}",
            templateKey,
            templateKey,
            objectId,
            X: 4f,
            Z: 5f,
            packet);
    }

    private static GameCharacter CreateCharacter(byte mapId) =>
        new()
        {
            Id = 851,
            AccountId = 85,
            Name = "NpcCatalogHero",
            CreatedUtc = TestTime.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = mapId,
            PositionX = 4f,
            PositionZ = 5f,
            Level = 20,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

    private static MethodInfo RequiredMethod(string name) =>
        typeof(GameClientHandler).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

    private static FieldInfo RequiredField(string name) =>
        typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

    private sealed class NoopStore : GameStoreTestStub;
}
