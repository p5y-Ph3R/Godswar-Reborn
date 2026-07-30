using System.Diagnostics.Metrics;
using System.Text.Json;
using Godswar.Server.Networking;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ServerRuntimeProfileChecks
{
    private const string RuntimeEnvironment =
        "GODSWAR_RUNTIME_PROFILE";
    private const string StorageEnvironment =
        "GODSWAR_STORAGE_PROVIDER";
    private const string SecureEnvironment =
        "GODSWAR_SECURE_ENABLED";

    public static Task RunAsync()
    {
        CheckPolicyMatrix();
        CheckMissingFileDoesNotCreateDefaults();
        CheckStrictEnvironmentOverrides();
        CheckCheckpointConfiguration();
        CheckCheckedInProfiles();
        CheckMetrics();
        return Task.CompletedTask;
    }

    private static void CheckPolicyMatrix()
    {
        ExpectReason(
            new ServerOptions(),
            ServerStartupRejectionReason.RuntimeProfileMissing,
            "missing runtime profile");

        var unknownRuntime = LocalJson();
        unknownRuntime.RuntimeProfile = "1";
        ExpectReason(
            unknownRuntime,
            ServerStartupRejectionReason.RuntimeProfileUnknown,
            "numeric runtime profile");

        var missingStorage = LocalJson();
        missingStorage.Storage.Provider = "";
        ExpectReason(
            missingStorage,
            ServerStartupRejectionReason.StorageProviderMissing,
            "missing storage provider");

        var nullStorage = LocalJson();
        nullStorage.Storage = null!;
        ExpectReason(
            nullStorage,
            ServerStartupRejectionReason.StorageProviderMissing,
            "null storage section");

        var unknownStorage = LocalJson();
        unknownStorage.Storage.Provider = "document";
        ExpectReason(
            unknownStorage,
            ServerStartupRejectionReason.StorageProviderUnknown,
            "unknown storage provider");

        var productionJson = LocalJson();
        productionJson.RuntimeProfile = "Production";
        productionJson.Secure.Enabled = true;
        ExpectReason(
            productionJson,
            ServerStartupRejectionReason.JsonStorageForbidden,
            "production JSON");

        var postgresWithoutConnection = LocalJson();
        postgresWithoutConnection.Storage.Provider = "Postgres";
        ExpectReason(
            postgresWithoutConnection,
            ServerStartupRejectionReason.PostgresConnectionMissing,
            "PostgreSQL without connection string");

        var productionRaw = Postgres(
            "Production",
            secure: false);
        ExpectReason(
            productionRaw,
            ServerStartupRejectionReason.RawTransportForbidden,
            "production raw transport");

        AssertAccepted(
            LocalJson(),
            ServerRuntimeProfileKind.LocalDevelopment,
            GameStorageProviderKind.Json,
            ServerListenerTransport.RawTcp,
            legacyAuthentication: true,
            "local JSON raw");
        AssertAccepted(
            Postgres("LocalDevelopment", secure: false),
            ServerRuntimeProfileKind.LocalDevelopment,
            GameStorageProviderKind.Postgres,
            ServerListenerTransport.RawTcp,
            legacyAuthentication: true,
            "local PostgreSQL raw");

        var localSecure = LocalJson();
        localSecure.Secure.Enabled = true;
        AssertAccepted(
            localSecure,
            ServerRuntimeProfileKind.LocalDevelopment,
            GameStorageProviderKind.Json,
            ServerListenerTransport.SecureTls,
            legacyAuthentication: false,
            "local secure JSON");
        AssertAccepted(
            Postgres("Production", secure: true),
            ServerRuntimeProfileKind.Production,
            GameStorageProviderKind.Postgres,
            ServerListenerTransport.SecureTls,
            legacyAuthentication: false,
            "production secure PostgreSQL");
    }

    private static void CheckMissingFileDoesNotCreateDefaults()
    {
        var directory = NewTemporaryDirectory();
        var path = Path.Combine(directory, "absent.json");
        try
        {
            ExpectLoadReason(
                path,
                ServerStartupRejectionReason.OptionsFileMissing,
                "missing options file");
            Check.True(
                !File.Exists(path),
                "missing options file is not generated");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckStrictEnvironmentOverrides()
    {
        var directory = NewTemporaryDirectory();
        var path = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(
            path,
            """
            {
              "runtimeProfile": "LocalDevelopment",
              "storage": {
                "provider": "Json"
              }
            }
            """);
        var previousRuntime =
            Environment.GetEnvironmentVariable(RuntimeEnvironment);
        var previousStorage =
            Environment.GetEnvironmentVariable(StorageEnvironment);
        var previousSecure =
            Environment.GetEnvironmentVariable(SecureEnvironment);
        try
        {
            Environment.SetEnvironmentVariable(
                RuntimeEnvironment,
                "unknown");
            ExpectLoadReason(
                path,
                ServerStartupRejectionReason.RuntimeProfileUnknown,
                "unknown runtime environment override");

            Environment.SetEnvironmentVariable(
                RuntimeEnvironment,
                "LocalDevelopment");
            Environment.SetEnvironmentVariable(
                StorageEnvironment,
                "unknown");
            ExpectLoadReason(
                path,
                ServerStartupRejectionReason.StorageProviderUnknown,
                "unknown storage environment override");

            Environment.SetEnvironmentVariable(
                StorageEnvironment,
                "Json");
            Environment.SetEnvironmentVariable(
                SecureEnvironment,
                "tru");
            Check.Throws<InvalidDataException>(
                () => ServerOptions.Load(path),
                "malformed secure environment override");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                RuntimeEnvironment,
                previousRuntime);
            Environment.SetEnvironmentVariable(
                StorageEnvironment,
                previousStorage);
            Environment.SetEnvironmentVariable(
                SecureEnvironment,
                previousSecure);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckCheckedInProfiles()
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
            var configuration = document.RootElement;
            Check.True(
                configuration.GetProperty("runtimeProfile")
                    .GetString() == "LocalDevelopment",
                $"{name} explicitly names LocalDevelopment");
            Check.True(
                !string.IsNullOrWhiteSpace(
                    configuration.GetProperty("storage")
                        .GetProperty("provider")
                        .GetString()),
                $"{name} explicitly names a storage provider");
        }

        var baseCompose = File.ReadAllText(
            Path.Combine(root, "docker-compose.yml"));
        var secureCompose = File.ReadAllText(
            Path.Combine(root, "docker-compose.secure.yml"));
        Check.True(
            baseCompose.Contains(
                "GODSWAR_RUNTIME_PROFILE: LocalDevelopment",
                StringComparison.Ordinal),
            "base Compose explicitly names LocalDevelopment");
        Check.True(
            secureCompose.Contains(
                "GODSWAR_RUNTIME_PROFILE: LocalDevelopment",
                StringComparison.Ordinal),
            "secure Compose explicitly names LocalDevelopment");
    }

    private static void CheckMetrics()
    {
        var measurements =
            new List<(string Name, string Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, activeListener) =>
            {
                if (instrument.Meter.Name ==
                    ServerProfileMetrics.MeterName)
                {
                    activeListener.EnableMeasurementEvents(
                        instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) =>
                measurements.Add(
                    (
                        instrument.Name,
                        string.Join(
                            ",",
                            tags.ToArray().Select(
                                tag =>
                                    $"{tag.Key}={tag.Value}")))));
        listener.Start();

        ServerProfileMetrics.RecordStartupRejection(
            "runtime_profile_missing");
        ServerProfileMetrics.RecordLegacyAuthenticationAttempt(
            "login",
            "blocked");

        Check.True(
            measurements.Any(measurement =>
                measurement.Name ==
                    "godswar.server.startup.rejections" &&
                measurement.Tags ==
                    "reason=runtime_profile_missing"),
            "startup rejection metric has a bounded reason");
        Check.True(
            measurements.Any(measurement =>
                measurement.Name ==
                    "godswar.server.legacy_auth.attempts" &&
                measurement.Tags.Contains(
                    "endpoint=login",
                    StringComparison.Ordinal) &&
                measurement.Tags.Contains(
                    "outcome=blocked",
                    StringComparison.Ordinal)),
            "legacy authentication metric has bounded endpoint/outcome");
    }

    private static ServerOptions LocalJson() =>
        new()
        {
            RuntimeProfile = "LocalDevelopment",
            Storage = new StorageOptions
            {
                Provider = "Json"
            }
        };

    private static ServerOptions Postgres(
        string runtimeProfile,
        bool secure)
    {
        var options = new ServerOptions
        {
            RuntimeProfile = runtimeProfile,
            Storage = new StorageOptions
            {
                Provider = "Postgres",
                PostgresConnectionString =
                    "Host=127.0.0.1;Database=profile-check"
            }
        };
        options.Secure.Enabled = secure;
        return options;
    }

    private static void AssertAccepted(
        ServerOptions options,
        ServerRuntimeProfileKind runtimeProfile,
        GameStorageProviderKind storageProvider,
        ServerListenerTransport transport,
        bool legacyAuthentication,
        string description)
    {
        var result =
            ServerRuntimeProfilePolicy.Validate(options);
        Check.True(
            result.RuntimeProfile == runtimeProfile &&
            result.StorageProvider == storageProvider &&
            result.Transport == transport &&
            result.AllowsLegacyAuthentication ==
                legacyAuthentication,
            $"{description} profile");
        Check.True(
            (LegacyAuthenticationAccess.Create(result) is not null) ==
                legacyAuthentication,
            $"{description} legacy capability");
    }

    private static void ExpectReason(
        ServerOptions options,
        ServerStartupRejectionReason reason,
        string description)
    {
        try
        {
            ServerRuntimeProfilePolicy.Validate(options);
        }
        catch (ServerStartupConfigurationException ex)
            when (ex.Reason == reason)
        {
            Check.True(
                ServerRuntimeProfilePolicy.RejectionCode(ex.Reason)
                    .Length > 0,
                $"{description} stable rejection code");
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {reason}.");
    }

    private static void ExpectLoadReason(
        string path,
        ServerStartupRejectionReason reason,
        string description)
    {
        try
        {
            ServerOptions.Load(path);
        }
        catch (ServerStartupConfigurationException ex)
            when (ex.Reason == reason)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {reason}.");
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"godswar-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var candidate =
            new DirectoryInfo(Directory.GetCurrentDirectory());
        while (candidate is not null)
        {
            if (File.Exists(
                    Path.Combine(candidate.FullName, "GodswarServer.sln")))
            {
                return candidate.FullName;
            }
            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "The repository root could not be found.");
    }
}
