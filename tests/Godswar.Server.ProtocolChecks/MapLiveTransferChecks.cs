using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MapLiveTransferChecks
{
    private const int AccountId = 121;
    private const int CharacterId = 1_231;
    private const uint PlayerObjectId = 0x6801;
    private const uint MonsterObjectId = 10_902;
    private const byte SourceMapId = 0;
    private const byte TargetMapId = 4;
    private const float SourceX = 205f;
    private const float SourceZ = -120f;
    private const float TargetX = 97.98f;
    private const float TargetZ = -222.85f;

    private static readonly DateTimeOffset TestTime =
        new(2026, 7, 27, 4, 5, 6, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await CheckHiddenTransferAndActivationAsync();
        await CheckDestinationFaultRollbackAsync();
        await CheckLegacyDestinationFaultPreservesSourceStateAsync();
    }

    private static async Task CheckHiddenTransferAndActivationAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = CreateRegistry();
        var character = CreateCharacter();
        registry.JoinMap(
            socket.Session,
            AccountId,
            character,
            PlayerObjectId,
            worldReady: true,
            joinedAt: TestTime);
        var source = registry.GetMapSessions(SourceMapId).Single();

        Check.True(
            registry.TryTransferMap(
                socket.Session,
                SourceMapId,
                TargetMapId,
                TargetX,
                TargetZ),
            "live map transfer stages successfully");
        Check.Equal(
            TargetMapId,
            character.CurrentMap,
            "map transfer updates the authoritative character map");
        Check.Equal(
            TargetX,
            character.PositionX,
            "map transfer updates the authoritative character X");
        Check.Equal(
            TargetZ,
            character.PositionZ,
            "map transfer updates the authoritative character Z");

        Check.Equal(
            0,
            registry.GetMapPopulation(SourceMapId),
            "map transfer removes source ECS ownership");
        Check.Equal(
            0,
            registry.GetMapSessions(SourceMapId).Count,
            "map transfer removes source world visibility");
        Check.Equal(
            1,
            registry.GetMapPopulation(TargetMapId),
            "hidden destination remains owned by its ECS map");
        Check.Equal(
            0,
            registry.GetMapSessions(TargetMapId).Count,
            "hidden destination is excluded from world readers");
        Check.True(
            !registry.TryGetMapSessionByObjectId(
                TargetMapId,
                PlayerObjectId,
                excludeSession: null,
                out _),
            "hidden destination is excluded from object lookup");

        Check.True(
            registry.TryMarkWorldReady(
                socket.Session,
                new Dictionary<uint, long>(),
                out var unseenPlayers,
                TestTime.AddSeconds(1)),
            "destination activates after hydration");
        Check.Equal(
            0,
            unseenPlayers.Count,
            "empty destination has no prerequisite player snapshots");
        var active = registry.GetMapSessions(TargetMapId).Single();
        Check.True(
            active.WorldReady &&
            active.WorldRevision == source.WorldRevision + 1,
            "activation preserves the advanced destination generation");
        Check.True(
            registry.TryGetMapSessionByObjectId(
                TargetMapId,
                PlayerObjectId,
                excludeSession: null,
                out var activeByObject) &&
            ReferenceEquals(active, activeByObject),
            "activated destination publishes object lookup");

        Check.True(
            !registry.TryTransferMap(
                socket.Session,
                expectedSourceMapId: SourceMapId,
                targetMapId: 5,
                targetX: 10f,
                targetZ: 20f),
            "stale source generation cannot transfer the player");
        Check.Equal(
            TargetMapId,
            character.CurrentMap,
            "rejected stale-source transfer preserves the active map");
        Check.Equal(
            1,
            registry.GetMapPopulation(TargetMapId),
            "rejected stale-source transfer preserves ECS ownership");
        Check.True(
            !registry.TryTransferMap(
                socket.Session,
                expectedSourceMapId: TargetMapId,
                targetMapId: 199,
                targetX: 0f,
                targetZ: 0f),
            "unknown runtime map cannot become authoritative");
        Check.True(
            !registry.TryTransferMap(
                socket.Session,
                expectedSourceMapId: TargetMapId,
                targetMapId: 5,
                targetX:
                    MapTraversalLimits.MaximumCoordinateMagnitude + 1f,
                targetZ: 0f),
            "out-of-world destination cannot become authoritative");
        Check.Equal(
            0,
            socket.Available,
            "registry transfer emits no transport packets");

        registry.Remove(socket.Session);
    }

    private static async Task CheckDestinationFaultRollbackAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = CreateRegistry();
        await registry.PublishMapNpcDefinitionsAsync(
            TargetMapId,
            [CreateCollidingNpc()],
            originSession: null,
            CancellationToken.None);
        var character = CreateCharacter();
        registry.JoinMap(
            socket.Session,
            AccountId,
            character,
            PlayerObjectId,
            worldReady: true,
            joinedAt: TestTime);

        Check.Throws<InvalidOperationException>(
            () => registry.TryTransferMap(
                socket.Session,
                SourceMapId,
                TargetMapId,
                TargetX,
                TargetZ),
            "destination object collision rejects map transfer");

        Check.Equal(
            SourceMapId,
            character.CurrentMap,
            "failed transfer restores the authoritative character map");
        Check.Equal(
            SourceX,
            character.PositionX,
            "failed transfer restores the authoritative character X");
        Check.Equal(
            SourceZ,
            character.PositionZ,
            "failed transfer restores the authoritative character Z");
        Check.Equal(
            1,
            registry.GetMapPopulation(SourceMapId),
            "failed transfer retains source ECS ownership");
        Check.Equal(
            1,
            registry.GetMapSessions(SourceMapId).Count,
            "failed transfer retains source world visibility");
        Check.Equal(
            0,
            registry.GetMapPopulation(TargetMapId),
            "failed transfer leaves destination player-free");
        Check.Equal(
            0,
            registry.GetMapSessions(TargetMapId).Count,
            "failed transfer publishes no destination player");
        Check.True(
            registry.TryGetMapSessionByObjectId(
                SourceMapId,
                PlayerObjectId,
                excludeSession: null,
                out var retained) &&
            retained.WorldReady,
            "failed transfer preserves source object lookup");
        Check.True(
            !registry.TryGetMapSessionByObjectId(
                TargetMapId,
                PlayerObjectId,
                excludeSession: null,
                out _),
            "failed transfer publishes no destination object lookup");
        Check.Equal(
            0,
            socket.Available,
            "failed registry transfer emits no transport packets");

        registry.Remove(socket.Session);
    }

    private static async Task
        CheckLegacyDestinationFaultPreservesSourceStateAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = CreateRegistry(PlayerRuntimeMode.Legacy);
        registry.InitializeMapMonsters(
            SourceMapId,
            [CreateMonster()],
            TestTime);
        await registry.PublishMapNpcDefinitionsAsync(
            TargetMapId,
            [CreateCollidingNpc()],
            originSession: null,
            CancellationToken.None);
        var character = CreateCharacter();
        registry.JoinMap(
            socket.Session,
            AccountId,
            character,
            PlayerObjectId,
            worldReady: true,
            joinedAt: TestTime);

        Check.True(
            registry.TryApplyMonsterDamage(
                SourceMapId,
                MonsterObjectId,
                damage: 1,
                attackerCharacterId: character.Id,
                now: TestTime,
                out _),
            "legacy rollback fixture establishes monster aggro");
        await using (var visibility = await registry
                         .BeginMonsterVisibilityTransitionAsync(
                             socket.Session,
                             SourceMapId,
                             character.PositionX,
                             character.PositionZ,
                             CancellationToken.None))
        {
            Check.True(
                visibility is not null,
                "legacy rollback fixture opens monster visibility");
            Check.Equal(
                1,
                visibility!.Delta.Entering.Count,
                "legacy rollback fixture sees its source monster");
            visibility.Commit();
        }

        var before = registry
            .GetMapMonsterSnapshots(SourceMapId)
            .Single();
        Check.True(
            registry.IsMonsterVisibleTo(
                socket.Session,
                MonsterObjectId),
            "legacy rollback fixture commits source viewer state");

        Check.Throws<InvalidOperationException>(
            () => registry.TryTransferMap(
                socket.Session,
                SourceMapId,
                TargetMapId,
                TargetX,
                TargetZ),
            "legacy destination collision rejects map transfer");

        Check.Equal(
            SourceMapId,
            character.CurrentMap,
            "legacy failure preserves authoritative source map");
        Check.Equal(
            SourceX,
            character.PositionX,
            "legacy failure preserves authoritative source X");
        Check.Equal(
            SourceZ,
            character.PositionZ,
            "legacy failure preserves authoritative source Z");
        Check.Equal(
            1,
            registry.GetMapPopulation(SourceMapId),
            "legacy failure retains source population");
        Check.Equal(
            0,
            registry.GetMapPopulation(TargetMapId),
            "legacy failure leaves destination player-free");
        Check.True(
            registry.IsMonsterVisibleTo(
                socket.Session,
                MonsterObjectId),
            "legacy failure preserves source monster viewer state");

        await using (var afterFailure = await registry
                         .BeginMonsterVisibilityTransitionAsync(
                             socket.Session,
                             SourceMapId,
                             character.PositionX,
                             character.PositionZ,
                             CancellationToken.None))
        {
            Check.True(
                afterFailure is not null,
                "legacy failure keeps source visibility operational");
            Check.Equal(
                0,
                afterFailure!.Delta.Entering.Count,
                "legacy failure does not duplicate monster entry");
            Check.Equal(
                0,
                afterFailure.Delta.Leaving.Count,
                "legacy failure does not emit a false monster exit");
            afterFailure.Commit();
        }

        var after = registry
            .GetMapMonsterSnapshots(SourceMapId)
            .Single();
        Check.Equal(
            before.SpawnGeneration,
            after.SpawnGeneration,
            "legacy failure preserves monster spawn generation");
        Check.Equal(
            before.HealthRevision,
            after.HealthRevision,
            "legacy failure preserves monster health revision");
        Check.Equal(
            before.CurrentHealth,
            after.CurrentHealth,
            "legacy failure preserves monster health");
        Check.True(
            after.CombatPhase == before.CombatPhase,
            "legacy failure preserves monster aggro phase");

        await registry.AdvanceMonsterWorldOnceAsync(
            TestTime,
            CancellationToken.None);
        Check.True(
            registry.GetMapMonsterSnapshots(SourceMapId)
                .Single()
                .CombatPhase == MonsterCombatPhase.Attacking,
            "legacy failure retains aggro target after rollback");

        await registry.PublishMapNpcDefinitionsAsync(
            TargetMapId,
            [],
            originSession: null,
            CancellationToken.None);
        Check.True(
            registry.TryTransferMap(
                socket.Session,
                SourceMapId,
                TargetMapId,
                TargetX,
                TargetZ),
            "legacy transfer succeeds after destination conflict clears");
        Check.Equal(
            0,
            registry.GetMapPopulation(SourceMapId),
            "successful legacy transfer removes source membership");
        Check.Equal(
            1,
            registry.GetMapPopulation(TargetMapId),
            "successful legacy transfer commits destination membership");
        Check.Equal(
            0,
            registry.GetMapSessions(TargetMapId).Count,
            "successful legacy transfer remains hidden until hydration");
        Check.True(
            registry.TryMarkWorldReady(
                socket.Session,
                new Dictionary<uint, long>(),
                out var unseenPlayers,
                TestTime.AddSeconds(1)),
            "successful legacy transfer activates after hydration");
        Check.Equal(
            0,
            unseenPlayers.Count,
            "successful legacy destination has no unseen peers");
        Check.Equal(
            1,
            registry.GetMapSessions(TargetMapId).Count,
            "activated legacy destination becomes visible");
        registry.Remove(socket.Session);
    }

    private static GameSessionRegistry CreateRegistry(
        PlayerRuntimeMode playerRuntimeMode = PlayerRuntimeMode.Ecs) =>
        new(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode,
            gameplayCatalogs: GameplayContentTestFixtures.Runtime);

    private static GameCharacter CreateCharacter() =>
        new()
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "LiveMapTransferHero",
            CreatedUtc = TestTime.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = SourceMapId,
            PositionX = SourceX,
            PositionZ = SourceZ,
            Level = 20,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

    private static NpcSpawnDefinition CreateCollidingNpc() =>
        new(
            TargetMapId,
            "Sparta_Newbie",
            "Sparta_Newbie_TransferCollision",
            "Sparta_Newbie_TransferCollision_Male1",
            PlayerObjectId,
            TargetX,
            TargetZ,
            PlayerObjectId,
            NpcSpawnDefinitionFactory.DefaultAppearanceType,
            NpcSpawnDefinitionFactory.DefaultFacing,
            [],
            []);

    private static CapturedMonsterSpawn CreateMonster()
    {
        const string templateKey = "LegacyTransferRollbackMonster";
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
            MonsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            237);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            237);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28, 4),
            SourceX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32, 4),
            2f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36, 4),
            SourceZ);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            SourceMapId,
            "Sparta",
            templateKey,
            templateKey,
            MonsterObjectId,
            SourceX,
            SourceZ,
            packet);
    }
}
