using System.Security.Cryptography;

namespace Godswar.Server.Operations;

internal sealed class ServerOperationsOptions
{
    public ManagementOptions Management { get; set; } = new();

    public ServerReadinessMonitorOptions Readiness { get; set; } = new();

    public string DrainTokenFile { get; set; } = string.Empty;

    public void ApplyEnvironment()
    {
        Management ??= new ManagementOptions();
        Readiness ??= new ServerReadinessMonitorOptions();
        Management.Enabled = ReadBool(
            "GODSWAR_MANAGEMENT_ENABLED",
            Management.Enabled);
        Management.BindHost =
            Environment.GetEnvironmentVariable(
                "GODSWAR_MANAGEMENT_BIND_HOST") ??
            Management.BindHost;
        Management.Port = ReadInt(
            "GODSWAR_MANAGEMENT_PORT",
            Management.Port);
        DrainTokenFile =
            Environment.GetEnvironmentVariable(
                "GODSWAR_MANAGEMENT_DRAIN_TOKEN_FILE") ??
            DrainTokenFile;
    }

    public void Validate(params int[] reservedTcpPorts)
    {
        Management ??= new ManagementOptions();
        Readiness ??= new ServerReadinessMonitorOptions();
        Management.ValidateResourceBounds();
        if (Management.Enabled)
        {
            Management.Validate(reservedTcpPorts);
        }
        Readiness.Validate();

        if (Management.Enabled &&
            !string.IsNullOrEmpty(DrainTokenFile) &&
            !Path.IsPathFullyQualified(DrainTokenFile))
        {
            throw new InvalidDataException(
                "Operations.DrainTokenFile must be an absolute secret-file path.");
        }
    }

    private static int ReadInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return fallback;
        }
        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"{name} must be a valid integer.");
    }

    private static bool ReadBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return fallback;
        }
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"{name} must be 'true' or 'false'.");
    }
}

internal static class ManagementDrainTokenFile
{
    private const int MaximumFileBytes = 258;

    public static ManagementTokenAuthenticator? TryLoad(
        string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException(
                "The management drain token path must be absolute.");
        }

        var bytes = new byte[MaximumFileBytes + 1];
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                MaximumFileBytes + 1,
                FileOptions.SequentialScan);
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0)
                {
                    break;
                }
                count += read;
            }
            if (count > MaximumFileBytes)
            {
                throw new InvalidDataException(
                    "The management drain token file is too large.");
            }

            var length = count;
            while (length > 0 &&
                bytes[length - 1] is (byte)'\r' or (byte)'\n')
            {
                length--;
            }

            return new ManagementTokenAuthenticator(
                bytes.AsSpan(0, length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
