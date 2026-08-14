using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterPlayerDamageEcsLiveAdapterChecks
{
    private static async Task CheckMonsterReboundParityAsync()
    {
        await CheckMonsterReboundAsync(PlayerRuntimeMode.Legacy);
        await CheckMonsterReboundAsync(PlayerRuntimeMode.Ecs);
        await CheckMonsterReboundLeaseRollbackAsync();
        await CheckMonsterReboundRewardSurvivesCancellationAsync();
    }

    private static async Task CheckMonsterReboundLeaseRollbackAsync()
    {
        const uint monsterObjectId = 9_107;
        const ulong combatEventId = 777;
        var activeAt = DateTimeOffset.UtcNow;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Legacy,
            gameplayCatalogs: CreateMonsterCombatCatalog(
                MonsterAttackDamageKind.Physical));
        var character = CreateCharacter();
        character.Id += 7;
        character.Name = "ReboundLeaseRollback";
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [CreateMonster(
                monsterObjectId,
                character.PositionX,
                character.PositionZ,
                tier: 100)],
            activeAt);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: activeAt);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var monster),
            "rebound lease rollback monster is queryable");

        var stale = monster with
        {
            SpawnGeneration = checked(monster.SpawnGeneration + 1)
        };
        var failed = registry.CommitMonsterReboundForSession(
            socket.Session,
            stale,
            combatEventId,
            appliedPlayerDamage: 10,
            requestedReboundDamage: 5);
        Check.True(
            !failed.Claimed && !failed.Applied,
            "stale monster mutation rolls back the rebound lease");

        var retry = registry.CommitMonsterReboundForSession(
            socket.Session,
            monster,
            combatEventId,
            appliedPlayerDamage: 10,
            requestedReboundDamage: 5);
        Check.True(
            retry.Claimed &&
            retry.DamageResult is { } applied &&
            applied.BeforeHealth - applied.AfterHealth == 5,
            "same committed event can retry rebound after stale failure");
        var replay = registry.CommitMonsterReboundForSession(
            socket.Session,
            monster,
            combatEventId,
            appliedPlayerDamage: 10,
            requestedReboundDamage: 5);
        Check.True(
            !replay.Claimed && !replay.Applied,
            "successful rebound retains its replay claim");
        registry.Remove(socket.Session);
    }

    private static async Task CheckMonsterReboundAsync(
        PlayerRuntimeMode playerRuntimeMode)
    {
        var activeAt = DateTimeOffset.UtcNow;
        var monsterObjectId = playerRuntimeMode == PlayerRuntimeMode.Ecs
            ? 9_106u
            : 9_105u;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            playerRuntimeMode,
            gameplayCatalogs: CreateMonsterCombatCatalog(
                MonsterAttackDamageKind.Physical));
        var character = CreateCharacter();
        character.Id += playerRuntimeMode == PlayerRuntimeMode.Ecs
            ? 6
            : 5;
        character.Name = $"Rebound{playerRuntimeMode}";
        character.CurrentHp = 10_000;
        character.MaxHp = 10_000;
        character.CalculatedStats = new CharacterStats
        {
            DamageRebound = 10_000,
            DamageReboundFlat = 7
        };
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [CreateMonster(
                monsterObjectId,
                character.PositionX,
                character.PositionZ,
                tier: 100)],
            activeAt);
        var playerObjectId = WorldObjectIds.ForPlayer(character.Id);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            playerObjectId,
            joinedAt: activeAt);
        await using (var visibility =
                     await registry.BeginMonsterVisibilityTransitionAsync(
                         socket.Session,
                         character.CurrentMap,
                         character.PositionX,
                         character.PositionZ,
                         CancellationToken.None)
                     ?? throw new InvalidOperationException(
                         "Rebound visibility transition was unavailable."))
        {
            visibility.Commit();
        }

        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var monsterBefore),
            $"{playerRuntimeMode} rebound monster is queryable");
        var profile = registry.GameplayCatalogs.MonsterCombatProfiles
            .Resolve(monsterBefore.Definition);
        var eventId = FindMonsterHitEventId(profile, character);
        MonsterDamageResult? preparedKill = null;
        var prepareCount = 0;
        var publishCount = 0;
        var queuedBytesAtPrepare = 0;
        var queuedBytesAtPublish = 0;
        registry.RegisterPveMonsterKillRewardPreparer(
            socket.Session,
            damageResult =>
            {
                prepareCount++;
                preparedKill = damageResult;
                queuedBytesAtPrepare = socket.Available;
                return Task.FromResult<PreparedPveMonsterKillReward?>(
                    new PreparedPveMonsterKillReward(_ =>
                    {
                        publishCount++;
                        queuedBytesAtPublish = socket.Available;
                        return Task.CompletedTask;
                    }));
            });

        var hpBefore = character.CurrentHp;
        await registry.ProcessMonsterAttackForSessionAsync(
            socket.Session,
            new MonsterRuntimeUpdate(
                MonsterRuntimeUpdateKind.Attacked,
                monsterBefore,
                TargetCharacterId: character.Id,
                TargetX: character.PositionX,
                TargetZ: character.PositionZ,
                TargetObjectId: playerObjectId,
                TargetLifeRevision:
                    registry.GetPlayerLifeRevision(socket.Session),
                TargetVitalsRevision: character.VitalsRevision,
                AttackEventId: eventId),
            CancellationToken.None);

        var appliedPlayerDamage = checked((uint)(
            hpBefore - character.CurrentHp));
        var expectedRebound = checked(appliedPlayerDamage + 7u);
        Check.True(
            appliedPlayerDamage > monsterBefore.CurrentHealth,
            $"{playerRuntimeMode} fixture produces terminal rebound");
        Check.True(
            preparedKill is { Killed: true } &&
            preparedKill.ObjectId == monsterObjectId &&
            prepareCount == 1 &&
            publishCount == 1 &&
            queuedBytesAtPrepare == 0 &&
            queuedBytesAtPublish >= 84,
            $"{playerRuntimeMode} rebound settles before all attack packets " +
            "and publishes reward packets afterward");
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var monsterAfter) &&
            !monsterAfter.IsAlive &&
            monsterAfter.CurrentHealth == 0 &&
            monsterAfter.HealthRevision ==
                monsterBefore.HealthRevision + 1,
            $"{playerRuntimeMode} terminal rebound mutates monster once");

        var impactPacket = await socket.ReadPacketAsync(24);
        var incomingPacket = await socket.ReadPacketAsync(30);
        var reboundPacket = await socket.ReadPacketAsync(30);
        Check.Equal((ushort)10046,
            BinaryPrimitives.ReadUInt16LittleEndian(
                impactPacket.AsSpan(2, 2)),
            $"{playerRuntimeMode} publishes incoming impact first");
        Check.Equal(monsterObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                incomingPacket.AsSpan(4, 4)),
            $"{playerRuntimeMode} publishes incoming damage second");
        Check.Equal(0x1448u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                reboundPacket.AsSpan(4, 4)),
            $"{playerRuntimeMode} rebound self source is authoritative");
        Check.Equal(monsterObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                reboundPacket.AsSpan(20, 4)),
            $"{playerRuntimeMode} rebound targets the attacking monster");
        Check.Equal(expectedRebound,
            BinaryPrimitives.ReadUInt32LittleEndian(
                reboundPacket.AsSpan(24, 4)),
            $"{playerRuntimeMode} rebound packet preserves derived damage");
        Check.Equal(
            (byte)CombatHitOutcome.Normal,
            reboundPacket[29],
            $"{playerRuntimeMode} rebound is terminal non-critical damage");

        registry.Remove(socket.Session);
    }

    private static async Task
        CheckMonsterReboundRewardSurvivesCancellationAsync()
    {
        const uint monsterObjectId = 9_108;
        var activeAt = DateTimeOffset.UtcNow;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Legacy,
            gameplayCatalogs: CreateMonsterCombatCatalog(
                MonsterAttackDamageKind.Physical));
        var character = CreateCharacter();
        character.Id += 8;
        character.Name = "ReboundCancellation";
        character.CurrentHp = 10_000;
        character.MaxHp = 10_000;
        character.CalculatedStats = new CharacterStats
        {
            DamageRebound = 10_000,
            DamageReboundFlat = 7
        };
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [CreateMonster(
                monsterObjectId,
                character.PositionX,
                character.PositionZ,
                tier: 100)],
            activeAt);
        var playerObjectId = WorldObjectIds.ForPlayer(character.Id);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            playerObjectId,
            joinedAt: activeAt);
        await using (var visibility =
                     await registry.BeginMonsterVisibilityTransitionAsync(
                         socket.Session,
                         character.CurrentMap,
                         character.PositionX,
                         character.PositionZ,
                         CancellationToken.None)
                     ?? throw new InvalidOperationException(
                         "Cancellation rebound visibility was unavailable."))
        {
            visibility.Commit();
        }

        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var monsterBefore),
            "cancellation rebound monster is queryable");
        var profile = registry.GameplayCatalogs.MonsterCombatProfiles
            .Resolve(monsterBefore.Definition);
        var eventId = FindMonsterHitEventId(profile, character);
        using var transportCancellation = new CancellationTokenSource();
        var prepareCount = 0;
        var publishCount = 0;
        registry.RegisterPveMonsterKillRewardPreparer(
            socket.Session,
            damageResult =>
            {
                Check.True(
                    damageResult.Killed,
                    "cancellation reward preparation sees terminal mutation");
                prepareCount++;
                transportCancellation.Cancel();
                return Task.FromResult<PreparedPveMonsterKillReward?>(
                    new PreparedPveMonsterKillReward(_ =>
                    {
                        publishCount++;
                        return Task.CompletedTask;
                    }));
            });

        var canceled = false;
        try
        {
            await registry.ProcessMonsterAttackForSessionAsync(
                socket.Session,
                new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Attacked,
                    monsterBefore,
                    TargetCharacterId: character.Id,
                    TargetX: character.PositionX,
                    TargetZ: character.PositionZ,
                    TargetObjectId: playerObjectId,
                    TargetLifeRevision:
                        registry.GetPlayerLifeRevision(socket.Session),
                    TargetVitalsRevision: character.VitalsRevision,
                    AttackEventId: eventId),
                transportCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Check.True(
            canceled &&
            prepareCount == 1 &&
            publishCount == 0 &&
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var monsterAfter) &&
            !monsterAfter.IsAlive,
            "terminal rebound reward is durably prepared before canceled " +
            "damage transport and is not packet-published out of order");
        registry.Remove(socket.Session);
    }

    private static ulong FindMonsterHitEventId(
        in MonsterCombatProfile profile,
        GameCharacter character)
    {
        for (ulong eventId = 1; eventId < 10_000; eventId++)
        {
            if (MonsterIncomingCombatPolicy.ResolveAttack(
                    profile,
                    character,
                    default,
                    eventId).Hit)
            {
                return eventId;
            }
        }

        throw new InvalidOperationException(
            "No deterministic monster hit was available for rebound.");
    }
}
