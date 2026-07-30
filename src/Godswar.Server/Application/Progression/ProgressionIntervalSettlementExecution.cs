namespace Godswar.Server.Application.Progression;

internal enum ProgressionIntervalSettlementDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    RequestHashConflict = 3,
    InvalidIntent = 4,
    CharacterNotFound = 5,
    IntervalConflict = 6
}

internal enum ProgressionIntervalConflict : byte
{
    None = 0,
    StaleSession = 1,
    InvalidSequence = 2,
    Overlap = 3,
    Gap = 4
}

internal sealed record ProgressionIntervalProjection(
    Guid OnlineSessionId,
    long LastIntervalSequence,
    DateTimeOffset LastIntervalEndUtc,
    long AggregateRevision,
    int ZodiacEnergy,
    int ZodiacEnergyRemainderX100,
    DateOnly ZodiacOnlineDay,
    long ZodiacOnlineDurationTicksToday,
    DateOnly? ZodiacLastCompensationDay);

internal sealed record ProgressionIntervalSettlementReceipt(
    int CharacterId,
    Guid OnlineSessionId,
    long IntervalSequence,
    DateTimeOffset OnlineFromUtc,
    DateTimeOffset OnlineUntilUtc,
    int GainedZodiacEnergyX100,
    bool ZodiacCompensationApplied,
    int UpdatedBoostCount,
    ProgressionIntervalProjection Projection,
    string AuditReference,
    Guid OutboxEventId);

internal sealed record ProgressionIntervalSettlementExecutionResult(
    ProgressionIntervalSettlementDisposition Disposition,
    ProgressionIntervalSettlementReceipt? Receipt = null,
    ProgressionIntervalProjection? Projection = null,
    ProgressionIntervalConflict Conflict =
        ProgressionIntervalConflict.None)
{
    public bool IsDurable => Receipt is not null;

    public bool IsSuccess => Disposition is
        ProgressionIntervalSettlementDisposition.Committed or
        ProgressionIntervalSettlementDisposition.Duplicate;

    public static ProgressionIntervalSettlementExecutionResult Committed(
        ProgressionIntervalSettlementReceipt receipt) =>
        new(
            ProgressionIntervalSettlementDisposition.Committed,
            receipt,
            receipt.Projection);

    public static ProgressionIntervalSettlementExecutionResult Duplicate(
        ProgressionIntervalSettlementReceipt receipt,
        ProgressionIntervalProjection projection) =>
        new(
            ProgressionIntervalSettlementDisposition.Duplicate,
            receipt,
            projection);

    public static ProgressionIntervalSettlementExecutionResult
        RequestHashConflict() =>
        new(
            ProgressionIntervalSettlementDisposition
                .RequestHashConflict);

    public static ProgressionIntervalSettlementExecutionResult
        InvalidIntent() =>
        new(
            ProgressionIntervalSettlementDisposition.InvalidIntent);

    public static ProgressionIntervalSettlementExecutionResult
        CharacterNotFound() =>
        new(
            ProgressionIntervalSettlementDisposition.CharacterNotFound);

    public static ProgressionIntervalSettlementExecutionResult
        IntervalRejected(
            ProgressionIntervalConflict conflict,
            ProgressionIntervalProjection? projection)
    {
        if (conflict == ProgressionIntervalConflict.None)
        {
            throw new ArgumentOutOfRangeException(nameof(conflict));
        }

        return new(
            ProgressionIntervalSettlementDisposition.IntervalConflict,
            Projection: projection,
            Conflict: conflict);
    }
}
