using System.Text;
using System.Text.Json.Serialization;

namespace Godswar.Server;

internal enum CoordinationProviderKind : byte
{
    Local = 1,
    Redis = 2
}

/// <summary>
/// Bounded, disposable cross-process coordination settings. PostgreSQL
/// remains authoritative for player value and ownership generations.
/// </summary>
internal sealed class CoordinationRuntimeOptions
{
    public const string DefaultConnectionStringEnvironmentVariable =
        "GODSWAR_REDIS_CONNECTION_STRING";

    public const string ConnectionStringFileEnvironmentVariable =
        "GODSWAR_REDIS_CONNECTION_STRING_FILE";

    private const int MaximumConnectionStringFileBytes = 4_096;

    public string Provider { get; set; } = "Local";

    public string Environment { get; set; } = "development";

    public string ConnectionStringEnvironmentVariable { get; set; } =
        DefaultConnectionStringEnvironmentVariable;

    public int Database { get; set; }

    public int Capacity { get; set; } = 4_096;

    public int MaximumConcurrentOperations { get; set; } = 128;

    public int QueueAdmissionTimeoutMilliseconds { get; set; } = 25;

    public int OperationTimeoutMilliseconds { get; set; } = 250;

    public int ConnectTimeoutMilliseconds { get; set; } = 1_000;

    public int CircuitFailureThreshold { get; set; } = 5;

    public int CircuitOpenMilliseconds { get; set; } = 5_000;

    public int ServerHeartbeatSeconds { get; set; } = 5;

    public int ServerTtlSeconds { get; set; } = 20;

    public int PlayerLeaseRenewalSeconds { get; set; } = 10;

    public int PlayerLeaseTtlSeconds { get; set; } = 30;

    public bool RequireTls { get; set; } = true;

    [JsonIgnore]
    public string ConnectionString { get; private set; } = string.Empty;

    [JsonIgnore]
    public CoordinationProviderKind ProviderKind =>
        ParseProvider(Provider);

    public TimeSpan QueueAdmissionTimeout =>
        TimeSpan.FromMilliseconds(
            QueueAdmissionTimeoutMilliseconds);

    public TimeSpan OperationTimeout =>
        TimeSpan.FromMilliseconds(OperationTimeoutMilliseconds);

    public TimeSpan CircuitOpenDuration =>
        TimeSpan.FromMilliseconds(CircuitOpenMilliseconds);

    public TimeSpan ServerHeartbeat =>
        TimeSpan.FromSeconds(ServerHeartbeatSeconds);

    public TimeSpan ServerTtl =>
        TimeSpan.FromSeconds(ServerTtlSeconds);

    public TimeSpan PlayerLeaseRenewal =>
        TimeSpan.FromSeconds(PlayerLeaseRenewalSeconds);

    public TimeSpan PlayerLeaseTtl =>
        TimeSpan.FromSeconds(PlayerLeaseTtlSeconds);

    public void ApplyEnvironment()
    {
        Provider =
            System.Environment.GetEnvironmentVariable(
                "GODSWAR_COORDINATION_PROVIDER") ??
            Provider;
        Environment =
            System.Environment.GetEnvironmentVariable(
                "GODSWAR_COORDINATION_ENVIRONMENT") ??
            Environment;
        ConnectionStringEnvironmentVariable =
            System.Environment.GetEnvironmentVariable(
                "GODSWAR_REDIS_CONNECTION_STRING_ENVIRONMENT_VARIABLE") ??
            ConnectionStringEnvironmentVariable;
        Capacity = ReadInt(
            "GODSWAR_COORDINATION_CAPACITY",
            Capacity);
        MaximumConcurrentOperations = ReadInt(
            "GODSWAR_REDIS_MAXIMUM_CONCURRENT_OPERATIONS",
            MaximumConcurrentOperations);
        QueueAdmissionTimeoutMilliseconds = ReadInt(
            "GODSWAR_REDIS_QUEUE_ADMISSION_TIMEOUT_MILLISECONDS",
            QueueAdmissionTimeoutMilliseconds);
        OperationTimeoutMilliseconds = ReadInt(
            "GODSWAR_REDIS_OPERATION_TIMEOUT_MILLISECONDS",
            OperationTimeoutMilliseconds);
        ConnectTimeoutMilliseconds = ReadInt(
            "GODSWAR_REDIS_CONNECT_TIMEOUT_MILLISECONDS",
            ConnectTimeoutMilliseconds);
        CircuitFailureThreshold = ReadInt(
            "GODSWAR_REDIS_CIRCUIT_FAILURE_THRESHOLD",
            CircuitFailureThreshold);
        CircuitOpenMilliseconds = ReadInt(
            "GODSWAR_REDIS_CIRCUIT_OPEN_MILLISECONDS",
            CircuitOpenMilliseconds);
        ServerHeartbeatSeconds = ReadInt(
            "GODSWAR_COORDINATION_SERVER_HEARTBEAT_SECONDS",
            ServerHeartbeatSeconds);
        ServerTtlSeconds = ReadInt(
            "GODSWAR_COORDINATION_SERVER_TTL_SECONDS",
            ServerTtlSeconds);
        PlayerLeaseRenewalSeconds = ReadInt(
            "GODSWAR_COORDINATION_PLAYER_LEASE_RENEWAL_SECONDS",
            PlayerLeaseRenewalSeconds);
        PlayerLeaseTtlSeconds = ReadInt(
            "GODSWAR_COORDINATION_PLAYER_LEASE_TTL_SECONDS",
            PlayerLeaseTtlSeconds);
        RequireTls = ReadBool(
            "GODSWAR_REDIS_REQUIRE_TLS",
            RequireTls);
        Database = ReadInt(
            "GODSWAR_REDIS_DATABASE",
            Database);
    }

    public void NormalizeAndValidate()
    {
        Provider = (Provider ?? string.Empty).Trim();
        Environment = (Environment ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        ConnectionStringEnvironmentVariable =
            (ConnectionStringEnvironmentVariable ?? string.Empty).Trim();

        _ = ProviderKind;
        RequireToken(
            Environment,
            32,
            nameof(Environment));
        RequireEnvironmentVariableName(
            ConnectionStringEnvironmentVariable);
        RequireRange(Database, 0, 15, nameof(Database));
        RequireRange(Capacity, 512, 100_000, nameof(Capacity));
        RequireRange(
            MaximumConcurrentOperations,
            1,
            1_024,
            nameof(MaximumConcurrentOperations));
        RequireRange(
            QueueAdmissionTimeoutMilliseconds,
            1,
            1_000,
            nameof(QueueAdmissionTimeoutMilliseconds));
        RequireRange(
            OperationTimeoutMilliseconds,
            10,
            5_000,
            nameof(OperationTimeoutMilliseconds));
        RequireRange(
            ConnectTimeoutMilliseconds,
            100,
            30_000,
            nameof(ConnectTimeoutMilliseconds));
        RequireRange(
            CircuitFailureThreshold,
            1,
            100,
            nameof(CircuitFailureThreshold));
        RequireRange(
            CircuitOpenMilliseconds,
            100,
            60_000,
            nameof(CircuitOpenMilliseconds));
        RequireRange(
            ServerHeartbeatSeconds,
            1,
            30,
            nameof(ServerHeartbeatSeconds));
        RequireRange(
            ServerTtlSeconds,
            5,
            120,
            nameof(ServerTtlSeconds));
        RequireRange(
            PlayerLeaseRenewalSeconds,
            1,
            30,
            nameof(PlayerLeaseRenewalSeconds));
        RequireRange(
            PlayerLeaseTtlSeconds,
            5,
            120,
            nameof(PlayerLeaseTtlSeconds));
        if (ServerHeartbeatSeconds * 2 >= ServerTtlSeconds)
        {
            throw new InvalidDataException(
                "Coordination server TTL must exceed twice its heartbeat.");
        }
        if (PlayerLeaseRenewalSeconds * 2 >= PlayerLeaseTtlSeconds)
        {
            throw new InvalidDataException(
                "Player lease TTL must exceed twice its renewal interval.");
        }

        ConnectionString = ProviderKind == CoordinationProviderKind.Redis
            ? ResolveRedisConnectionString()
            : string.Empty;
        if (ProviderKind == CoordinationProviderKind.Redis &&
            string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidDataException(
                "Redis coordination requires its connection string in " +
                ConnectionStringEnvironmentVariable + ".");
        }
    }

    private string ResolveRedisConnectionString()
    {
        var direct = System.Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);
        var filePath = System.Environment.GetEnvironmentVariable(
            ConnectionStringFileEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(direct) &&
            !string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidDataException(
                "Redis coordination connection-string environment and " +
                "secret-file sources are mutually exclusive.");
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return direct ?? string.Empty;
        }
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new InvalidDataException(
                $"{ConnectionStringFileEnvironmentVariable} must contain " +
                "an absolute secret-file path.");
        }

        try
        {
            var bytes = new byte[MaximumConnectionStringFileBytes + 1];
            var byteCount = 0;
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: MaximumConnectionStringFileBytes,
                    FileOptions.SequentialScan);
                while (byteCount < bytes.Length)
                {
                    var read = stream.Read(
                        bytes,
                        byteCount,
                        bytes.Length - byteCount);
                    if (read == 0)
                    {
                        break;
                    }
                    byteCount += read;
                }
                if (byteCount is < 1 or > MaximumConnectionStringFileBytes)
                {
                    throw new InvalidDataException(
                        "Redis coordination secret file must exist and " +
                        $"contain between 1 and " +
                        $"{MaximumConnectionStringFileBytes} bytes.");
                }

                var value = new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true)
                    .GetString(bytes, 0, byteCount)
                    .Trim();
                if (value.Length == 0 ||
                    value.Any(character =>
                        character is '\0' or '\r' or '\n'))
                {
                    throw new InvalidDataException(
                        "Redis coordination secret file contains invalid " +
                        "connection-string content.");
                }

                return value;
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or
                DirectoryNotFoundException)
        {
            throw new InvalidDataException(
                "Redis coordination secret file must exist and contain " +
                $"between 1 and {MaximumConnectionStringFileBytes} bytes.",
                exception);
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                DecoderFallbackException or
                NotSupportedException)
        {
            throw new InvalidDataException(
                "Redis coordination secret file could not be read.",
                exception);
        }
    }

    private static CoordinationProviderKind ParseProvider(string provider)
    {
        if (Enum.TryParse<CoordinationProviderKind>(
                provider,
                ignoreCase: true,
                out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            "Coordination.Provider must be 'Local' or 'Redis'.");
    }

    private static int ReadInt(string name, int fallback)
    {
        var raw = System.Environment.GetEnvironmentVariable(name);
        if (raw is null)
        {
            return fallback;
        }
        if (int.TryParse(raw, out var value))
        {
            return value;
        }

        throw new InvalidDataException($"{name} must be an integer.");
    }

    private static bool ReadBool(string name, bool fallback)
    {
        var raw = System.Environment.GetEnvironmentVariable(name);
        if (raw is null)
        {
            return fallback;
        }
        if (bool.TryParse(raw, out var value))
        {
            return value;
        }

        throw new InvalidDataException($"{name} must be true or false.");
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
                $"Coordination.{name} must be between " +
                $"{minimum} and {maximum}.");
        }
    }

    private static void RequireToken(
        string value,
        int maximumLength,
        string name)
    {
        if (value.Length is < 1 ||
            value.Length > maximumLength ||
            value.Any(character =>
                character is not (
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or
                    '-' or '_')))
        {
            throw new InvalidDataException(
                $"Coordination.{name} must be a bounded lowercase token.");
        }
    }

    private static void RequireEnvironmentVariableName(string value)
    {
        if (value.Length is < 1 or > 96 ||
            value.Any(character =>
                character is not (
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or
                    '_')))
        {
            throw new InvalidDataException(
                "Coordination connection-string environment-variable " +
                "name is invalid.");
        }
    }
}
