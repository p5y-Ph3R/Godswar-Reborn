namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Frozen first authored formula. It remains callable so recorded formula
/// version 1 evidence can always be reproduced.
/// </summary>
internal static class AuthoredCombatV1
{
    public const int Version = 1;

    private static readonly AuthoredCombatFormula Formula = new(
        Version,
        AuthoredHitChancePolicy.Normalized(
            favorableAdjustmentBasisPoints: 4_000,
            dodgeAdjustmentBasisPoints: 4_000),
        AuthoredCriticalChancePolicy.Normalized(
            adjustmentBasisPoints: 4_500));

    public static CombatResolution ResolveBasicAttack(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        ulong eventId,
        int targetOrder = 0) =>
        Formula.ResolveBasicAttack(
            attacker,
            target,
            eventId,
            targetOrder);

    public static CombatResolution ResolveSkillDamage(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        int property,
        decimal powerAdjustment,
        decimal flatPower,
        ulong eventId,
        int targetOrder = 0) =>
        Formula.ResolveSkillDamage(
            attacker,
            target,
            property,
            powerAdjustment,
            flatPower,
            eventId,
            targetOrder);

    public static CombatResolution ResolveBasicAttackForOutcome(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        CombatHitOutcome outcome) =>
        Formula.ResolveBasicAttackForOutcome(attacker, target, outcome);

    public static CombatResolution ResolveSkillDamageForOutcome(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        int property,
        decimal powerAdjustment,
        decimal flatPower,
        CombatHitOutcome outcome) =>
        Formula.ResolveSkillDamageForOutcome(
            attacker,
            target,
            property,
            powerAdjustment,
            flatPower,
            outcome);

    public static int CalculateHitChanceBasisPoints(
        in CombatAttackerStats attacker,
        in CombatTargetStats target) =>
        Formula.CalculateHitChanceBasisPoints(attacker, target);

    public static int CalculateCriticalChanceBasisPoints(
        in CombatAttackerStats attacker,
        in CombatTargetStats target) =>
        Formula.CalculateCriticalChanceBasisPoints(attacker, target);

    public static int CalculateEffectiveDefense(
        int defense,
        int ignoreDefenseBasisPoints) =>
        AuthoredCombatFormula.CalculateEffectiveDefense(
            defense,
            ignoreDefenseBasisPoints);
}

/// <summary>
/// Current authored formula. V2 preserves the V1 accuracy curve whenever Hit
/// meets or exceeds Dodge. When Dodge wins, the first five hundred points of
/// deficit impose hard pressure before tapering toward the five-percent floor.
/// After a hit, Critical contests Critical Resistance as a capped direct ratio.
/// </summary>
internal static class AuthoredCombatV2
{
    public const int Version = 2;

    private static readonly AuthoredCombatFormula Formula = new(
        Version,
        AuthoredHitChancePolicy.Tiered(
            favorableAdjustmentBasisPoints: 4_000,
            initialPenaltyBasisPointsPerRating: 12,
            initialPressureRatings: 500,
            tailPenaltyNumerator: 5,
            tailPenaltyDenominator: 3),
        AuthoredCriticalChancePolicy.ContestedRatio(
            maximumChanceBasisPoints: 9_000));

    public static CombatResolution ResolveBasicAttack(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        ulong eventId,
        int targetOrder = 0) =>
        Formula.ResolveBasicAttack(
            attacker,
            target,
            eventId,
            targetOrder);

    public static CombatResolution ResolveSkillDamage(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        int property,
        decimal powerAdjustment,
        decimal flatPower,
        ulong eventId,
        int targetOrder = 0) =>
        Formula.ResolveSkillDamage(
            attacker,
            target,
            property,
            powerAdjustment,
            flatPower,
            eventId,
            targetOrder);

    public static CombatResolution ResolveBasicAttackForOutcome(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        CombatHitOutcome outcome) =>
        Formula.ResolveBasicAttackForOutcome(attacker, target, outcome);

    public static CombatResolution ResolveSkillDamageForOutcome(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        int property,
        decimal powerAdjustment,
        decimal flatPower,
        CombatHitOutcome outcome) =>
        Formula.ResolveSkillDamageForOutcome(
            attacker,
            target,
            property,
            powerAdjustment,
            flatPower,
            outcome);

    public static int CalculateHitChanceBasisPoints(
        in CombatAttackerStats attacker,
        in CombatTargetStats target) =>
        Formula.CalculateHitChanceBasisPoints(attacker, target);

    public static int CalculateCriticalChanceBasisPoints(
        in CombatAttackerStats attacker,
        in CombatTargetStats target) =>
        Formula.CalculateCriticalChanceBasisPoints(attacker, target);

    public static int CalculateEffectiveDefense(
        int defense,
        int ignoreDefenseBasisPoints) =>
        AuthoredCombatFormula.CalculateEffectiveDefense(
            defense,
            ignoreDefenseBasisPoints);
}

/// <summary>
/// Live routing seam for PvE combat. The captured PvE outcome distribution
/// remains on V1 until monster Hit/Dodge content is intentionally rebalanced.
/// </summary>
internal static class AuthoredCombatPveCurrent
{
    public const int Version = AuthoredCombatV1.Version;

    public static CombatResolution ResolveBasicAttack(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        ulong eventId,
        int targetOrder = 0) =>
        AuthoredCombatV1.ResolveBasicAttack(
            attacker,
            target,
            eventId,
            targetOrder);

    public static CombatResolution ResolveSkillDamage(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        int property,
        decimal powerAdjustment,
        decimal flatPower,
        ulong eventId,
        int targetOrder = 0) =>
        AuthoredCombatV1.ResolveSkillDamage(
            attacker,
            target,
            property,
            powerAdjustment,
            flatPower,
            eventId,
            targetOrder);

    public static CombatResolution ResolveBasicAttackForOutcome(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        CombatHitOutcome outcome) =>
        AuthoredCombatV1.ResolveBasicAttackForOutcome(
            attacker,
            target,
            outcome);

    public static CombatResolution ResolveSkillDamageForOutcome(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        int property,
        decimal powerAdjustment,
        decimal flatPower,
        CombatHitOutcome outcome) =>
        AuthoredCombatV1.ResolveSkillDamageForOutcome(
            attacker,
            target,
            property,
            powerAdjustment,
            flatPower,
            outcome);
}

/// <summary>
/// Live routing seam for admitted PvP combat. Native hostile-player skill wire
/// semantics remain gated. The development-only training-dummy adapter may use
/// the same V2 scalar skill formula after its narrower server-side admission.
/// </summary>
internal static class AuthoredCombatPvpCurrent
{
    public const int Version = AuthoredCombatV2.Version;

    public static CombatResolution ResolveBasicAttack(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        ulong eventId,
        int targetOrder = 0) =>
        AuthoredCombatV2.ResolveBasicAttack(
            attacker,
            target,
            eventId,
            targetOrder);

    public static CombatResolution ResolveSkillDamage(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        int property,
        decimal powerAdjustment,
        decimal flatPower,
        ulong eventId,
        int targetOrder = 0) =>
        AuthoredCombatV2.ResolveSkillDamage(
            attacker,
            target,
            property,
            powerAdjustment,
            flatPower,
            eventId,
            targetOrder);
}
