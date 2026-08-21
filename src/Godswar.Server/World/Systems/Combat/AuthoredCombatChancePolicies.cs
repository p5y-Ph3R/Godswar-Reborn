namespace Godswar.Server.World.Systems.Combat;

internal readonly struct AuthoredHitChancePolicy
{
    private const byte NormalizedMode = 1;
    private const byte TieredMode = 2;

    private readonly byte _mode;
    private readonly int _favorableAdjustmentBasisPoints;
    private readonly int _dodgeAdjustmentBasisPoints;
    private readonly int _dodgeThresholdRatings;
    private readonly int _dodgeTailNumerator;
    private readonly int _dodgeTailDenominator;

    private AuthoredHitChancePolicy(
        byte mode,
        int favorableAdjustmentBasisPoints,
        int dodgeAdjustmentBasisPoints,
        int dodgeThresholdRatings,
        int dodgeTailNumerator,
        int dodgeTailDenominator)
    {
        _mode = mode;
        _favorableAdjustmentBasisPoints =
            favorableAdjustmentBasisPoints;
        _dodgeAdjustmentBasisPoints = dodgeAdjustmentBasisPoints;
        _dodgeThresholdRatings = dodgeThresholdRatings;
        _dodgeTailNumerator = dodgeTailNumerator;
        _dodgeTailDenominator = dodgeTailDenominator;
    }

    public bool IsConfigured => _mode is NormalizedMode or TieredMode;

    public static AuthoredHitChancePolicy Normalized(
        int favorableAdjustmentBasisPoints,
        int dodgeAdjustmentBasisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            favorableAdjustmentBasisPoints);
        ArgumentOutOfRangeException.ThrowIfNegative(
            dodgeAdjustmentBasisPoints);
        return new(
            NormalizedMode,
            favorableAdjustmentBasisPoints,
            dodgeAdjustmentBasisPoints,
            dodgeThresholdRatings: 0,
            dodgeTailNumerator: 0,
            dodgeTailDenominator: 1);
    }

    public static AuthoredHitChancePolicy Tiered(
        int favorableAdjustmentBasisPoints,
        int initialPenaltyBasisPointsPerRating,
        int initialPressureRatings,
        int tailPenaltyNumerator,
        int tailPenaltyDenominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            favorableAdjustmentBasisPoints);
        ArgumentOutOfRangeException.ThrowIfNegative(
            initialPenaltyBasisPointsPerRating);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            initialPressureRatings);
        ArgumentOutOfRangeException.ThrowIfNegative(
            tailPenaltyNumerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            tailPenaltyDenominator);
        return new(
            TieredMode,
            favorableAdjustmentBasisPoints,
            initialPenaltyBasisPointsPerRating,
            initialPressureRatings,
            tailPenaltyNumerator,
            tailPenaltyDenominator);
    }

    public int Calculate(
        in CombatAttackerStats attacker,
        in CombatTargetStats target)
    {
        var hit = Math.Max(0L, attacker.Hit);
        var dodge = Math.Max(0L, target.Dodge);
        var delta = hit - dodge;
        var adjustment = delta >= 0
            ? _favorableAdjustmentBasisPoints * delta /
              (AuthoredCombatRatingScale.Resolve(
                   attacker.Level,
                   target.Level) + hit + dodge)
            : ResolveDodgePressureAdjustment(delta, hit, dodge,
                attacker.Level, target.Level);
        return (int)Math.Clamp(
            AuthoredCombatFormula.BaseHitChanceBasisPoints + adjustment,
            AuthoredCombatFormula.MinimumHitChanceBasisPoints,
            AuthoredCombatFormula.MaximumHitChanceBasisPoints);
    }

    private long ResolveDodgePressureAdjustment(
        long ratingDelta,
        long hit,
        long dodge,
        int attackerLevel,
        int targetLevel)
    {
        if (_mode == NormalizedMode)
        {
            return _dodgeAdjustmentBasisPoints * ratingDelta /
                   (AuthoredCombatRatingScale.Resolve(
                        attackerLevel,
                        targetLevel) + hit + dodge);
        }

        var deficit = -ratingDelta;
        var initialPressure = Math.Min(deficit, _dodgeThresholdRatings);
        var adjustment = -_dodgeAdjustmentBasisPoints * initialPressure;
        if (deficit <= _dodgeThresholdRatings)
        {
            return adjustment;
        }

        var tailPressure = deficit - _dodgeThresholdRatings;
        return adjustment -
               _dodgeTailNumerator * tailPressure /
               _dodgeTailDenominator;
    }
}

internal readonly struct AuthoredCriticalChancePolicy
{
    private const byte NormalizedMode = 1;
    private const byte ContestedRatioMode = 2;
    private const int NormalizedBaseChanceBasisPoints = 500;
    private const int NormalizedMaximumChanceBasisPoints = 5_000;
    private const int RatioScaleBasisPoints = 10_000;

    private readonly byte _mode;
    private readonly int _normalizedAdjustmentBasisPoints;
    private readonly int _maximumChanceBasisPoints;

    private AuthoredCriticalChancePolicy(
        byte mode,
        int normalizedAdjustmentBasisPoints,
        int maximumChanceBasisPoints)
    {
        _mode = mode;
        _normalizedAdjustmentBasisPoints =
            normalizedAdjustmentBasisPoints;
        _maximumChanceBasisPoints = maximumChanceBasisPoints;
    }

    public bool IsConfigured =>
        _mode is NormalizedMode or ContestedRatioMode;

    public static AuthoredCriticalChancePolicy Normalized(
        int adjustmentBasisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(adjustmentBasisPoints);
        return new(
            NormalizedMode,
            adjustmentBasisPoints,
            NormalizedMaximumChanceBasisPoints);
    }

    public static AuthoredCriticalChancePolicy ContestedRatio(
        int maximumChanceBasisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumChanceBasisPoints);
        if (maximumChanceBasisPoints > RatioScaleBasisPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumChanceBasisPoints));
        }

        return new(
            ContestedRatioMode,
            normalizedAdjustmentBasisPoints: 0,
            maximumChanceBasisPoints);
    }

    public int Calculate(
        in CombatAttackerStats attacker,
        in CombatTargetStats target)
    {
        var critical = Math.Max(0L, attacker.Critical);
        var resistance = Math.Max(0L, target.CriticalResistance);
        long chance;
        if (_mode == NormalizedMode)
        {
            var delta = critical - resistance;
            chance = NormalizedBaseChanceBasisPoints +
                     (_normalizedAdjustmentBasisPoints * delta /
                      (AuthoredCombatRatingScale.Resolve(
                           attacker.Level,
                           target.Level) + critical + resistance));
        }
        else if (critical + resistance == 0)
        {
            chance = 0;
        }
        else
        {
            chance = RatioScaleBasisPoints * critical /
                     (critical + resistance);
        }

        return (int)Math.Clamp(
            chance,
            0,
            _maximumChanceBasisPoints);
    }
}

internal static class AuthoredCombatRatingScale
{
    public static long Resolve(int attackerLevel, int targetLevel)
    {
        var level = Math.Clamp(
            Math.Max(attackerLevel, targetLevel),
            1,
            10_000);
        return 100L + (25L * level);
    }
}
