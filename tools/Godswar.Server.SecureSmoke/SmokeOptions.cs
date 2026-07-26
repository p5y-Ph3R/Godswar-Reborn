using System.Net;
using Npgsql;

namespace Godswar.Server.SecureSmoke;

internal sealed record SmokeOptions(
    IPAddress ServerAddress,
    int LoginPort,
    int GamePort,
    int UdpPort,
    string RootCertificatePath,
    string PostgresConnectionString,
    TimeSpan OperationTimeout)
{
    private const string RootVariable =
        "GODSWAR_SECURE_SMOKE_ROOT_CERTIFICATE_PATH";
    private const string PostgresVariable =
        "GODSWAR_SECURE_SMOKE_POSTGRES_CONNECTION_STRING";

    public static SmokeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 0)
        {
            throw new ArgumentException(
                "This probe accepts configuration only through its documented " +
                "environment variables.");
        }

        var addressText = ReadOptional(
            "GODSWAR_SECURE_SMOKE_ADDRESS",
            "127.0.0.1");
        if (!IPAddress.TryParse(addressText, out var address) ||
            !IPAddress.IsLoopback(address))
        {
            throw new InvalidDataException(
                "The secure smoke address must be a literal loopback address.");
        }

        var rootPath = Require(RootVariable);
        rootPath = Path.GetFullPath(rootPath);
        if (!File.Exists(rootPath))
        {
            throw new FileNotFoundException(
                "The public development root certificate was not found.",
                rootPath);
        }

        var connectionString = ValidatePostgres(
            Require(PostgresVariable));
        return new SmokeOptions(
            address,
            ReadPort("GODSWAR_SECURE_SMOKE_LOGIN_PORT", 6599),
            ReadPort("GODSWAR_SECURE_SMOKE_GAME_PORT", 7443),
            ReadPort("GODSWAR_SECURE_SMOKE_UDP_PORT", 7444),
            rootPath,
            connectionString,
            TimeSpan.FromSeconds(
                ReadBoundedInt(
                    "GODSWAR_SECURE_SMOKE_TIMEOUT_SECONDS",
                    defaultValue: 20,
                    minimum: 5,
                    maximum: 60)));
    }

    private static string ValidatePostgres(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(
            connectionString);
        if (!IPAddress.TryParse(builder.Host, out var host) ||
            !IPAddress.IsLoopback(host))
        {
            throw new InvalidDataException(
                "The smoke PostgreSQL host must be a literal loopback address.");
        }
        if (!string.Equals(
                builder.Database,
                "godswar_secure_dev",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The smoke probe may modify only the godswar_secure_dev database.");
        }
        if (string.IsNullOrWhiteSpace(builder.Username) ||
            string.IsNullOrEmpty(builder.Password))
        {
            throw new InvalidDataException(
                "The smoke PostgreSQL credentials are incomplete.");
        }

        builder.Timeout = Math.Min(
            builder.Timeout <= 0 ? 5 : builder.Timeout,
            5);
        builder.CommandTimeout = Math.Min(
            builder.CommandTimeout <= 0 ? 5 : builder.CommandTimeout,
            5);
        builder.Pooling = true;
        builder.MinPoolSize = 0;
        builder.MaxPoolSize = Math.Min(
            Math.Max(1, builder.MaxPoolSize),
            4);
        return builder.ConnectionString;
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Required environment variable {name} is missing.");
        }

        return value.Trim();
    }

    private static string ReadOptional(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : value.Trim();
    }

    private static int ReadPort(string name, int defaultValue) =>
        ReadBoundedInt(name, defaultValue, 1, ushort.MaxValue);

    private static int ReadBoundedInt(
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }
        if (!int.TryParse(text, out var value) ||
            value < minimum ||
            value > maximum)
        {
            throw new InvalidDataException(
                $"{name} must be an integer from {minimum} through {maximum}.");
        }

        return value;
    }
}
