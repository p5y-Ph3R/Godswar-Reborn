namespace Godswar.Server.ProtocolChecks;

internal static class WorldInstanceRuntimeOptionsChecks
{
    public const string CheckName =
        "B18B bounded world-instance runtime configuration";

    private static readonly string[] EnvironmentKeys =
    [
        "GODSWAR_WORLD_INSTANCE_REALM_ID",
        "GODSWAR_WORLD_INSTANCE_MAXIMUM_RUNTIMES",
        "GODSWAR_WORLD_INSTANCE_MAXIMUM_PLAYER_ASSIGNMENTS",
        "GODSWAR_WORLD_INSTANCE_MAXIMUM_RETIRED_INSTANCE_IDS",
        "GODSWAR_WORLD_INSTANCE_DEFAULT_OPEN_WORLD_PLAYER_CAPACITY",
        "GODSWAR_WORLD_INSTANCE_MAILBOX_CAPACITY",
        "GODSWAR_WORLD_INSTANCE_OWNER_INVOCATION_TIMEOUT_MILLISECONDS",
        "GODSWAR_WORLD_INSTANCE_SHUTDOWN_DRAIN_TIMEOUT_MILLISECONDS",
        "GODSWAR_WORLD_INSTANCE_MAXIMUM_FANOUT_CONCURRENCY",
        "GODSWAR_WORLD_INSTANCE_SERVER_NODE_ID",
        "GODSWAR_WORLD_INSTANCE_ROUTE_MANIFEST_FILE"
    ];

    public static Task RunAsync()
    {
        CheckDefaults();
        CheckValidation();
        CheckConfigurationBinding();
        return Task.CompletedTask;
    }

    private static void CheckDefaults()
    {
        var options = new WorldInstanceRuntimeOptions();
        Check.Throws<InvalidDataException>(
            options.Validate,
            "hosted realm is required");
        options.RealmId = 1;
        options.Validate();

        Check.Equal(1, options.RealmId, "configured realm ID");

        Check.Equal(
            "local-node",
            options.ServerNodeId,
            "default server node ID");
        Check.Equal(
            "local-node",
            options.ProcessServerNodeId.ToString(),
            "parsed default server node ID");
        Check.Equal(256, options.MaximumRuntimes, "maximum runtimes");
        Check.Equal(
            4_096,
            options.MaximumPlayerAssignments,
            "maximum player assignments");
        Check.Equal(
            65_536,
            options.MaximumRetiredInstanceIds,
            "maximum retired instance IDs");
        Check.Equal(
            512,
            options.DefaultOpenWorldPlayerCapacity,
            "default open-world player capacity");
        Check.Equal(1_024, options.MailboxCapacity, "mailbox capacity");
        Check.Equal(
            TimeSpan.FromSeconds(1),
            options.OwnerInvocationTimeout,
            "owner invocation timeout");
        Check.Equal(
            TimeSpan.FromSeconds(5),
            options.ShutdownDrainTimeout,
            "shutdown drain timeout");
        Check.Equal(
            8,
            options.MaximumFanoutConcurrency,
            "maximum fanout concurrency");
    }

    private static void CheckValidation()
    {
        CheckInvalid(
            options => options.ServerNodeId = "",
            "empty server node ID");
        CheckInvalid(
            options => options.ServerNodeId = "worker/node",
            "server node ID with unsupported punctuation");
        CheckInvalid(
            options => options.MaximumRuntimes = 0,
            "zero maximum runtimes");
        CheckInvalid(
            options => options.MaximumPlayerAssignments = 0,
            "zero maximum player assignments");
        CheckInvalid(
            options => options.MaximumRetiredInstanceIds = 128,
            "retired IDs below maximum runtimes");
        CheckInvalid(
            options => options.DefaultOpenWorldPlayerCapacity = 4_097,
            "open-world capacity above assignment bound");
        CheckInvalid(
            options => options.MailboxCapacity = 65_537,
            "oversized mailbox");
        CheckInvalid(
            options => options.OwnerInvocationTimeoutMilliseconds = 9,
            "undersized owner invocation timeout");
        CheckInvalid(
            options => options.ShutdownDrainTimeoutMilliseconds = 120_001,
            "oversized shutdown timeout");
        CheckInvalid(
            options => options.MaximumFanoutConcurrency = 129,
            "oversized fanout concurrency");
        CheckInvalid(
            options => options.StaticOpenWorldInstances =
            [
                new StaticOpenWorldInstanceOptions
                {
                    RealmId = 2,
                    MapId = 0,
                    WorldInstanceId = Guid.NewGuid().ToString()
                }
            ],
            "route from another realm");
    }

    private static void CheckConfigurationBinding()
    {
        var previous = EnvironmentKeys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"godswar-world-instance-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            ClearEnvironment();
            var path = WriteOptions(directory);
            var fromJson = ServerOptions.Load(path).Game.WorldInstances;
            AssertValues(
                fromJson,
                1,
                32,
                1_000,
                2_048,
                128,
                256,
                500,
                2_000,
                4,
                "postgres-worker-01",
                "PostgreSQL configuration");

            var routeManifest = Path.Combine(
                directory,
                "dwargon-routes.json");
            File.WriteAllText(
                routeManifest,
                """
                [
                  {
                    "realmId": 2,
                    "mapId": 0,
                    "worldInstanceId":
                      "22222222-2222-4222-8222-222222222222"
                  }
                ]
                """);

            var environmentValues = new[]
            {
                "2", "64", "2000", "4096", "256",
                "512", "750", "3000", "6",
                "env-worker-02", routeManifest
            };
            for (var index = 0; index < EnvironmentKeys.Length; index++)
            {
                Environment.SetEnvironmentVariable(
                    EnvironmentKeys[index],
                    environmentValues[index]);
            }

            var fromEnvironment =
                ServerOptions.Load(path).Game.WorldInstances;
            AssertValues(
                fromEnvironment,
                2,
                64,
                2_000,
                4_096,
                256,
                512,
                750,
                3_000,
                6,
                "env-worker-02",
                "environment");
            Check.True(
                fromEnvironment.StaticOpenWorldInstances is
                    [
                        {
                            RealmId: 2,
                            MapId: 0,
                            WorldInstanceId:
                                "22222222-2222-4222-8222-222222222222"
                        }
                    ],
                "environment route manifest replaces configured routes");

            Environment.SetEnvironmentVariable(
                EnvironmentKeys[1],
                "not-an-integer");
            Check.Throws<InvalidDataException>(
                () => ServerOptions.Load(path),
                "invalid environment integer fails startup");
        }
        finally
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckInvalid(
        Action<WorldInstanceRuntimeOptions> mutate,
        string name)
    {
        var options = new WorldInstanceRuntimeOptions { RealmId = 1 };
        mutate(options);
        Check.Throws<InvalidDataException>(
            options.Validate,
            name);
    }

    private static void AssertValues(
        WorldInstanceRuntimeOptions options,
        int realmId,
        int maximumRuntimes,
        int maximumPlayerAssignments,
        int maximumRetiredInstanceIds,
        int defaultOpenWorldPlayerCapacity,
        int mailboxCapacity,
        int ownerInvocationTimeoutMilliseconds,
        int shutdownDrainTimeoutMilliseconds,
        int maximumFanoutConcurrency,
        string serverNodeId,
        string source)
    {
        Check.Equal(realmId, options.RealmId, $"{source} realm ID");
        Check.Equal(
            serverNodeId,
            options.ServerNodeId,
            $"{source} server node ID");
        Check.Equal(
            serverNodeId,
            options.ProcessServerNodeId.ToString(),
            $"{source} parsed server node ID");
        Check.Equal(
            maximumRuntimes,
            options.MaximumRuntimes,
            $"{source} maximum runtimes");
        Check.Equal(
            maximumPlayerAssignments,
            options.MaximumPlayerAssignments,
            $"{source} maximum player assignments");
        Check.Equal(
            maximumRetiredInstanceIds,
            options.MaximumRetiredInstanceIds,
            $"{source} maximum retired IDs");
        Check.Equal(
            defaultOpenWorldPlayerCapacity,
            options.DefaultOpenWorldPlayerCapacity,
            $"{source} default open-world capacity");
        Check.Equal(
            mailboxCapacity,
            options.MailboxCapacity,
            $"{source} mailbox capacity");
        Check.Equal(
            ownerInvocationTimeoutMilliseconds,
            options.OwnerInvocationTimeoutMilliseconds,
            $"{source} owner invocation timeout");
        Check.Equal(
            shutdownDrainTimeoutMilliseconds,
            options.ShutdownDrainTimeoutMilliseconds,
            $"{source} shutdown drain timeout");
        Check.Equal(
            maximumFanoutConcurrency,
            options.MaximumFanoutConcurrency,
            $"{source} maximum fanout concurrency");
    }

    private static void ClearEnvironment()
    {
        foreach (var key in EnvironmentKeys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private static string WriteOptions(string directory)
    {
        var path = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(
            path,
            """
            {
              "runtimeProfile": "LocalDevelopment",
              "storage": {
                "provider": "Postgres",
                "postgresConnectionString":
                  "Host=127.0.0.1;Database=world-instance-options-check"
              },
              "authentication": {
                "allowLegacyRawAuthentication": true
              },
              "game": {
                "worldInstances": {
                  "realmId": 1,
                  "serverNodeId": "postgres-worker-01",
                  "maximumRuntimes": 32,
                  "maximumPlayerAssignments": 1000,
                  "maximumRetiredInstanceIds": 2048,
                  "defaultOpenWorldPlayerCapacity": 128,
                  "mailboxCapacity": 256,
                  "ownerInvocationTimeoutMilliseconds": 500,
                  "shutdownDrainTimeoutMilliseconds": 2000,
                  "maximumFanoutConcurrency": 4
                }
              }
            }
            """);
        return path;
    }
}
