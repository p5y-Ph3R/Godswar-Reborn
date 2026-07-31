using System.Text.Json;
using System.Text.Json.Nodes;

namespace Godswar.Server.ProtocolChecks;

internal static class B17CoordinationConfigurationChecks
{
    public const string CheckName =
        "B17 Redis coordination configuration policy";

    private const string TestConnectionVariable =
        "GODSWAR_B17_CONFIG_REDIS";

    private static readonly string[] CoordinationEnvironmentVariables =
    [
        "GODSWAR_COORDINATION_PROVIDER",
        "GODSWAR_COORDINATION_ENVIRONMENT",
        "GODSWAR_REDIS_CONNECTION_STRING_ENVIRONMENT_VARIABLE",
        "GODSWAR_COORDINATION_CAPACITY",
        "GODSWAR_REDIS_MAXIMUM_CONCURRENT_OPERATIONS",
        "GODSWAR_REDIS_QUEUE_ADMISSION_TIMEOUT_MILLISECONDS",
        "GODSWAR_REDIS_OPERATION_TIMEOUT_MILLISECONDS",
        "GODSWAR_REDIS_CONNECT_TIMEOUT_MILLISECONDS",
        "GODSWAR_REDIS_CIRCUIT_FAILURE_THRESHOLD",
        "GODSWAR_REDIS_CIRCUIT_OPEN_MILLISECONDS",
        "GODSWAR_COORDINATION_SERVER_HEARTBEAT_SECONDS",
        "GODSWAR_COORDINATION_SERVER_TTL_SECONDS",
        "GODSWAR_COORDINATION_PLAYER_LEASE_RENEWAL_SECONDS",
        "GODSWAR_COORDINATION_PLAYER_LEASE_TTL_SECONDS",
        "GODSWAR_REDIS_REQUIRE_TLS",
        "GODSWAR_REDIS_DATABASE",
        TestConnectionVariable
    ];

    public static Task RunAsync()
    {
        using var environment =
            new EnvironmentVariableScope(
                CoordinationEnvironmentVariables);
        foreach (var variable in CoordinationEnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"godswar-b17-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            CheckLocalProviderNeedsNoRedis(directory);
            CheckRedisRequiresConnection(directory);
            CheckRedisRequiresPostgres(directory);
            CheckProductionRequiresTls(directory);
            CheckCapacityCoversRuntime(directory);
            CheckValidRedisTopology(directory);
            CheckFiniteLeaseAndHeartbeatBounds();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static void CheckLocalProviderNeedsNoRedis(string directory)
    {
        var path = WriteOptions(
            directory,
            "local.json",
            provider: "Local",
            storageProvider: "Json",
            requireTls: true,
            capacity: 4_096);
        var loaded = ServerOptions.Load(path);
        Check.Equal(
            string.Empty,
            loaded.Coordination.ConnectionString,
            "local provider neither reads nor requires a Redis secret");
    }

    private static void CheckRedisRequiresConnection(string directory)
    {
        var path = WriteOptions(
            directory,
            "missing-secret.json",
            provider: "Redis",
            storageProvider: "Postgres",
            requireTls: false,
            capacity: 4_096);
        ExpectInvalidData(
            () => ServerOptions.Load(path),
            "requires its connection string",
            "Redis provider fails closed without its indirect secret");
    }

    private static void CheckRedisRequiresPostgres(string directory)
    {
        Environment.SetEnvironmentVariable(
            TestConnectionVariable,
            "127.0.0.1:6379,ssl=False");
        var path = WriteOptions(
            directory,
            "json-owner.json",
            provider: "Redis",
            storageProvider: "Json",
            requireTls: false,
            capacity: 4_096);
        ExpectInvalidData(
            () => ServerOptions.Load(path),
            "requires PostgreSQL durable ownership fences",
            "Redis cannot replace the PostgreSQL durable ownership fence");
    }

    private static void CheckProductionRequiresTls(string directory)
    {
        var path = WriteOptions(
            directory,
            "production-no-tls.json",
            provider: "Redis",
            storageProvider: "Postgres",
            requireTls: false,
            capacity: 4_096,
            runtimeProfile: "Production");
        ExpectInvalidData(
            () => ServerOptions.Load(path),
            "requires TLS",
            "production Redis coordination requires transport security");
    }

    private static void CheckCapacityCoversRuntime(string directory)
    {
        var path = WriteOptions(
            directory,
            "undersized.json",
            provider: "Redis",
            storageProvider: "Postgres",
            requireTls: false,
            capacity: 512,
            maximumConnections: 1_024);
        ExpectInvalidData(
            () => ServerOptions.Load(path),
            "capacity must cover",
            "coordination capacity covers the configured connection bound");
    }

    private static void CheckValidRedisTopology(string directory)
    {
        var path = WriteOptions(
            directory,
            "valid.json",
            provider: "Redis",
            storageProvider: "Postgres",
            requireTls: false,
            capacity: 4_096);
        var loaded = ServerOptions.Load(path);
        Check.True(
            loaded.Coordination.ProviderKind ==
                CoordinationProviderKind.Redis,
            "valid local-development Redis topology is accepted");
        Check.Equal(
            "127.0.0.1:6379,ssl=False",
            loaded.Coordination.ConnectionString,
            "connection material is resolved indirectly at startup");
    }

    private static void CheckFiniteLeaseAndHeartbeatBounds()
    {
        ExpectInvalidData(
            () => new CoordinationRuntimeOptions
            {
                ServerHeartbeatSeconds = 5,
                ServerTtlSeconds = 10
            }.NormalizeAndValidate(),
            "server TTL must exceed twice",
            "worker TTL must exceed two heartbeat intervals");
        ExpectInvalidData(
            () => new CoordinationRuntimeOptions
            {
                PlayerLeaseRenewalSeconds = 10,
                PlayerLeaseTtlSeconds = 20
            }.NormalizeAndValidate(),
            "Player lease TTL must exceed twice",
            "player TTL must exceed two renewal intervals");
    }

    private static void ExpectInvalidData(
        Action action,
        string expectedMessage,
        string description)
    {
        try
        {
            action();
        }
        catch (InvalidDataException error)
        {
            Check.True(
                error.Message.Contains(
                    expectedMessage,
                    StringComparison.OrdinalIgnoreCase),
                $"{description} reports its exact policy");
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected InvalidDataException.");
    }

    private static string WriteOptions(
        string directory,
        string name,
        string provider,
        string storageProvider,
        bool requireTls,
        int capacity,
        int maximumConnections = 512,
        string runtimeProfile = "LocalDevelopment")
    {
        var root = JsonNode.Parse(
            File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "appsettings.json")))!.AsObject();
        root["runtimeProfile"] = runtimeProfile;
        root["network"]!["maxActiveConnections"] = maximumConnections;
        root["authentication"]!["allowLegacyRawAuthentication"] = true;
        root["storage"]!["provider"] = storageProvider;
        root["storage"]!["postgresConnectionString"] =
            storageProvider == "Postgres"
                ? "Host=127.0.0.1;Database=b17_config"
                : string.Empty;
        root["coordination"] = new JsonObject
        {
            ["provider"] = provider,
            ["environment"] = "b17-config",
            ["connectionStringEnvironmentVariable"] =
                TestConnectionVariable,
            ["capacity"] = capacity,
            ["maximumConcurrentOperations"] = 16,
            ["queueAdmissionTimeoutMilliseconds"] = 25,
            ["operationTimeoutMilliseconds"] = 250,
            ["connectTimeoutMilliseconds"] = 1000,
            ["circuitFailureThreshold"] = 5,
            ["circuitOpenMilliseconds"] = 1000,
            ["serverHeartbeatSeconds"] = 5,
            ["serverTtlSeconds"] = 20,
            ["playerLeaseRenewalSeconds"] = 10,
            ["playerLeaseTtlSeconds"] = 30,
            ["requireTls"] = requireTls,
            ["database"] = 0
        };
        var path = Path.Combine(directory, name);
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        return path;
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(
                        current.FullName,
                        "GodswarServer.sln")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _values;

        public EnvironmentVariableScope(IEnumerable<string> names)
        {
            _values = names.ToDictionary(
                static name => name,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
        }

        public void Dispose()
        {
            foreach (var value in _values)
            {
                Environment.SetEnvironmentVariable(
                    value.Key,
                    value.Value);
            }
        }
    }
}
