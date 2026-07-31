namespace Godswar.Server;

/// <summary>
/// Process-local limits for world-instance ownership. These limits are
/// deliberately bounded independently of transport and persistence capacity.
/// </summary>
internal sealed class WorldInstanceRuntimeOptions
{
    public int MaximumRuntimes { get; set; } = 256;

    public int MaximumPlayerAssignments { get; set; } = 4_096;

    public int MaximumRetiredInstanceIds { get; set; } = 65_536;

    public int DefaultOpenWorldPlayerCapacity { get; set; } = 512;

    public int MailboxCapacity { get; set; } = 1_024;

    public int OwnerInvocationTimeoutMilliseconds { get; set; } = 1_000;

    public int ShutdownDrainTimeoutMilliseconds { get; set; } = 5_000;

    public int MaximumFanoutConcurrency { get; set; } = 8;

    public TimeSpan OwnerInvocationTimeout =>
        TimeSpan.FromMilliseconds(OwnerInvocationTimeoutMilliseconds);

    public TimeSpan ShutdownDrainTimeout =>
        TimeSpan.FromMilliseconds(ShutdownDrainTimeoutMilliseconds);

    public void Validate()
    {
        RequireRange(
            MaximumRuntimes,
            1,
            65_536,
            nameof(MaximumRuntimes));
        RequireRange(
            MaximumPlayerAssignments,
            1,
            1_000_000,
            nameof(MaximumPlayerAssignments));
        RequireRange(
            MaximumRetiredInstanceIds,
            MaximumRuntimes,
            1_000_000,
            nameof(MaximumRetiredInstanceIds));
        RequireRange(
            DefaultOpenWorldPlayerCapacity,
            1,
            Math.Min(MaximumPlayerAssignments, 100_000),
            nameof(DefaultOpenWorldPlayerCapacity));
        RequireRange(
            MailboxCapacity,
            1,
            65_536,
            nameof(MailboxCapacity));
        RequireRange(
            OwnerInvocationTimeoutMilliseconds,
            10,
            120_000,
            nameof(OwnerInvocationTimeoutMilliseconds));
        RequireRange(
            ShutdownDrainTimeoutMilliseconds,
            10,
            120_000,
            nameof(ShutdownDrainTimeoutMilliseconds));
        RequireRange(
            MaximumFanoutConcurrency,
            1,
            128,
            nameof(MaximumFanoutConcurrency));
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
