namespace Godswar.Server.Infrastructure.Messaging;

internal sealed class PostgresOutboxDispatcherOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum events or deferred recovery decisions handled in one polling
    /// pass. Events are leased immediately before their consumer runs rather
    /// than reserving this entire amount up front.
    /// </summary>
    public int BatchSize { get; set; } = 32;

    public int PollIntervalMilliseconds { get; set; } = 250;

    public int LeaseMilliseconds { get; set; } = 30_000;

    public int MaximumDeliveryAttempts { get; set; } = 8;

    public int BaseRetryDelayMilliseconds { get; set; } = 500;

    public int MaximumRetryDelayMilliseconds { get; set; } = 30_000;

    public int GapRetryDelayMilliseconds { get; set; } = 1_000;

    public int CommandTimeoutMilliseconds { get; set; } = 5_000;

    public void Validate()
    {
        RequireRange(BatchSize, 1, 256, nameof(BatchSize));
        RequireRange(
            PollIntervalMilliseconds,
            50,
            60_000,
            nameof(PollIntervalMilliseconds));
        RequireRange(
            LeaseMilliseconds,
            1_000,
            300_000,
            nameof(LeaseMilliseconds));
        RequireRange(
            MaximumDeliveryAttempts,
            1,
            64,
            nameof(MaximumDeliveryAttempts));
        RequireRange(
            BaseRetryDelayMilliseconds,
            50,
            60_000,
            nameof(BaseRetryDelayMilliseconds));
        RequireRange(
            MaximumRetryDelayMilliseconds,
            BaseRetryDelayMilliseconds,
            300_000,
            nameof(MaximumRetryDelayMilliseconds));
        RequireRange(
            GapRetryDelayMilliseconds,
            50,
            60_000,
            nameof(GapRetryDelayMilliseconds));
        RequireRange(
            CommandTimeoutMilliseconds,
            100,
            30_000,
            nameof(CommandTimeoutMilliseconds));

        if (LeaseMilliseconds <= CommandTimeoutMilliseconds)
        {
            throw new InvalidDataException(
                $"{nameof(LeaseMilliseconds)} must exceed " +
                $"{nameof(CommandTimeoutMilliseconds)}.");
        }
    }

    public TimeSpan PollInterval =>
        TimeSpan.FromMilliseconds(PollIntervalMilliseconds);

    public TimeSpan Lease =>
        TimeSpan.FromMilliseconds(LeaseMilliseconds);

    public TimeSpan GapRetryDelay =>
        TimeSpan.FromMilliseconds(GapRetryDelayMilliseconds);

    public TimeSpan CommandTimeout =>
        TimeSpan.FromMilliseconds(CommandTimeoutMilliseconds);

    public TimeSpan RetryDelay(int attempt)
    {
        var safeAttempt = Math.Clamp(attempt, 1, 30);
        var multiplier = 1L << Math.Min(safeAttempt - 1, 20);
        var milliseconds = Math.Min(
            MaximumRetryDelayMilliseconds,
            checked(BaseRetryDelayMilliseconds * multiplier));
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static void RequireRange(
        int value,
        int minimum,
        int maximum,
        string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"{name} must be between {minimum} and {maximum}.");
        }
    }
}
