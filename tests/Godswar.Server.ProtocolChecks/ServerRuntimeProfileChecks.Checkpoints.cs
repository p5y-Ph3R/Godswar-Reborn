using System.Text.Json;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ServerRuntimeProfileChecks
{
    private static readonly KeyValuePair<string, string>[]
        CheckpointEnvironmentOverrides =
        [
            new(
                "GODSWAR_CHECKPOINT_QUEUE_CAPACITY",
                "2048"),
            new(
                "GODSWAR_CHECKPOINT_WORKER_COUNT",
                "3"),
            new(
                "GODSWAR_CHECKPOINT_DIRECT_OPERATION_CONCURRENCY",
                "5"),
            new(
                "GODSWAR_CHECKPOINT_DIRECT_ADMISSION_TIMEOUT_MILLISECONDS",
                "750"),
            new(
                "GODSWAR_CHECKPOINT_COMMAND_TIMEOUT_MILLISECONDS",
                "6000"),
            new(
                "GODSWAR_CHECKPOINT_BASE_RETRY_DELAY_MILLISECONDS",
                "80"),
            new(
                "GODSWAR_CHECKPOINT_MAXIMUM_RETRY_DELAY_MILLISECONDS",
                "1600"),
            new(
                "GODSWAR_CHECKPOINT_MAXIMUM_RETRY_AGE_MILLISECONDS",
                "25000"),
            new(
                "GODSWAR_CHECKPOINT_SHUTDOWN_DRAIN_TIMEOUT_MILLISECONDS",
                "9000")
        ];

    private static void CheckCheckpointConfiguration()
    {
        var directory = NewTemporaryDirectory();
        var path = Path.Combine(directory, "appsettings.json");
        var previous = CheckpointEnvironmentOverrides.ToDictionary(
            pair => pair.Key,
            pair => Environment.GetEnvironmentVariable(pair.Key));
        try
        {
            foreach (var pair in CheckpointEnvironmentOverrides)
            {
                Environment.SetEnvironmentVariable(pair.Key, null);
            }

            File.WriteAllText(
                path,
                """
                {
                  "runtimeProfile": "LocalDevelopment",
                  "storage": {
                    "provider": "Postgres",
                    "postgresConnectionString":
                      "Host=127.0.0.1;Database=profile-checkpoint-check",
                    "checkpoints": {
                      "queueCapacity": 0
                    }
                  },
                  "authentication": {
                    "allowLegacyRawAuthentication": true
                  },
                  "game": {
                    "worldInstances": {
                      "realmId": 1
                    }
                  }
                }
                """);
            Check.Throws<InvalidDataException>(
                () => ServerOptions.Load(path),
                "checkpoint JSON bounds fail startup");

            File.WriteAllText(
                path,
                """
                {
                  "runtimeProfile": "LocalDevelopment",
                  "storage": {
                    "provider": "Postgres",
                    "postgresConnectionString":
                      "Host=127.0.0.1;Database=profile-checkpoint-check"
                  },
                  "authentication": {
                    "allowLegacyRawAuthentication": true
                  },
                  "game": {
                    "worldInstances": {
                      "realmId": 1
                    }
                  }
                }
                """);
            foreach (var pair in CheckpointEnvironmentOverrides)
            {
                Environment.SetEnvironmentVariable(
                    pair.Key,
                    pair.Value);
            }

            var options = ServerOptions.Load(path);
            var checkpoints = options.Storage.Checkpoints;
            Check.True(
                checkpoints.QueueCapacity == 2048 &&
                checkpoints.WorkerCount == 3 &&
                checkpoints.DirectOperationConcurrency == 5 &&
                checkpoints.DirectAdmissionTimeoutMilliseconds == 750 &&
                checkpoints.CommandTimeoutMilliseconds == 6000 &&
                checkpoints.BaseRetryDelayMilliseconds == 80 &&
                checkpoints.MaximumRetryDelayMilliseconds == 1600 &&
                checkpoints.MaximumRetryAgeMilliseconds == 25000 &&
                checkpoints.ShutdownDrainTimeoutMilliseconds == 9000,
                "all checkpoint environment overrides bind");

            Environment.SetEnvironmentVariable(
                "GODSWAR_CHECKPOINT_QUEUE_CAPACITY",
                "invalid");
            Check.Throws<InvalidDataException>(
                () => ServerOptions.Load(path),
                "malformed checkpoint environment override");

            Environment.SetEnvironmentVariable(
                "GODSWAR_CHECKPOINT_QUEUE_CAPACITY",
                "0");
            Check.Throws<InvalidDataException>(
                () => ServerOptions.Load(path),
                "checkpoint environment bounds fail startup");

            CheckCheckedInCheckpointDefaults();
        }
        finally
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(
                    pair.Key,
                    pair.Value);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckCheckedInCheckpointDefaults()
    {
        var root = FindRepositoryRoot();
        foreach (var name in new[]
                 {
                     "appsettings.json",
                     "appsettings.docker.json"
                 })
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, name)));
            var checkpoints = document.RootElement
                .GetProperty("storage")
                .GetProperty("checkpoints");
            Check.True(
                checkpoints.GetProperty("queueCapacity").GetInt32() ==
                    1024 &&
                checkpoints.GetProperty("workerCount").GetInt32() == 4 &&
                checkpoints
                    .GetProperty("directOperationConcurrency")
                    .GetInt32() == 8 &&
                checkpoints
                    .GetProperty(
                        "directAdmissionTimeoutMilliseconds")
                    .GetInt32() == 1000 &&
                checkpoints
                    .GetProperty("commandTimeoutMilliseconds")
                    .GetInt32() == 5000 &&
                checkpoints
                    .GetProperty("baseRetryDelayMilliseconds")
                    .GetInt32() == 100 &&
                checkpoints
                    .GetProperty("maximumRetryDelayMilliseconds")
                    .GetInt32() == 2000 &&
                checkpoints
                    .GetProperty("maximumRetryAgeMilliseconds")
                    .GetInt32() == 30000 &&
                checkpoints
                    .GetProperty(
                        "shutdownDrainTimeoutMilliseconds")
                    .GetInt32() == 10000,
                $"{name} has explicit safe checkpoint defaults");
        }
    }
}
