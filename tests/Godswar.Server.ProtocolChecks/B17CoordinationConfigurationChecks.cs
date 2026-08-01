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
        CoordinationRuntimeOptions.DefaultConnectionStringEnvironmentVariable,
        CoordinationRuntimeOptions.ConnectionStringFileEnvironmentVariable,
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
            CheckRedisConnectionSecretFile(directory);
            CheckRedisConnectionSourcesAreExclusive(directory);
            CheckRedisConnectionSecretFileBounds(directory);
            CheckRedisRequiresPostgres(directory);
            CheckProductionRequiresTls(directory);
            CheckCapacityCoversRuntime(directory);
            CheckValidRedisTopology(directory);
            CheckStagedMainWorkerTopology(directory);
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
            storageProvider: "Postgres",
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

    private static void CheckRedisConnectionSecretFile(string directory)
    {
        var secretPath = Path.Combine(directory, "redis.connection-string");
        File.WriteAllText(
            secretPath,
            "redis-coordination:6379,ssl=False");
        Environment.SetEnvironmentVariable(
            CoordinationRuntimeOptions.ConnectionStringFileEnvironmentVariable,
            secretPath);
        try
        {
            var loaded = ServerOptions.Load(WriteOptions(
                directory,
                "secret-file.json",
                provider: "Redis",
                storageProvider: "Postgres",
                requireTls: false,
                capacity: 4_096));
            Check.Equal(
                "redis-coordination:6379,ssl=False",
                loaded.Coordination.ConnectionString,
                "Redis connection material is loaded from a bounded secret file");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CoordinationRuntimeOptions.ConnectionStringFileEnvironmentVariable,
                null);
        }
    }

    private static void CheckRedisConnectionSourcesAreExclusive(
        string directory)
    {
        var secretPath = Path.Combine(directory, "ambiguous.connection-string");
        File.WriteAllText(secretPath, "redis-coordination:6379,ssl=False");
        Environment.SetEnvironmentVariable(
            TestConnectionVariable,
            "127.0.0.1:6379,ssl=False");
        Environment.SetEnvironmentVariable(
            CoordinationRuntimeOptions.ConnectionStringFileEnvironmentVariable,
            secretPath);
        try
        {
            ExpectInvalidData(
                () => ServerOptions.Load(WriteOptions(
                    directory,
                    "ambiguous-secret.json",
                    provider: "Redis",
                    storageProvider: "Postgres",
                    requireTls: false,
                    capacity: 4_096)),
                "mutually exclusive",
                "Redis rejects ambiguous direct and secret-file sources");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CoordinationRuntimeOptions.ConnectionStringFileEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(TestConnectionVariable, null);
        }
    }

    private static void CheckRedisConnectionSecretFileBounds(string directory)
    {
        Environment.SetEnvironmentVariable(TestConnectionVariable, null);
        var missingPath = Path.Combine(directory, "missing.connection-string");
        Environment.SetEnvironmentVariable(
            CoordinationRuntimeOptions.ConnectionStringFileEnvironmentVariable,
            missingPath);
        ExpectInvalidData(
            () => ServerOptions.Load(WriteOptions(
                directory,
                "missing-secret-file.json",
                provider: "Redis",
                storageProvider: "Postgres",
                requireTls: false,
                capacity: 4_096)),
            "must exist",
            "Redis rejects a missing connection-string secret file");

        var oversizedPath = Path.Combine(directory, "oversized.connection-string");
        File.WriteAllText(oversizedPath, new string('x', 4_097));
        Environment.SetEnvironmentVariable(
            CoordinationRuntimeOptions.ConnectionStringFileEnvironmentVariable,
            oversizedPath);
        try
        {
            ExpectInvalidData(
                () => ServerOptions.Load(WriteOptions(
                    directory,
                    "oversized-secret-file.json",
                    provider: "Redis",
                    storageProvider: "Postgres",
                    requireTls: false,
                    capacity: 4_096)),
                "between 1 and 4096 bytes",
                "Redis rejects an oversized connection-string secret file");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CoordinationRuntimeOptions.ConnectionStringFileEnvironmentVariable,
                null);
        }
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

    private static void CheckStagedMainWorkerTopology(string directory)
    {
        Environment.SetEnvironmentVariable(TestConnectionVariable, null);
        var secretPath = Path.Combine(directory, "staged.connection-string");
        File.WriteAllText(
            secretPath,
            "redis-coordination:6379,ssl=False");
        Environment.SetEnvironmentVariable(
            CoordinationRuntimeOptions.ConnectionStringFileEnvironmentVariable,
            secretPath);
        try
        {
            var options = ServerOptions.Load(Path.Combine(
                FindRepositoryRoot(),
                "deploy",
                "local",
                "redis-coordinated-worker.json"));
            Check.Equal(
                "tempest-openworld-01",
                options.Game.WorldInstances.ServerNodeId,
                "staged worker has one stable node identity");
            Check.Equal(
                23,
                options.Game.WorldInstances.StaticOpenWorldInstances.Length,
                "staged worker owns all currently connected open-world maps");
            Check.True(
                options.Game.WorldInstances.StaticOpenWorldInstances
                    .Select(static route => (int)route.MapId)
                    .Order()
                    .SequenceEqual(Enumerable.Range(0, 23)),
                "staged worker owns exactly map IDs zero through twenty-two");
            Check.Equal(
                23,
                options.Game.WorldInstances.StaticOpenWorldInstances
                    .Select(static route => route.ProcessWorldInstanceId)
                    .Distinct()
                    .Count(),
                "staged worker route instance identities are unique");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CoordinationRuntimeOptions.ConnectionStringFileEnvironmentVariable,
                null);
        }
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
