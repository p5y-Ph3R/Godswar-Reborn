namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct HostileStatusProcRatings(
    int AttackerLevel,
    int TargetLevel,
    int EffectiveAttackerHit,
    int EffectiveTargetDodge,
    int AttackerStatusHit,
    int TargetStatusResistance);

internal readonly record struct HostileStatusProcDecision(
    int ChanceBasisPoints,
    int RollBasisPoints,
    long OffenseRating,
    long DefenseRating,
    long RatingScale,
    bool Applied);

/// <summary>
/// Reborn-authored deterministic landing rule for duration-based hostile
/// statuses. Stock StatusOdds is a rating with an unrecovered native scale,
/// so every supported status begins at the explicitly authored 50% neutral
/// chance. Hit/status-success contest Dodge/status-resistance symmetrically.
/// </summary>
internal static class HostileStatusProcPolicy
{
    internal const int BaseChanceBasisPoints = 5_000;
    internal const int MinimumChanceBasisPoints = 500;
    internal const int MaximumChanceBasisPoints = 9_500;
    private const int MaximumAdjustmentBasisPoints = 4_500;

    public static HostileStatusProcDecision Evaluate(
        in HostileStatusProcRatings ratings,
        ulong eventId,
        int targetOrder)
    {
        var hit = Math.Max(0L, ratings.EffectiveAttackerHit);
        var statusHit = Math.Max(0L, ratings.AttackerStatusHit);
        var dodge = Math.Max(0L, ratings.EffectiveTargetDodge);
        var resistance = Math.Max(0L, ratings.TargetStatusResistance);
        var offense = checked(hit + statusHit);
        var defense = checked(dodge + resistance);
        var scale = AuthoredCombatRatingScale.Resolve(
            ratings.AttackerLevel,
            ratings.TargetLevel);
        var denominator = checked(scale + offense + defense);
        var adjustment = checked(
            MaximumAdjustmentBasisPoints * (offense - defense)) /
            denominator;
        var chance = checked((int)Math.Clamp(
            BaseChanceBasisPoints + adjustment,
            MinimumChanceBasisPoints,
            MaximumChanceBasisPoints));
        var roll = DeterministicCombatRandom.RollBasisPoints(
            eventId,
            targetOrder,
            CombatRandomStage.StatusProc);
        return new(
            chance,
            roll,
            offense,
            defense,
            scale,
            roll < chance);
    }
}

internal static class HostileStatusDurationPolicy
{
    /// <summary>
    /// StatusHit/StatusMiss are landing ratings for duration-based spells;
    /// they do not alter the stock duration.
    /// </summary>
    public static DateTimeOffset ResolveExpiry(
        DateTimeOffset appliedAt,
        TimeSpan stockDuration)
    {
        if (stockDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stockDuration));
        }
        return appliedAt + stockDuration;
    }
}
