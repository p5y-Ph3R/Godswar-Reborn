using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Monsters;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterEcsParityChecks
{
    private static void CheckAggressiveProximityAndDamageThreatParity()
    {
        CheckMonsterAttackCastVisualLayout();
        CheckAggressiveProximityParity();
        CheckAggressiveClusterParity();
        CheckPatrolInterruptionOnProximityParity();
        CheckCumulativeDamageThreatParity();
    }

    private static void CheckAggressiveProximityParity()
    {
        var passive = CreateMonster(11001, 100f, 50f, tier: 29);
        var passiveTarget = Target(801, passive.X + 2f, passive.Z);
        var passiveLegacy = new MonsterMapRuntime(0, [passive], Start);
        var passiveEcs = new EcsMonsterMapRuntime(0, [passive], Start);
        AssertTickEqual(
            passiveLegacy.Advance(Start, [passiveTarget]),
            passiveEcs.Advance(Start, [passiveTarget]),
            "tier-29 passive proximity");
        var passiveTick = passiveLegacy.Advance(
            Start + MonsterMapRuntime.TickInterval,
            [passiveTarget]);
        var passiveEcsTick = passiveEcs.Advance(
            Start + MonsterMapRuntime.TickInterval,
            [passiveTarget]);
        AssertTickEqual(passiveTick, passiveEcsTick, "tier-29 passive tick");
        Check.True(
            passiveTick.Updates.All(update =>
                update.Kind != MonsterRuntimeUpdateKind.Attacked),
            "tier 29 does not proximity-aggro");

        var aggressive = CreateMonster(11002, 100f, 50f, tier: 30);
        var outside = Target(
            802,
            aggressive.X + MonsterAggroPolicy.DetectionRadius + 0.1f,
            aggressive.Z);
        var outsideLegacy = new MonsterMapRuntime(0, [aggressive], Start);
        var outsideEcs = new EcsMonsterMapRuntime(0, [aggressive], Start);
        var outsideLegacyTick = outsideLegacy.Advance(Start, [outside]);
        var outsideEcsTick = outsideEcs.Advance(Start, [outside]);
        AssertTickEqual(
            outsideLegacyTick,
            outsideEcsTick,
            "outside aggressive radius");
        Check.True(
            outsideLegacyTick.Updates.Count == 0,
            "tier 30 does not aggro beyond the presence radius");

        var inside = Target(
            803,
            aggressive.X + MonsterAggroPolicy.DetectionRadius - 1f,
            aggressive.Z);
        var insideLegacy = new MonsterMapRuntime(0, [aggressive], Start);
        var insideEcs = new EcsMonsterMapRuntime(0, [aggressive], Start);
        var insideLegacyTick = insideLegacy.Advance(Start, [inside]);
        var insideEcsTick = insideEcs.Advance(Start, [inside]);
        AssertTickEqual(
            insideLegacyTick,
            insideEcsTick,
            "inside aggressive radius");
        Check.True(
            insideLegacyTick.Updates.Any(update =>
                update.Kind == MonsterRuntimeUpdateKind.Started &&
                update.Monster.CombatPhase == MonsterCombatPhase.Chasing),
            "tier 30 starts chasing within the presence radius");

        var closest = Target(805, aggressive.X + 1f, aggressive.Z);
        var farther = Target(804, aggressive.X + 2f, aggressive.Z);
        var closestLegacy = new MonsterMapRuntime(0, [aggressive], Start);
        var closestEcs = new EcsMonsterMapRuntime(0, [aggressive], Start);
        AssertTickEqual(
            closestLegacy.Advance(Start, [closest, farther]),
            closestEcs.Advance(Start, [closest, farther]),
            "nearest aggressive target acquisition");
        var attackAt = Start + MonsterMapRuntime.TickInterval;
        var closestLegacyTick = closestLegacy.Advance(
            attackAt,
            [closest, farther]);
        var closestEcsTick = closestEcs.Advance(
            attackAt,
            [closest, farther]);
        AssertTickEqual(
            closestLegacyTick,
            closestEcsTick,
            "nearest aggressive target attack");
        AssertAttackTarget(
            closestLegacyTick,
            closest.CharacterId,
            "aggressive monster chooses the nearest target");
    }

    private static void CheckAggressiveClusterParity()
    {
        var target = Target(807, 100f, 50f);
        var offset = MonsterAggroPolicy.DetectionRadius - 2f;
        var definitions = new[]
        {
            CreateMonster(11005, 100f - offset, 50f, tier: 30),
            CreateMonster(11006, 100f, 50f + offset, tier: 30),
            CreateMonster(11007, 100f + offset, 50f, tier: 30)
        };
        var legacy = new MonsterMapRuntime(0, definitions, Start);
        var ecs = new EcsMonsterMapRuntime(0, definitions, Start);

        var legacyAcquired = legacy.Advance(Start, [target]);
        var ecsAcquired = ecs.Advance(Start, [target]);
        AssertTickEqual(
            legacyAcquired,
            ecsAcquired,
            "aggressive cluster acquisition");
        Check.Equal(
            3,
            legacyAcquired.Updates.Count(update =>
                update.Kind == MonsterRuntimeUpdateKind.Started &&
                update.Monster.CombatPhase == MonsterCombatPhase.Chasing),
            "every nearby aggressive monster starts chasing");

        var attacked = new HashSet<uint>();
        var now = Start;
        while (attacked.Count < definitions.Length &&
               now < Start + TimeSpan.FromSeconds(10))
        {
            now += MonsterMapRuntime.TickInterval;
            var tick = AdvancePair(
                legacy,
                ecs,
                now,
                [target],
                "aggressive cluster chase");
            foreach (var attack in tick.Updates.Where(update =>
                         update.Kind == MonsterRuntimeUpdateKind.Attacked))
            {
                attacked.Add(attack.Monster.ObjectId);
            }
        }
        Check.Equal(
            3,
            attacked.Count,
            "every nearby aggressive monster attacks independently");
    }

    private static void CheckCumulativeDamageThreatParity()
    {
        var definition = CreateMonster(11003, 100f, 50f);
        var legacy = new MonsterMapRuntime(0, [definition], Start);
        var ecs = new EcsMonsterMapRuntime(0, [definition], Start);
        var first = Target(811, definition.X + 1f, definition.Z);
        var second = Target(812, definition.X + 2f, definition.Z);
        var targets = new[] { first, second };

        ApplyDamagePair(
            legacy,
            ecs,
            definition.ObjectId,
            first.CharacterId,
            40,
            Start,
            "first threat hit");
        AssertTickEqual(
            legacy.Advance(Start, targets),
            ecs.Advance(Start, targets),
            "first threat target acquisition");
        var now = Start + MonsterMapRuntime.TickInterval;
        var firstAttack = AdvancePair(legacy, ecs, now, targets, "first threat attack");
        AssertAttackTarget(firstAttack, first.CharacterId, "first attacker has aggro");

        ApplyDamagePair(
            legacy,
            ecs,
            definition.ObjectId,
            second.CharacterId,
            30,
            now,
            "lower cumulative threat hit");
        now += MonsterMapRuntime.AttackCooldown;
        var lowerThreatAttack = AdvancePair(
            legacy,
            ecs,
            now,
            targets,
            "lower cumulative threat attack");
        AssertAttackTarget(
            lowerThreatAttack,
            first.CharacterId,
            "lower cumulative damage does not steal aggro");

        ApplyDamagePair(
            legacy,
            ecs,
            definition.ObjectId,
            second.CharacterId,
            10,
            now,
            "equal cumulative threat hit");
        now += MonsterMapRuntime.AttackCooldown;
        var tiedAttack = AdvancePair(
            legacy,
            ecs,
            now,
            targets,
            "equal cumulative threat attack");
        AssertAttackTarget(
            tiedAttack,
            first.CharacterId,
            "an exact damage tie preserves current aggro");

        ApplyDamagePair(
            legacy,
            ecs,
            definition.ObjectId,
            second.CharacterId,
            1,
            now,
            "higher cumulative threat hit");
        AssertTickEqual(
            legacy.Advance(now, targets),
            ecs.Advance(now, targets),
            "higher threat target acquisition");
        now += MonsterMapRuntime.TickInterval;
        var higherThreatAttack = AdvancePair(
            legacy,
            ecs,
            now,
            targets,
            "higher cumulative threat attack");
        AssertAttackTarget(
            higherThreatAttack,
            second.CharacterId,
            "highest cumulative actual damage owns aggro");

        legacy.ClearAggroForCharacter(second.CharacterId, now);
        ecs.ClearAggroForCharacter(second.CharacterId, now);
        AssertTickEqual(
            legacy.Advance(now, targets),
            ecs.Advance(now, targets),
            "removed threat leader promotion");
        now += MonsterMapRuntime.TickInterval;
        var promotedAttack = AdvancePair(
            legacy,
            ecs,
            now,
            targets,
            "promoted threat leader attack");
        AssertAttackTarget(
            promotedAttack,
            first.CharacterId,
            "next-highest damage dealer inherits aggro");
    }

    private static void CheckPatrolInterruptionOnProximityParity()
    {
        var definition = CreateMonster(11004, 100f, 50f, tier: 30);
        var legacy = new MonsterMapRuntime(0, [definition], Start);
        var ecs = new EcsMonsterMapRuntime(0, [definition], Start);
        var now = Start;
        MonsterRuntimeSnapshot moving;

        do
        {
            now += MonsterMapRuntime.TickInterval;
            AdvancePair(legacy, ecs, now, [], "pre-aggro patrol");
            moving = legacy.Snapshot().Single();
        }
        while (!moving.IsMoving && now < Start + TimeSpan.FromMinutes(1));

        Check.True(moving.IsMoving, "aggressive monster begins idle patrol");
        var target = Target(806, moving.X + 1f, moving.Z);
        var acquired = AdvancePair(
            legacy,
            ecs,
            now,
            [target],
            "patrol proximity acquisition");
        Check.True(
            acquired.Updates[0].Kind == MonsterRuntimeUpdateKind.Arrived,
            "proximity aggro stops the old patrol before combat");
        Check.Equal(
            1u,
            acquired.Updates[0].MovementEndField ?? 0,
            "proximity aggro publishes a native movement end");

        var attack = AdvancePair(
            legacy,
            ecs,
            now + MonsterMapRuntime.TickInterval,
            [target],
            "post-patrol proximity attack");
        AssertAttackTarget(
            attack,
            target.CharacterId,
            "aggressive monster attacks after stopping patrol");
    }

    private static MonsterCombatTarget Target(
        int characterId,
        float x,
        float z) => new(characterId, x, z, IsAlive: true);

    private static void ApplyDamagePair(
        MonsterMapRuntime legacy,
        EcsMonsterMapRuntime ecs,
        uint objectId,
        int attackerCharacterId,
        uint damage,
        DateTimeOffset now,
        string description)
    {
        var legacyApplied = legacy.TryApplyDamage(
            objectId,
            damage,
            attackerCharacterId,
            now,
            out var legacyResult);
        var ecsApplied = ecs.TryApplyDamage(
            objectId,
            damage,
            attackerCharacterId,
            now,
            out var ecsResult);
        Check.Equal(legacyApplied, ecsApplied, $"{description} acceptance");
        Check.True(
            legacyResult == ecsResult,
            $"{description} result parity");
    }

    private static MonsterRuntimeTick AdvancePair(
        MonsterMapRuntime legacy,
        EcsMonsterMapRuntime ecs,
        DateTimeOffset now,
        IReadOnlyList<MonsterCombatTarget> targets,
        string description)
    {
        var legacyTick = legacy.Advance(now, targets);
        var ecsTick = ecs.Advance(now, targets);
        AssertTickEqual(legacyTick, ecsTick, description);
        return legacyTick;
    }

    private static void AssertAttackTarget(
        MonsterRuntimeTick tick,
        int expectedCharacterId,
        string description)
    {
        var attack = tick.Updates.Single(update =>
            update.Kind == MonsterRuntimeUpdateKind.Attacked);
        Check.Equal(
            expectedCharacterId,
            attack.TargetCharacterId ?? 0,
            description);
    }
}
