using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.Networking.SemanticGateway;

internal sealed partial class SemanticGatewayRuntimeOptions
{
    private const long MaximumAggregateBufferBytes =
        512L * 1_024 * 1_024;

    private static IPEndPoint ValidateLoginEndpoint(
        SemanticGatewayLoginEndpointOptions? options)
    {
        if (options is null)
        {
            throw new InvalidDataException(
                "SemanticGateway.Login is required.");
        }

        return ParseBindEndpoint(
            options.BindHost,
            options.BindPort,
            "SemanticGateway.Login");
    }

    private static ValidatedGameEndpoint ValidateGameEndpoint(
        SemanticGatewayGameEndpointOptions? options)
    {
        if (options is null)
        {
            throw new InvalidDataException(
                "SemanticGateway.Game is required.");
        }

        var bind = ParseBindEndpoint(
            options.BindHost,
            options.BindPort,
            "SemanticGateway.Game");
        var publicHost = ValidateLoopbackPublicHost(
            options.PublicHost,
            "SemanticGateway.Game.PublicHost");
        RequirePort(
            options.PublicPort,
            "SemanticGateway.Game.PublicPort");
        return new ValidatedGameEndpoint(
            bind,
            publicHost,
            options.PublicPort);
    }

    private static SemanticGatewayClientRuntimeLimits ValidateClientLimits(
        SemanticGatewayLimitOptions options)
    {
        RequireRange(
            options.ListenBacklog,
            1,
            4_096,
            "SemanticGateway.Limits.ListenBacklog");
        RequireRange(
            options.MaximumConnections,
            1,
            4_096,
            "SemanticGateway.Limits.MaximumConnections");
        RequireRange(
            options.BufferSizeBytes,
            1_024,
            64 * 1_024,
            "SemanticGateway.Limits.BufferSizeBytes");
        RequireRange(
            options.FirstPacketTimeoutMilliseconds,
            50,
            2 * 60 * 1_000,
            "SemanticGateway.Limits.FirstPacketTimeoutMilliseconds");
        RequireRange(
            options.IdleTimeoutMilliseconds,
            1_000,
            10 * 60 * 1_000,
            "SemanticGateway.Limits.IdleTimeoutMilliseconds");
        RequireRange(
            options.DrainTimeoutMilliseconds,
            100,
            60_000,
            "SemanticGateway.Limits.DrainTimeoutMilliseconds");

        var aggregateBuffers = checked(
            (long)options.MaximumConnections *
            2 *
            options.BufferSizeBytes);
        if (aggregateBuffers > MaximumAggregateBufferBytes)
        {
            throw new InvalidDataException(
                "Semantic-gateway connection capacity and buffer size " +
                "reserve more than 512 MiB.");
        }

        try
        {
            return new SemanticGatewayClientRuntimeLimits(
                options.ListenBacklog,
                options.MaximumConnections,
                options.MaximumUnauthenticatedConnections,
                options.MaximumUnauthenticatedConnectionsPerIp,
                options.MaximumUnauthenticatedConnectionsPerPrefix,
                options.BufferSizeBytes,
                TimeSpan.FromMilliseconds(
                    options.FirstPacketTimeoutMilliseconds),
                TimeSpan.FromMilliseconds(
                    options.IdleTimeoutMilliseconds),
                TimeSpan.FromMilliseconds(
                    options.DrainTimeoutMilliseconds));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Semantic-gateway client limits are invalid.",
                exception);
        }
    }

    private static BackhaulRuntimeLimits ValidateBackhaulLimits(
        SemanticGatewayLimitOptions options)
    {
        RequireRange(
            options.MaximumConcurrentBackhaulTlsHandshakes,
            1,
            BackhaulHandshakeGate.MaximumConcurrency,
            "SemanticGateway.Limits." +
            "MaximumConcurrentBackhaulTlsHandshakes");
        try
        {
            return new BackhaulRuntimeLimits(
                TimeSpan.FromMilliseconds(
                    options.BackhaulConnectTimeoutMilliseconds),
                TimeSpan.FromMilliseconds(
                    options.BackhaulTlsHandshakeTimeoutMilliseconds),
                TimeSpan.FromMilliseconds(
                    options.BackhaulOpenSessionTimeoutMilliseconds),
                TimeSpan.FromMilliseconds(
                    options.BackhaulWriteTimeoutMilliseconds))
                .Validate();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Semantic-gateway backhaul deadlines are invalid.",
                exception);
        }
    }

    private static SemanticGatewayAuthorityLimits ValidateAuthorityLimits(
        SemanticGatewayAuthorityLimitOptions options)
    {
        try
        {
            return new SemanticGatewayAuthorityLimits(
                options.MaximumLoginGenerations,
                options.MaximumAdmissions,
                options.MaximumAdmissionsPerGeneration,
                options.MaximumExpiryWorkPerOperation,
                TimeSpan.FromSeconds(
                    options.LoginGenerationTtlSeconds),
                TimeSpan.FromSeconds(
                    options.ReservationTtlSeconds),
                TimeSpan.FromSeconds(
                    options.CommittedAdmissionTtlSeconds));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Semantic-gateway authority limits are invalid.",
                exception);
        }
    }

    private static IPEndPoint ParseBindEndpoint(
        string? host,
        int port,
        string property)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host.Length > 64 ||
            !IPAddress.TryParse(host, out var parsed))
        {
            throw new InvalidDataException(
                $"{property}.BindHost must be an exact IP address.");
        }
        RequirePort(port, $"{property}.BindPort");
        var address = NormalizeAddress(parsed);
        if (!IPAddress.IsLoopback(address))
        {
            throw new InvalidDataException(
                $"{property}.BindHost must be an exact loopback IP for the " +
                "local raw-client milestone.");
        }

        return new IPEndPoint(address, port);
    }

    private static IPEndPoint ParsePrivateEndpoint(
        string? host,
        int port,
        string property)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host.Length > 64 ||
            !IPAddress.TryParse(host, out var parsed))
        {
            throw new InvalidDataException(
                $"{property}.BackhaulHost must be an exact private IP.");
        }
        RequirePort(port, $"{property}.BackhaulPort");
        var address = NormalizeAddress(parsed);
        if (!IsPrivateOrLoopback(address))
        {
            throw new InvalidDataException(
                $"{property}.BackhaulHost must be loopback, RFC1918, or " +
                "IPv6 unique-local.");
        }

        return new IPEndPoint(address, port);
    }

    private static string ValidateHost(
        string? configured,
        string property)
    {
        if (string.IsNullOrWhiteSpace(configured) ||
            configured.Length > 253 ||
            !string.Equals(
                configured,
                configured.Trim(),
                StringComparison.Ordinal) ||
            configured.Any(static value =>
                value is <= ' ' or > '~') ||
            Uri.CheckHostName(configured) == UriHostNameType.Unknown)
        {
            throw new InvalidDataException(
                $"{property} must be a bounded ASCII DNS name or IP.");
        }

        if (IPAddress.TryParse(configured, out var parsed) &&
            IsInvalidPublicAddress(NormalizeAddress(parsed)))
        {
            throw new InvalidDataException(
                $"{property} must identify a concrete host.");
        }

        return configured;
    }

    private static string ValidateLoopbackPublicHost(
        string? configured,
        string property)
    {
        if (string.IsNullOrWhiteSpace(configured) ||
            configured.Length > 64 ||
            !IPAddress.TryParse(configured, out var parsed) ||
            !IPAddress.IsLoopback(NormalizeAddress(parsed)))
        {
            throw new InvalidDataException(
                $"{property} must be an exact launcher-visible loopback IP " +
                "for the local raw-client milestone.");
        }

        return NormalizeAddress(parsed).ToString();
    }

    private static BackhaulCertificatePins BuildWorkerPins(
        string[]? configured,
        int workerIndex)
    {
        if (configured is null ||
            configured.Length is < 1 or >
                BackhaulCertificatePins.MaximumPins)
        {
            throw new InvalidDataException(
                $"SemanticGateway.Workers[{workerIndex}] must configure " +
                $"between 1 and {BackhaulCertificatePins.MaximumPins} " +
                "worker certificate pins.");
        }
        var unique = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pin in configured)
        {
            if (string.IsNullOrWhiteSpace(pin) ||
                pin.Length != 64 ||
                !unique.Add(pin))
            {
                throw new InvalidDataException(
                    $"SemanticGateway.Workers[{workerIndex}] contains an " +
                    "invalid or duplicate worker certificate pin.");
            }
        }

        try
        {
            return new BackhaulCertificatePins(configured);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"SemanticGateway.Workers[{workerIndex}] contains an " +
                "invalid worker certificate pin set.",
                exception);
        }
    }

    private static X509Certificate2 LoadGatewayCertificate(
        string configurationPath,
        SemanticGatewayCertificateOptions options,
        TimeProvider timeProvider)
    {
        var certificatePath = ResolveCertificatePath(
            configurationPath,
            options.Path);
        ValidateEnvironmentVariableName(
            options.PasswordEnvironmentVariable);
        var password = Environment.GetEnvironmentVariable(
            options.PasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidDataException(
                "The semantic-gateway certificate password environment " +
                "variable is unavailable.");
        }

        X509Certificate2? certificate = null;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                password,
                OperatingSystem.IsWindows()
                    ? X509KeyStorageFlags.DefaultKeySet
                    : X509KeyStorageFlags.EphemeralKeySet);
            BackhaulTlsPolicy.ValidateLocalCertificate(
                certificate,
                BackhaulCertificatePurpose.GatewayClient,
                timeProvider);
            return certificate;
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                CryptographicException or
                ArgumentException)
        {
            certificate?.Dispose();
            throw new InvalidDataException(
                "The semantic-gateway client certificate could not be " +
                "loaded or validated.");
        }
    }

    private static string ResolveCertificatePath(
        string configurationPath,
        string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) ||
            configuredPath.Length > 1_024 ||
            configuredPath.Contains('\0'))
        {
            throw new InvalidDataException(
                "SemanticGateway.GatewayCertificate.Path is invalid.");
        }

        string resolved;
        try
        {
            var root = Path.GetDirectoryName(configurationPath) ??
                Environment.CurrentDirectory;
            resolved = Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(Path.Combine(root, configuredPath));
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            throw new InvalidDataException(
                "SemanticGateway.GatewayCertificate.Path is invalid.");
        }

        if (!File.Exists(resolved))
        {
            throw new InvalidDataException(
                "The semantic-gateway client certificate file is missing.");
        }

        return resolved;
    }

    private static void ValidateEnvironmentVariableName(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) ||
            configured.Length > 128 ||
            !IsEnvironmentNameStart(configured[0]) ||
            configured.Skip(1).Any(static value =>
                !IsEnvironmentNamePart(value)))
        {
            throw new InvalidDataException(
                "The semantic-gateway certificate password environment " +
                "variable name is invalid.");
        }
    }

    private static bool IsEnvironmentNameStart(char value) =>
        value is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            '_';

    private static bool IsEnvironmentNamePart(char value) =>
        IsEnvironmentNameStart(value) ||
        value is >= '0' and <= '9';

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xFE) == 0xFC;
    }

    private static bool IsInvalidListenerAddress(IPAddress address) =>
        address.Equals(IPAddress.None) ||
        address.Equals(IPAddress.IPv6None) ||
        IsMulticast(address);

    private static bool IsInvalidPublicAddress(IPAddress address) =>
        address.Equals(IPAddress.Any) ||
        address.Equals(IPAddress.IPv6Any) ||
        IsInvalidListenerAddress(address);

    private static bool IsMulticast(IPAddress address)
    {
        if (address.IsIPv6Multicast)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] is >= 224 and <= 239;
    }

    private static void RequirePort(int value, string property)
    {
        if (value is < 1 or > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"{property} must be between 1 and 65535.");
        }
    }

    private static void RequireRange(
        int value,
        int minimum,
        int maximum,
        string property)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"{property} must be between {minimum} and {maximum}.");
        }
    }

    private sealed record ValidatedGameEndpoint(
        IPEndPoint Bind,
        string PublicHost,
        int PublicPort);
}
