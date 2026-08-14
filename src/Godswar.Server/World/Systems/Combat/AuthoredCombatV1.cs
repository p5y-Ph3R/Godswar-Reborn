namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Versioned project-authored combat formula. This is deliberately isolated
/// from content projection so a future capture-backed formula can coexist with
/// already persisted event evidence.
/// </summary>
internal static class AuthoredCombatV1
{
    public const int Version = 1;
    public const int BasisPointScale = 10_000;
    public const int MaximumIgnoreDefenseBasisPoints = 8_000;
    public const int MaximumDamageReductionBasisPoints = 8_000;
    public const int MaximumCriticalReductionBasisPoints = 8_000;
    public const int MinimumHitChanceBasisPoints = 500;
    public const int MaximumHitChanceBasisPoints = 9_800;
    public const int BaseHitChanceBasisPoints = 9_000;
    public const int BaseCriticalChanceBasisPoints = 500;
    public const int MaximumCriticalChanceBasisPoints = 5_000;
    public const int BaseCriticalBonusBasisPoints = 5_000;

    public static CombatResolution ResolveBasicAttack(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        ulong eventId,
        int targetOrder = 0) =>
        ResolveWithRolls(
            attacker,
            target,
            ResolveBasicChannel(attacker.Profession),
            powerAdjustment: 0m,
            flatPower: 0m,
            minimumOneDamage: true,
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
        ResolveWithRolls(
            attacker,
            target,
            property == 1
                ? CombatDamageChannel.Magic
                : CombatDamageChannel.Physical,
            powerAdjustment,
            flatPower,
            minimumOneDamage: false,
            eventId,
            targetOrder);

    public static CombatResolution ResolveBasicAttackForOutcome(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        CombatHitOutcome outcome) =>
        ResolveForOutcome(
            attacker,
            target,
            ResolveBasicChannel(attacker.Profession),
            powerAdjustment: 0m,
            flatPower: 0m,
            minimumOneDamage: true,
            eventId: 0,
            targetOrder: 0,
            outcome,
            ForcedRollEvidence(attacker, target));

    public static CombatResolution ResolveSkillDamageForOutcome(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        int property,
        decimal powerAdjustment,
        decimal flatPower,
        CombatHitOutcome outcome) =>
        ResolveForOutcome(
            attacker,
            target,
            property == 1
                ? CombatDamageChannel.Magic
                : CombatDamageChannel.Physical,
            powerAdjustment,
            flatPower,
            minimumOneDamage: false,
            eventId: 0,
            targetOrder: 0,
            outcome,
            ForcedRollEvidence(attacker, target));

    public static int CalculateHitChanceBasisPoints(
        in CombatAttackerStats attacker,
        in CombatTargetStats target)
    {
        var hit = Math.Max(0L, attacker.Hit);
        var dodge = Math.Max(0L, target.Dodge);
        var scale = ResolveRatingScale(attacker.Level, target.Level);
        var adjustment = 4_000L * (hit - dodge) /
                         (scale + hit + dodge);
        return (int)Math.Clamp(
            BaseHitChanceBasisPoints + adjustment,
            MinimumHitChanceBasisPoints,
            MaximumHitChanceBasisPoints);
    }

    public static int CalculateCriticalChanceBasisPoints(
        in CombatAttackerStats attacker,
        in CombatTargetStats target)
    {
        var critical = Math.Max(0L, attacker.Critical);
        var resistance = Math.Max(0L, target.CriticalResistance);
        var scale = ResolveRatingScale(attacker.Level, target.Level);
        var adjustment = 4_500L * (critical - resistance) /
                         (scale + critical + resistance);
        return (int)Math.Clamp(
            BaseCriticalChanceBasisPoints + adjustment,
            0,
            MaximumCriticalChanceBasisPoints);
    }

    public static int CalculateEffectiveDefense(
        int defense,
        int ignoreDefenseBasisPoints)
    {
        var boundedDefense = Math.Max(0L, defense);
        var boundedIgnore = Math.Clamp(
            ignoreDefenseBasisPoints,
            0,
            MaximumIgnoreDefenseBasisPoints);
        return (int)(boundedDefense *
                     (BasisPointScale - boundedIgnore) /
                     BasisPointScale);
    }

    private static CombatResolution ResolveWithRolls(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        CombatDamageChannel channel,
        decimal powerAdjustment,
        decimal flatPower,
        bool minimumOneDamage,
        ulong eventId,
        int targetOrder)
    {
        if (targetOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetOrder));
        }

        var hitChance = CalculateHitChanceBasisPoints(attacker, target);
        var hitRoll = DeterministicCombatRandom.RollBasisPoints(
            eventId,
            targetOrder,
            CombatRandomStage.Hit);
        var criticalChance = CalculateCriticalChanceBasisPoints(
            attacker,
            target);
        if (hitRoll >= hitChance)
        {
            return ResolveForOutcome(
                attacker,
                target,
                channel,
                powerAdjustment,
                flatPower,
                minimumOneDamage,
                eventId,
                targetOrder,
                CombatHitOutcome.Miss,
                new CombatRollEvidence(
                    hitChance,
                    hitRoll,
                    criticalChance,
                    CombatRollEvidence.NotRolled));
        }

        var criticalRoll = DeterministicCombatRandom.RollBasisPoints(
            eventId,
            targetOrder,
            CombatRandomStage.Critical);
        var outcome = criticalRoll < criticalChance
            ? CombatHitOutcome.Critical
            : CombatHitOutcome.Normal;
        return ResolveForOutcome(
            attacker,
            target,
            channel,
            powerAdjustment,
            flatPower,
            minimumOneDamage,
            eventId,
            targetOrder,
            outcome,
            new CombatRollEvidence(
                hitChance,
                hitRoll,
                criticalChance,
                criticalRoll));
    }

    private static CombatResolution ResolveForOutcome(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        CombatDamageChannel channel,
        decimal powerAdjustment,
        decimal flatPower,
        bool minimumOneDamage,
        ulong eventId,
        int targetOrder,
        CombatHitOutcome outcome,
        in CombatRollEvidence rolls)
    {
        if (outcome is not (
                CombatHitOutcome.Normal or
                CombatHitOutcome.Miss or
                CombatHitOutcome.Critical))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        var attack = ResolveAttack(attacker, channel);
        var effectiveDefense = ResolveEffectiveDefense(
            attacker,
            target,
            channel);
        if (outcome == CombatHitOutcome.Miss)
        {
            return new CombatResolution(
                Version,
                eventId,
                targetOrder,
                channel,
                outcome,
                Damage: 0,
                rolls,
                new CombatDamageEvidence(
                    attack,
                    effectiveDefense,
                    Math.Max(0m, (decimal)attack - effectiveDefense),
                    0m,
                    0m,
                    0m,
                    0m,
                    0m,
                    0m));
        }

        var attackAfterDefense = Math.Max(
            0m,
            (decimal)attack - effectiveDefense);
        var coefficient = Math.Max(
            0m,
            SaturatingAdd(1m, powerAdjustment));
        var core = SaturatingAdd(
            SaturatingMultiply(attackAfterDefense, coefficient),
            flatPower);
        var typedBonus = ResolveTypedDamageBonus(attacker, channel);
        var afterTypedBonus = SaturatingMultiply(
            core,
            1m + (Math.Max(0, typedBonus) /
                  (decimal)BasisPointScale));

        var criticalBonus = outcome == CombatHitOutcome.Critical
            ? CalculateCriticalBonus(afterTypedBonus, attacker, target)
            : 0m;
        var append = ResolveAppendDamage(attacker, channel);
        var withAppend = SaturatingAdd(
            SaturatingAdd(afterTypedBonus, criticalBonus),
            Math.Max(0, append));
        var reduction = Math.Clamp(
            ResolveTypedDamageReduction(target, channel),
            0,
            MaximumDamageReductionBasisPoints);
        var afterReduction = withAppend > 0m
            ? SaturatingMultiply(
                withAppend,
                (BasisPointScale - reduction) /
                (decimal)BasisPointScale)
            : withAppend;
        var absorption = Math.Max(
            0,
            ResolveFlatAbsorption(target, channel));
        var afterAbsorption = SaturatingAdd(
            afterReduction,
            -absorption);
        var shouldFloorAtOne = minimumOneDamage || withAppend > 0m;
        var damage = ResolveFinalDamage(
            afterAbsorption,
            shouldFloorAtOne);

        return new CombatResolution(
            Version,
            eventId,
            targetOrder,
            channel,
            outcome,
            damage,
            rolls,
            new CombatDamageEvidence(
                attack,
                effectiveDefense,
                attackAfterDefense,
                core,
                afterTypedBonus,
                criticalBonus,
                withAppend,
                afterReduction,
                afterAbsorption));
    }

    private static decimal CalculateCriticalBonus(
        decimal afterTypedBonus,
        in CombatAttackerStats attacker,
        in CombatTargetStats target)
    {
        if (afterTypedBonus <= 0m)
        {
            return 0m;
        }

        var criticalRate = BaseCriticalBonusBasisPoints +
                           Math.Max(0L,
                               attacker.CriticalDamageBasisPoints);
        var bonus = SaturatingAdd(
            SaturatingMultiply(
                afterTypedBonus,
                criticalRate / (decimal)BasisPointScale),
            Math.Max(0, attacker.CriticalDamageFlat));
        var reduction = Math.Clamp(
            target.CriticalDamageReductionBasisPoints,
            0,
            MaximumCriticalReductionBasisPoints);
        bonus = SaturatingMultiply(
            bonus,
            (BasisPointScale - reduction) /
            (decimal)BasisPointScale);
        bonus = SaturatingAdd(
            bonus,
            -Math.Max(0, target.CriticalDamageFlatReduction));
        return Math.Max(0m, bonus);
    }

    private static CombatRollEvidence ForcedRollEvidence(
        in CombatAttackerStats attacker,
        in CombatTargetStats target) =>
        new(
            CalculateHitChanceBasisPoints(attacker, target),
            CombatRollEvidence.NotRolled,
            CalculateCriticalChanceBasisPoints(attacker, target),
            CombatRollEvidence.NotRolled);

    private static CombatDamageChannel ResolveBasicChannel(byte profession) =>
        profession is 2 or 3
            ? CombatDamageChannel.Magic
            : CombatDamageChannel.Physical;

    private static int ResolveAttack(
        in CombatAttackerStats attacker,
        CombatDamageChannel channel) =>
        Math.Max(0, channel == CombatDamageChannel.Magic
            ? attacker.MagicAttack
            : attacker.PhysicalAttack);

    private static int ResolveEffectiveDefense(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        CombatDamageChannel channel) =>
        channel == CombatDamageChannel.Magic
            ? CalculateEffectiveDefense(
                target.MagicDefense,
                attacker.IgnoreMagicDefenseBasisPoints)
            : CalculateEffectiveDefense(
                target.PhysicalDefense,
                attacker.IgnorePhysicalDefenseBasisPoints);

    private static int ResolveTypedDamageBonus(
        in CombatAttackerStats attacker,
        CombatDamageChannel channel) =>
        channel == CombatDamageChannel.Magic
            ? attacker.MagicDamageBonusBasisPoints
            : attacker.PhysicalDamageBonusBasisPoints;

    private static int ResolveAppendDamage(
        in CombatAttackerStats attacker,
        CombatDamageChannel channel) =>
        channel == CombatDamageChannel.Magic
            ? attacker.MagicAppendDamage
            : attacker.PhysicalAppendDamage;

    private static int ResolveTypedDamageReduction(
        in CombatTargetStats target,
        CombatDamageChannel channel) =>
        channel == CombatDamageChannel.Magic
            ? target.MagicDamageReductionBasisPoints
            : target.PhysicalDamageReductionBasisPoints;

    private static int ResolveFlatAbsorption(
        in CombatTargetStats target,
        CombatDamageChannel channel) =>
        channel == CombatDamageChannel.Magic
            ? target.MagicFlatAbsorption
            : target.PhysicalFlatAbsorption;

    private static long ResolveRatingScale(int attackerLevel, int targetLevel)
    {
        var level = Math.Clamp(
            Math.Max(attackerLevel, targetLevel),
            1,
            10_000);
        return 100L + (25L * level);
    }

    private static uint ResolveFinalDamage(
        decimal damage,
        bool minimumOneDamage)
    {
        if (damage <= 0m)
        {
            return minimumOneDamage ? 1u : 0u;
        }

        var rounded = decimal.Round(
            damage,
            0,
            MidpointRounding.AwayFromZero);
        if (rounded >= uint.MaxValue)
        {
            return uint.MaxValue;
        }

        return (uint)Math.Max(
            minimumOneDamage ? 1m : 0m,
            rounded);
    }

    private static decimal SaturatingAdd(decimal left, decimal right)
    {
        try
        {
            return left + right;
        }
        catch (OverflowException)
        {
            return right >= 0m ? decimal.MaxValue : decimal.MinValue;
        }
    }

    private static decimal SaturatingMultiply(decimal left, decimal right)
    {
        try
        {
            return left * right;
        }
        catch (OverflowException)
        {
            return Math.Sign(left) == Math.Sign(right)
                ? decimal.MaxValue
                : decimal.MinValue;
        }
    }
}
