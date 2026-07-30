namespace Godswar.Server.Application.Progression;

internal readonly record struct ProgressionIntervalAuthorityState(
    Guid OnlineSessionId,
    long LastIntervalSequence,
    DateTimeOffset LastIntervalEndUtc,
    long AggregateRevision);

internal static class ProgressionIntervalSettlementPolicy
{
    public static ProgressionIntervalConflict ValidateNext(
        ProgressionIntervalSettlementCommand command,
        ProgressionIntervalAuthorityState? authority,
        DateTimeOffset? durableZodiacWatermark)
    {
        if (authority is null)
        {
            if (command.IntervalSequence != 1)
            {
                return ProgressionIntervalConflict.InvalidSequence;
            }

            return durableZodiacWatermark.HasValue &&
                command.OnlineFromUtc < durableZodiacWatermark.Value
                ? ProgressionIntervalConflict.Overlap
                : ProgressionIntervalConflict.None;
        }

        var current = authority.Value;
        if (current.OnlineSessionId != command.OnlineSessionId)
        {
            if (command.IntervalSequence != 1)
            {
                return ProgressionIntervalConflict.StaleSession;
            }

            return command.OnlineFromUtc < current.LastIntervalEndUtc
                ? ProgressionIntervalConflict.Overlap
                : ProgressionIntervalConflict.None;
        }

        if (command.IntervalSequence != current.LastIntervalSequence + 1)
        {
            return ProgressionIntervalConflict.InvalidSequence;
        }

        if (command.OnlineFromUtc < current.LastIntervalEndUtc)
        {
            return ProgressionIntervalConflict.Overlap;
        }

        return command.OnlineFromUtc > current.LastIntervalEndUtc
            ? ProgressionIntervalConflict.Gap
            : ProgressionIntervalConflict.None;
    }
}
