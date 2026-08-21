using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsParityChecks
{
    private static void CheckAuthoredCombatV2()
    {
        CheckHitCurveAtLevel(
            level: 140,
            expected: [
                (0, 0, 9_000),
                (4_000, 4_000, 9_000),
                (4_000, 2_000, 9_800),
                (6_000, 4_000, 9_588),
                (4_000, 6_000, 500),
                (2_000, 4_000, 500),
                (0, 4_000, 500),
                (4_000, 8_000, 500),
                (0, 10_000, 500),
                (10_000, 0, 9_800)
            ]);
        CheckHitCurveAtLevel(
            level: 200,
            expected: [
                (0, 0, 9_000),
                (4_000, 4_000, 9_000),
                (4_000, 2_000, 9_720),
                (6_000, 4_000, 9_529),
                (4_000, 6_000, 500),
                (2_000, 4_000, 500),
                (0, 4_000, 500),
                (4_000, 8_000, 500),
                (0, 10_000, 500),
                (10_000, 0, 9_800)
            ]);

        CheckDodgeAdvantageAcceptancePoints();
        CheckCriticalResistanceAcceptancePoints();

        var negativeRatings = HitCurvePair(
            level: 140,
            hit: -1,
            dodge: -1);
        Check.Equal(
            9_000,
            AuthoredCombatV2.CalculateHitChanceBasisPoints(
                negativeRatings.Attacker,
                negativeRatings.Target),
            "V2 clamps negative Hit and Dodge before resolving accuracy");

        var maximumHit = HitCurvePair(
            level: 10_000,
            hit: int.MaxValue,
            dodge: 0);
        var maximumDodge = HitCurvePair(
            level: 10_000,
            hit: 0,
            dodge: int.MaxValue);
        Check.Equal(
            9_800,
            AuthoredCombatV2.CalculateHitChanceBasisPoints(
                maximumHit.Attacker,
                maximumHit.Target),
            "V2 reaches but never exceeds the accuracy ceiling");
        Check.Equal(
            500,
            AuthoredCombatV2.CalculateHitChanceBasisPoints(
                maximumDodge.Attacker,
                maximumDodge.Target),
            "V2 makes the five-percent evasion floor reachable");

        var replay = HitCurvePair(
            level: 140,
            hit: 4_000,
            dodge: 6_000);
        var current = AuthoredCombatPvpCurrent.ResolveBasicAttack(
            replay.Attacker,
            replay.Target,
            eventId: 2);
        var pve = AuthoredCombatPveCurrent.ResolveBasicAttack(
            replay.Attacker,
            replay.Target,
            eventId: 2);
        Check.True(
            current.FormulaVersion == AuthoredCombatV2.Version &&
            pve.FormulaVersion == AuthoredCombatV1.Version &&
            pve.Rolls.HitChanceBasisPoints == 8_412,
            "live PvP selects V2 while PvE preserves the V1 curve");
        Check.Equal(
            pve.Rolls.HitRollBasisPoints,
            current.Rolls.HitRollBasisPoints,
            "formula versions preserve the event-derived raw hit roll");

        var v1Boundary = AuthoredCombatV1.ResolveBasicAttack(
            replay.Attacker,
            replay.Target,
            eventId: 9);
        var v2Boundary = AuthoredCombatPvpCurrent.ResolveBasicAttack(
            replay.Attacker,
            replay.Target,
            eventId: 9);
        Check.True(
            v1Boundary.Rolls.HitRollBasisPoints == 8_367 &&
            v1Boundary.Outcome is not CombatHitOutcome.Miss &&
            v1Boundary.Damage > 0 &&
            v2Boundary.Outcome == CombatHitOutcome.Miss &&
            v2Boundary.Damage == 0,
            "the tuned PvP boundary converts the same V1 hit into a V2 miss");
    }

    private static void CheckCriticalResistanceAcceptancePoints()
    {
        var expected = new (
            int Critical,
            int Resistance,
            int ChanceBasisPoints)[]
        {
            (0, 0, 0),
            (0, 250, 0),
            (1, 0, 9_000),
            (8, 1, 8_888),
            (9, 1, 9_000),
            (500, 0, 9_000),
            (250, 250, 5_000),
            (500, 500, 5_000),
            (500, 430, 5_376),
            (500, 400, 5_555),
            (500, 250, 6_666),
            (250, 500, 3_333),
            (430, 500, 4_623)
        };

        foreach (var point in expected)
        {
            var pair = CriticalCurvePair(
                level: 140,
                point.Critical,
                point.Resistance);
            Check.Equal(
                point.ChanceBasisPoints,
                AuthoredCombatV2.CalculateCriticalChanceBasisPoints(
                    pair.Attacker,
                    pair.Target),
                $"V2 Critical {point.Critical} Resistance " +
                point.Resistance);
        }

        var levelIndependent = CriticalCurvePair(
            level: 200,
            critical: 500,
            resistance: 0);
        Check.Equal(
            9_000,
            AuthoredCombatV2.CalculateCriticalChanceBasisPoints(
                levelIndependent.Attacker,
                levelIndependent.Target),
            "V2 zero-resistance Critical cap is level-independent");

        var negative = CriticalCurvePair(140, -1, -1);
        Check.Equal(
            0,
            AuthoredCombatV2.CalculateCriticalChanceBasisPoints(
                negative.Attacker,
                negative.Target),
            "V2 grants no natural Critical when both ratings clamp to zero");
        var negativeCritical = CriticalCurvePair(140, -1, 500);
        var negativeResistance = CriticalCurvePair(140, 500, -1);
        Check.True(
            AuthoredCombatV2.CalculateCriticalChanceBasisPoints(
                negativeCritical.Attacker,
                negativeCritical.Target) == 0 &&
            AuthoredCombatV2.CalculateCriticalChanceBasisPoints(
                negativeResistance.Attacker,
                negativeResistance.Target) == 9_000,
            "V2 independently clamps negative Critical and Resistance");
        var maximumCritical = CriticalCurvePair(
            10_000,
            int.MaxValue,
            0);
        var maximumResistance = CriticalCurvePair(
            10_000,
            0,
            int.MaxValue);
        Check.Equal(
            9_000,
            AuthoredCombatV2.CalculateCriticalChanceBasisPoints(
                maximumCritical.Attacker,
                maximumCritical.Target),
            "V2 Critical chance reaches but never exceeds ninety percent");
        Check.Equal(
            0,
            AuthoredCombatV2.CalculateCriticalChanceBasisPoints(
                maximumResistance.Attacker,
                maximumResistance.Target),
            "V2 Critical Resistance can suppress criticals completely");
        var equalMaximum = CriticalCurvePair(
            10_000,
            int.MaxValue,
            int.MaxValue);
        Check.Equal(
            5_000,
            AuthoredCombatV2.CalculateCriticalChanceBasisPoints(
                equalMaximum.Attacker,
                equalMaximum.Target),
            "V2 equal maximum ratings remain a fifty-percent contest");

        var replay = CriticalCurvePair(140, 500, 0);
        Check.Equal(
            1_048,
            AuthoredCombatV1.CalculateCriticalChanceBasisPoints(
                replay.Attacker,
            replay.Target),
            "V1 retains its historical normalized Critical curve");
        var v1Resistance = CriticalCurvePair(140, 500, 430);
        Check.Equal(
            569,
            AuthoredCombatV1.CalculateCriticalChanceBasisPoints(
                v1Resistance.Attacker,
                v1Resistance.Target),
            "V1 retains its historical Critical Resistance curve");
        var v1 = AuthoredCombatV1.ResolveBasicAttack(
            replay.Attacker,
            replay.Target,
            eventId: 7);
        var v2 = AuthoredCombatPvpCurrent.ResolveBasicAttack(
            replay.Attacker,
            replay.Target,
            eventId: 7);
        Check.True(
            v1.Rolls.HitRollBasisPoints == 1_083 &&
            v1.Rolls.CriticalRollBasisPoints == 2_868 &&
            v1.Outcome == CombatHitOutcome.Normal &&
            v2.Outcome == CombatHitOutcome.Critical,
            "the same event remains normal in V1 and becomes Critical in V2");
    }

    private static void CheckDodgeAdvantageAcceptancePoints()
    {
        var expected = new (int Dodge, int ChanceBasisPoints)[]
        {
            (3_000, 9_000),
            (3_100, 7_800),
            (3_250, 6_000),
            (3_500, 3_000),
            (3_501, 2_999),
            (4_000, 2_167),
            (4_500, 1_334),
            (4_999, 502),
            (5_000, 500)
        };

        var previousChance = int.MaxValue;
        foreach (var point in expected)
        {
            var pair = HitCurvePair(
                level: 140,
                hit: 3_000,
                dodge: point.Dodge);
            var actual = AuthoredCombatV2.CalculateHitChanceBasisPoints(
                pair.Attacker,
                pair.Target);
            Check.Equal(
                point.ChanceBasisPoints,
                actual,
                $"V2 Hit 3000 Dodge {point.Dodge}");
            Check.True(
                actual <= previousChance,
                "V2 Dodge-pressure acceptance points are monotonic");
            previousChance = actual;
        }

        var levelIndependent = HitCurvePair(
            level: 200,
            hit: 3_000,
            dodge: 3_500);
        Check.Equal(
            3_000,
            AuthoredCombatV2.CalculateHitChanceBasisPoints(
                levelIndependent.Attacker,
                levelIndependent.Target),
            "V2 Dodge pressure is intentionally level-independent");
    }

    private static void CheckHitCurveAtLevel(
        int level,
        (int Hit, int Dodge, int ChanceBasisPoints)[] expected)
    {
        var previousChance = int.MaxValue;
        foreach (var point in expected)
        {
            var pair = HitCurvePair(level, point.Hit, point.Dodge);
            var actual = AuthoredCombatV2.CalculateHitChanceBasisPoints(
                pair.Attacker,
                pair.Target);
            Check.Equal(
                point.ChanceBasisPoints,
                actual,
                $"V2 level {level} Hit {point.Hit} Dodge {point.Dodge}");

            if (point.Hit == 4_000 && point.Dodge >= 4_000)
            {
                Check.True(
                    actual <= previousChance,
                    "V2 accuracy is monotonic as Dodge rises");
                previousChance = actual;
            }
        }
    }

    private static (
        CombatAttackerStats Attacker,
        CombatTargetStats Target) HitCurvePair(
            int level,
            int hit,
            int dodge) =>
        (
            new CombatAttackerStats
            {
                Level = level,
                Profession = 0,
                PhysicalAttack = 1_000,
                Hit = hit
            },
            new CombatTargetStats
            {
                Level = level,
                Dodge = dodge
            });

    private static (
        CombatAttackerStats Attacker,
        CombatTargetStats Target) CriticalCurvePair(
            int level,
            int critical,
            int resistance) =>
        (
            new CombatAttackerStats
            {
                Level = level,
                Profession = 0,
                PhysicalAttack = 1_000,
                Critical = critical
            },
            new CombatTargetStats
            {
                Level = level,
                CriticalResistance = resistance
            });
}
