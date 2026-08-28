using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Monsters;
using Godswar.Server.World.Systems.Monsters;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterEcsParityChecks
{
    private static void CheckNeverRespawnParity()
    {
        CheckNeverRespawnDeathParity();
        CheckNeverRespawnLeashParity();
        CheckNeverRespawnConfigurationRejections();
    }

    private static void CheckNeverRespawnDeathParity()
    {
        var definition = CreateMonster(10013, 100f, 50f);
        var legacy = new MonsterMapRuntime(
            0,
            [definition],
            Start,
            respawnPolicy: MonsterRespawnPolicy.Never);
        var ecs = new EcsMonsterMapRuntime(
            0,
            [definition],
            Start,
            respawnPolicy: MonsterRespawnPolicy.Never);

        var entity = ecs.World.Query<MonsterLifecycleComponent>().Single();
        ref var lifecycle =
            ref ecs.World.Get<MonsterLifecycleComponent>(entity);
        Check.True(
            lifecycle.RespawnPolicy == MonsterRespawnPolicy.Never &&
            lifecycle.RespawnDelay is null,
            "never-respawn ECS hydration has no latent timer");

        Check.True(
            legacy.TryApplyDamage(
                definition.ObjectId,
                uint.MaxValue,
                attackerCharacterId: 731,
                now: Start,
                out var legacyDeath),
            "legacy never-respawn lethal damage accepted");
        Check.True(
            ecs.TryApplyDamage(
                definition.ObjectId,
                uint.MaxValue,
                attackerCharacterId: 731,
                now: Start,
                out var ecsDeath),
            "ECS never-respawn lethal damage accepted");
        Check.True(
            legacyDeath == ecsDeath &&
            legacyDeath.Killed &&
            legacyDeath.Monster.RespawnAt is null &&
            legacyDeath.Monster.SpawnGeneration == 1,
            "never-respawn death has parity and no scheduled replacement");

        var deathTick = legacy.Advance(Start);
        var ecsDeathTick = ecs.Advance(Start);
        AssertTickEqual(deathTick, ecsDeathTick, "never-respawn death publication");
        Check.True(
            deathTick.Updates.Select(update => update.Kind)
                .SequenceEqual([MonsterRuntimeUpdateKind.Died]),
            "never-respawn death still publishes Died");

        var despawnAt = Start + MonsterMapRuntime.DefaultCorpseDespawnDelay;
        var despawnTick = legacy.Advance(despawnAt);
        var ecsDespawnTick = ecs.Advance(despawnAt);
        AssertTickEqual(
            despawnTick,
            ecsDespawnTick,
            "never-respawn corpse despawn");
        Check.True(
            despawnTick.Updates.Select(update => update.Kind)
                .SequenceEqual([MonsterRuntimeUpdateKind.Despawned]),
            "never-respawn corpse still publishes Despawned");

        var farFuture = Start + TimeSpan.FromDays(365);
        var legacyFuture = legacy.Advance(farFuture);
        var ecsFuture = ecs.Advance(farFuture);
        AssertTickEqual(
            legacyFuture,
            ecsFuture,
            "never-respawn indefinite absence");
        Check.Equal(
            0,
            legacyFuture.Updates.Count,
            "never-respawn death emits no future lifecycle update");
        AssertSnapshotsEqual(legacy, ecs, "never-respawn final death state");
        var snapshot = legacy.Snapshot().Single();
        Check.True(
            !snapshot.IsAlive &&
            !snapshot.IsSpawned &&
            snapshot.RespawnAt is null &&
            snapshot.SpawnGeneration == 1,
            "never-respawn monster remains absent in generation one");
    }

    private static void CheckNeverRespawnLeashParity()
    {
        var definition = CreateMonster(10013, 100f, 50f);
        var legacy = new MonsterMapRuntime(
            0,
            [definition],
            Start,
            respawnPolicy: MonsterRespawnPolicy.Never);
        var ecs = new EcsMonsterMapRuntime(
            0,
            [definition],
            Start,
            respawnPolicy: MonsterRespawnPolicy.Never);
        var target = new MonsterCombatTarget(
            CharacterId: 731,
            X: definition.X + 12f,
            Z: definition.Z,
            IsAlive: true);

        legacy.TryApplyDamage(
            definition.ObjectId,
            damage: 10,
            attackerCharacterId: target.CharacterId,
            now: Start,
            out _);
        ecs.TryApplyDamage(
            definition.ObjectId,
            damage: 10,
            attackerCharacterId: target.CharacterId,
            now: Start,
            out _);
        AssertTickEqual(
            legacy.Advance(Start, [target]),
            ecs.Advance(Start, [target]),
            "never-respawn chase start");

        var now = Start + MonsterMapRuntime.TickInterval;
        AssertTickEqual(
            legacy.Advance(now, [target]),
            ecs.Advance(now, [target]),
            "never-respawn chase movement");
        legacy.ClearAggroForCharacter(target.CharacterId, now);
        ecs.ClearAggroForCharacter(target.CharacterId, now);
        AssertTickEqual(
            legacy.Advance(now, [target]),
            ecs.Advance(now, [target]),
            "never-respawn leash return start");

        MonsterRuntimeTick? returnedTick = null;
        for (var tickIndex = 0; tickIndex < 100; tickIndex++)
        {
            now += MonsterMapRuntime.TickInterval;
            var legacyTick = legacy.Advance(now, [target]);
            var ecsTick = ecs.Advance(now, [target]);
            AssertTickEqual(
                legacyTick,
                ecsTick,
                $"never-respawn leash return {tickIndex}");
            if (legacyTick.Updates.Any(update =>
                    update.Kind == MonsterRuntimeUpdateKind.Returned))
            {
                returnedTick = legacyTick;
                break;
            }
        }

        Check.True(returnedTick is not null, "never-respawn monster returns home");
        Check.True(
            returnedTick!.Updates.All(update =>
                update.Kind is not (
                    MonsterRuntimeUpdateKind.Despawned or
                    MonsterRuntimeUpdateKind.Respawned)),
            "never-respawn leash return does not retire the monster");

        now += MonsterMapRuntime.TickInterval;
        var settledLegacy = legacy.Advance(now, [target]);
        var settledEcs = ecs.Advance(now, [target]);
        AssertTickEqual(
            settledLegacy,
            settledEcs,
            "never-respawn same-generation leash settlement");
        AssertSnapshotsEqual(legacy, ecs, "never-respawn settled leash state");
        var settled = legacy.Snapshot().Single();
        Check.True(
            settled.IsAlive &&
            settled.IsSpawned &&
            settled.SpawnGeneration == 1 &&
            settled.CombatPhase == MonsterCombatPhase.None &&
            settled.RespawnAt is null,
            "never-respawn leash settles the original living generation");
    }

    private static void CheckNeverRespawnConfigurationRejections()
    {
        var definition = CreateMonster(10013, 100f, 50f);
        var invalid = (MonsterRespawnPolicy)99;
        Check.Throws<ArgumentOutOfRangeException>(
            () => new MonsterMapRuntime(
                0,
                [definition],
                Start,
                respawnPolicy: invalid),
            "legacy rejects an unknown respawn policy");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new EcsMonsterMapRuntime(
                0,
                [definition],
                Start,
                respawnPolicy: invalid),
            "ECS rejects an unknown respawn policy");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MonsterMapRuntimeFactory.Create(
                MonsterRuntimeMode.Legacy,
                0,
                [definition],
                Start,
                respawnPolicy: invalid),
            "monster runtime factory rejects an unknown respawn policy");

        Check.Throws<ArgumentException>(
            () => new MonsterMapRuntime(
                0,
                [definition],
                Start,
                respawnDelay: TimeSpan.FromSeconds(20),
                respawnPolicy: MonsterRespawnPolicy.Never),
            "legacy rejects a Never policy combined with a timer");
        Check.Throws<ArgumentException>(
            () => new EcsMonsterMapRuntime(
                0,
                [definition],
                Start,
                respawnDelay: TimeSpan.FromSeconds(20),
                respawnPolicy: MonsterRespawnPolicy.Never),
            "ECS rejects a Never policy combined with a timer");

        var persisted = new WorldBossRespawnState(
            0,
            definition.TemplateKey,
            Start + TimeSpan.FromHours(1));
        Check.Throws<ArgumentException>(
            () => new MonsterMapRuntime(
                0,
                [definition],
                Start,
                activeWorldBossRespawn: persisted,
                respawnPolicy: MonsterRespawnPolicy.Never),
            "legacy rejects persisted timed world-boss state under Never");
        Check.Throws<ArgumentException>(
            () => new EcsMonsterMapRuntime(
                0,
                [definition],
                Start,
                activeWorldBossRespawn: persisted,
                respawnPolicy: MonsterRespawnPolicy.Never),
            "ECS rejects persisted timed world-boss state under Never");

        var worldBossCatalog = WorldBossCatalog.Create(
            [new WorldBossDefinition(
                0,
                "Sparta",
                definition.TemplateKey,
                "Test World Boss",
                RespawnInterval: TimeSpan.FromHours(12))],
            TimeSpan.FromHours(12));
        Check.Throws<ArgumentException>(
            () => new MonsterMapRuntime(
                0,
                [definition],
                Start,
                worldBossCatalog: worldBossCatalog,
                respawnPolicy: MonsterRespawnPolicy.Never),
            "legacy preserves configured world bosses as timed-only");
        Check.Throws<ArgumentException>(
            () => new EcsMonsterMapRuntime(
                0,
                [definition],
                Start,
                worldBossCatalog: worldBossCatalog,
                respawnPolicy: MonsterRespawnPolicy.Never),
            "ECS preserves configured world bosses as timed-only");
    }
}
