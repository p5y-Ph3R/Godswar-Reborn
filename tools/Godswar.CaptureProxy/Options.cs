sealed record Options(
    string LoginHost,
    int LoginPort,
    int LocalLoginPort,
    int LocalGamePort,
    string LocalAdvertisedHost,
    string? DefaultGameHost,
    int DefaultGamePort,
    string OutputPath,
    string PostgresConnectionString,
    short? MonsterMapId,
    bool DisableDatabaseLogging)
{
    private const string DefaultPostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=godswar;Username=godswar;Password=godswar_dev_password;Pooling=true";

    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[i][2..];
            if (string.Equals(key, "disable-db", StringComparison.OrdinalIgnoreCase))
            {
                values[key] = "true";
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for --{key}");
            }

            values[key] = args[++i];
        }

        if (!values.TryGetValue("login-host", out var loginHost) || string.IsNullOrWhiteSpace(loginHost))
        {
            throw new ArgumentException("Required: --login-host <host-or-ip>");
        }

        return new Options(
            LoginHost: loginHost,
            LoginPort: GetInt(values, "login-port", 5999),
            LocalLoginPort: GetInt(values, "local-login-port", 5999),
            LocalGamePort: GetInt(values, "local-game-port", 7000),
            LocalAdvertisedHost: GetString(values, "local-advertised-host", "127.1.1.110"),
            DefaultGameHost: values.GetValueOrDefault("default-game-host"),
            DefaultGamePort: GetInt(values, "default-game-port", 7000),
            OutputPath: GetString(values, "out", Path.Combine("captures", $"godswar-proxy-{DateTime.Now:yyyyMMdd-HHmmss}.log")),
            PostgresConnectionString: GetString(
                values,
                "postgres-connection-string",
                Environment.GetEnvironmentVariable("GODSWAR_CAPTURE_POSTGRES_CONNECTION_STRING")
                    ?? Environment.GetEnvironmentVariable("GODSWAR_POSTGRES_CONNECTION_STRING")
                    ?? DefaultPostgresConnectionString),
            MonsterMapId: GetOptionalShort(values, "monster-map-id"),
            DisableDatabaseLogging: GetBool(values, "disable-db", false));
    }

    private static int GetInt(Dictionary<string, string> values, string key, int fallback)
    {
        return values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : fallback;
    }

    private static string GetString(Dictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
    {
        return values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : fallback;
    }

    private static short? GetOptionalShort(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return null;
        }

        if (short.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"Invalid value for --{key}: {value}");
    }
}
