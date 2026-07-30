namespace Godswar.Server.Application.Characters;

internal sealed class CharacterCheckpointWorkerOptions
{
    public int QueueCapacity { get; set; } = 1_024;

    public int WorkerCount { get; set; } = 4;

    public int DirectOperationConcurrency { get; set; } = 8;

    public int DirectAdmissionTimeoutMilliseconds { get; set; } = 1_000;

    public int CommandTimeoutMilliseconds { get; set; } = 5_000;

    public int BaseRetryDelayMilliseconds { get; set; } = 100;

    public int MaximumRetryDelayMilliseconds { get; set; } = 2_000;

    public int MaximumRetryAgeMilliseconds { get; set; } = 30_000;

    public int ShutdownDrainTimeoutMilliseconds { get; set; } = 10_000;

    public void Validate()
    {
        RequireRange(QueueCapacity, 1, 65_536, nameof(QueueCapacity));
        RequireRange(WorkerCount, 1, 64, nameof(WorkerCount));
        RequireRange(
            DirectOperationConcurrency,
            1,
            128,
            nameof(DirectOperationConcurrency));
        RequireRange(
            DirectAdmissionTimeoutMilliseconds,
            1,
            30_000,
            nameof(DirectAdmissionTimeoutMilliseconds));
        RequireRange(
            CommandTimeoutMilliseconds,
            10,
            120_000,
            nameof(CommandTimeoutMilliseconds));
        RequireRange(
            BaseRetryDelayMilliseconds,
            1,
            60_000,
            nameof(BaseRetryDelayMilliseconds));
        RequireRange(
            MaximumRetryDelayMilliseconds,
            BaseRetryDelayMilliseconds,
            300_000,
            nameof(MaximumRetryDelayMilliseconds));
        RequireRange(
            MaximumRetryAgeMilliseconds,
            BaseRetryDelayMilliseconds,
            3_600_000,
            nameof(MaximumRetryAgeMilliseconds));
        RequireRange(
            ShutdownDrainTimeoutMilliseconds,
            10,
            120_000,
            nameof(ShutdownDrainTimeoutMilliseconds));
    }

    public TimeSpan DirectAdmissionTimeout =>
        TimeSpan.FromMilliseconds(DirectAdmissionTimeoutMilliseconds);

    public TimeSpan CommandTimeout =>
        TimeSpan.FromMilliseconds(CommandTimeoutMilliseconds);

    public TimeSpan MaximumRetryAge =>
        TimeSpan.FromMilliseconds(MaximumRetryAgeMilliseconds);

    public TimeSpan ShutdownDrainTimeout =>
        TimeSpan.FromMilliseconds(ShutdownDrainTimeoutMilliseconds);

    public TimeSpan RetryDelay(int failureCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            failureCount);
        var exponent = Math.Min(failureCount - 1, 20);
        var multiplier = 1L << exponent;
        var delay = Math.Min(
            MaximumRetryDelayMilliseconds,
            checked(BaseRetryDelayMilliseconds * multiplier));
        return TimeSpan.FromMilliseconds(delay);
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
