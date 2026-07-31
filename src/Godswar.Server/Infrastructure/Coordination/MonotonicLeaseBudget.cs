namespace Godswar.Server.Infrastructure.Coordination;

/// <summary>
/// Conservative process-local lease budget. Capture it before the remote
/// lease operation starts so network latency can only shorten local use.
/// Absolute authority timestamps remain available for telemetry.
/// </summary>
internal readonly record struct MonotonicLeaseBudget(
    long StartedAtTimestamp,
    TimeSpan Lifetime)
{
    public static MonotonicLeaseBudget Capture(
        TimeProvider timeProvider,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        return new(timeProvider.GetTimestamp(), lifetime);
    }

    public bool IsCurrent(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (Lifetime <= TimeSpan.Zero)
        {
            return false;
        }

        var elapsed =
            timeProvider.GetElapsedTime(StartedAtTimestamp);
        return elapsed >= TimeSpan.Zero && elapsed < Lifetime;
    }
}
