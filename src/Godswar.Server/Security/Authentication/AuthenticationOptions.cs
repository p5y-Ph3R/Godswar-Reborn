namespace Godswar.Server.Security.Authentication;

internal sealed class AuthenticationOptions
{
    public const int PasswordSaltBytes = 16;
    public const int PasswordHashBytes = 32;
    public const int MaximumPasswordBytes = 32;
    public const int MaximumUsernameBytes = 32;
    public const int DefaultIterations = 600_000;
    public const int DefaultMaximumConcurrentKdfs = 16;
    public const int HardMinimumStoredIterations = 100_000;
    public const int HardMaximumStoredIterations = 10_000_000;

    public int Iterations { get; set; } = DefaultIterations;

    public int MinimumStoredIterations { get; set; } =
        HardMinimumStoredIterations;

    public int MaximumStoredIterations { get; set; } = 2_000_000;

    /// <summary>
    /// Zero selects min(Environment.ProcessorCount, 16).
    /// </summary>
    public int MaximumConcurrentKdfs { get; set; }

    public int QueueCapacity { get; set; } = 64;

    public int QueueCredentialBytes { get; set; } = 8 * 1024;

    public int QueueAdmissionTimeoutMilliseconds { get; set; } = 250;

    public int OperationTimeoutMilliseconds { get; set; } = 5_000;

    public bool AllowRegistration { get; set; }

    public bool AllowPlaintextMigration { get; set; } = true;

    public void Validate()
    {
        if (MinimumStoredIterations is <
                HardMinimumStoredIterations or >
                HardMaximumStoredIterations)
        {
            throw new InvalidDataException(
                $"{nameof(MinimumStoredIterations)} must be between " +
                $"{HardMinimumStoredIterations} and {HardMaximumStoredIterations}.");
        }
        if (MaximumStoredIterations is <
                HardMinimumStoredIterations or >
                HardMaximumStoredIterations ||
            MaximumStoredIterations < MinimumStoredIterations)
        {
            throw new InvalidDataException(
                $"{nameof(MaximumStoredIterations)} must be between " +
                $"{nameof(MinimumStoredIterations)} and " +
                $"{HardMaximumStoredIterations}.");
        }
        if (Iterations < MinimumStoredIterations ||
            Iterations > MaximumStoredIterations)
        {
            throw new InvalidDataException(
                $"{nameof(Iterations)} must be inside the accepted stored-cost range.");
        }
        if (MaximumConcurrentKdfs is < 0 or >
                DefaultMaximumConcurrentKdfs)
        {
            throw new InvalidDataException(
                $"{nameof(MaximumConcurrentKdfs)} must be zero or between 1 and " +
                $"{DefaultMaximumConcurrentKdfs}.");
        }
        if (QueueCapacity is < 1 or > 4_096)
        {
            throw new InvalidDataException(
                $"{nameof(QueueCapacity)} must be between 1 and 4096.");
        }
        if (QueueCredentialBytes is < MaximumPasswordBytes or >
                1024 * 1024)
        {
            throw new InvalidDataException(
                $"{nameof(QueueCredentialBytes)} must be between " +
                $"{MaximumPasswordBytes} and 1048576.");
        }
        if (QueueAdmissionTimeoutMilliseconds is < 1 or > 5_000)
        {
            throw new InvalidDataException(
                $"{nameof(QueueAdmissionTimeoutMilliseconds)} must be between 1 and 5000.");
        }
        if (OperationTimeoutMilliseconds is < 1_000 or > 30_000 ||
            OperationTimeoutMilliseconds <= QueueAdmissionTimeoutMilliseconds)
        {
            throw new InvalidDataException(
                $"{nameof(OperationTimeoutMilliseconds)} must be between 1000 and " +
                $"30000 and exceed {nameof(QueueAdmissionTimeoutMilliseconds)}.");
        }
    }

    public AuthenticationPolicy Snapshot()
    {
        Validate();
        var concurrency = MaximumConcurrentKdfs == 0
            ? Math.Min(
                Math.Max(1, Environment.ProcessorCount),
                DefaultMaximumConcurrentKdfs)
            : MaximumConcurrentKdfs;

        return new AuthenticationPolicy(
            Iterations,
            MinimumStoredIterations,
            MaximumStoredIterations,
            concurrency,
            QueueCapacity,
            QueueCredentialBytes,
            TimeSpan.FromMilliseconds(QueueAdmissionTimeoutMilliseconds),
            TimeSpan.FromMilliseconds(OperationTimeoutMilliseconds),
            AllowRegistration,
            AllowPlaintextMigration);
    }
}

internal readonly record struct AuthenticationPolicy(
    int Iterations,
    int MinimumStoredIterations,
    int MaximumStoredIterations,
    int MaximumConcurrentKdfs,
    int QueueCapacity,
    int QueueCredentialBytes,
    TimeSpan QueueAdmissionTimeout,
    TimeSpan OperationTimeout,
    bool AllowRegistration,
    bool AllowPlaintextMigration);
