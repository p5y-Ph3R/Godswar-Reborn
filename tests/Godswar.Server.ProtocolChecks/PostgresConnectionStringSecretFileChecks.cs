using System.Text;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresConnectionStringSecretFileChecks
{
    public const string CheckName =
        "PostgreSQL connection-string secret-file";

    private const string DirectEnvironment =
        ServerOptions.PostgresConnectionStringEnvironmentVariable;

    private const string FileEnvironment =
        ServerOptions.PostgresConnectionStringFileEnvironmentVariable;

    public static Task RunAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"godswar-postgres-secret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var optionsPath = Path.Combine(directory, "appsettings.json");
        var secretPath = Path.Combine(directory, "postgres.connection-string");
        var missingPath = Path.Combine(directory, "missing.secret");
        var previousDirect = Environment.GetEnvironmentVariable(
            DirectEnvironment);
        var previousFile = Environment.GetEnvironmentVariable(
            FileEnvironment);
        try
        {
            File.WriteAllText(
                optionsPath,
                """
                {
                  "runtimeProfile": "LocalDevelopment",
                  "storage": {
                    "provider": "Postgres",
                    "postgresConnectionString":
                      "Host=json;Database=profile-check"
                  },
                  "authentication": {
                    "allowLegacyRawAuthentication": true
                  }
                }
                """);

            ClearSources();
            Check.Equal(
                "Host=json;Database=profile-check",
                ServerOptions.Load(optionsPath)
                    .Storage.PostgresConnectionString,
                "JSON fallback remains valid without environment sources");

            Environment.SetEnvironmentVariable(
                DirectEnvironment,
                "Host=direct;Database=profile-check");
            Check.Equal(
                "Host=direct;Database=profile-check",
                ServerOptions.Load(optionsPath)
                    .Storage.PostgresConnectionString,
                "direct environment source remains supported");

            Environment.SetEnvironmentVariable(DirectEnvironment, null);
            WriteUtf8(secretPath, "Host=file;Database=profile-check");
            Environment.SetEnvironmentVariable(FileEnvironment, secretPath);
            Check.Equal(
                "Host=file;Database=profile-check",
                ServerOptions.Load(optionsPath)
                    .Storage.PostgresConnectionString,
                "absolute file source overrides the JSON fallback");

            WriteUtf8(
                secretPath,
                "Host=file;Database=terminal-newline\r\n");
            Check.Equal(
                "Host=file;Database=terminal-newline",
                ServerOptions.Load(optionsPath)
                    .Storage.PostgresConnectionString,
                "one terminal newline is removed");

            Environment.SetEnvironmentVariable(
                DirectEnvironment,
                "Host=direct;Database=conflict");
            ExpectInvalid(
                optionsPath,
                "mutually exclusive",
                "direct and file sources are mutually exclusive");

            Environment.SetEnvironmentVariable(DirectEnvironment, null);
            Environment.SetEnvironmentVariable(FileEnvironment, "   ");
            ExpectInvalid(
                optionsPath,
                "must not be blank",
                "blank configured file path is rejected");

            Environment.SetEnvironmentVariable(FileEnvironment, "relative.secret");
            ExpectInvalid(
                optionsPath,
                "absolute secret-file path",
                "relative file path is rejected");

            Environment.SetEnvironmentVariable(FileEnvironment, missingPath);
            ExpectInvalid(
                optionsPath,
                "must exist and contain between",
                "missing secret file is rejected");

            Environment.SetEnvironmentVariable(FileEnvironment, secretPath);
            File.WriteAllBytes(secretPath, []);
            ExpectInvalid(
                optionsPath,
                "must exist and contain between",
                "empty secret file is rejected");

            File.WriteAllBytes(secretPath, new byte[4_097]);
            ExpectInvalid(
                optionsPath,
                "must exist and contain between",
                "oversized secret file is rejected");

            File.WriteAllBytes(secretPath, [0xC3, 0x28]);
            ExpectInvalid(
                optionsPath,
                "could not be read",
                "invalid UTF-8 is rejected");

            WriteUtf8(secretPath, "Host=file;\nDatabase=embedded-newline");
            ExpectInvalid(
                optionsPath,
                "contains invalid",
                "embedded newline is rejected");

            WriteUtf8(secretPath, "Host=file;Database=nul\0suffix");
            ExpectInvalid(
                optionsPath,
                "contains invalid",
                "NUL content is rejected");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DirectEnvironment,
                previousDirect);
            Environment.SetEnvironmentVariable(
                FileEnvironment,
                previousFile);
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static void ClearSources()
    {
        Environment.SetEnvironmentVariable(DirectEnvironment, null);
        Environment.SetEnvironmentVariable(FileEnvironment, null);
    }

    private static void WriteUtf8(string path, string value) =>
        File.WriteAllText(path, value, new UTF8Encoding(false));

    private static void ExpectInvalid(
        string path,
        string expectedMessage,
        string description)
    {
        try
        {
            ServerOptions.Load(path);
        }
        catch (InvalidDataException exception)
        {
            Check.True(
                exception.Message.Contains(
                    expectedMessage,
                    StringComparison.Ordinal),
                $"{description} has the expected rejection message");
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected InvalidDataException.");
    }
}
