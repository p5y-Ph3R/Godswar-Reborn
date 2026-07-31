using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Godswar.Server.Networking.RelayGateway;

/// <summary>
/// Explicit JSON configuration for the opt-in legacy TCP relay. Upstream
/// names are resolved once during validation and every result must be a
/// loopback or private address. Runtime connections never repeat DNS lookup.
/// </summary>
internal sealed class RelayGatewayOptions
{
    internal const int MaximumConfigurationBytes = 64 * 1024;
    private const long MaximumApplicationBufferBytes =
        512L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public RelayGatewayEndpointOptions? Login { get; set; }

    public RelayGatewayEndpointOptions? Game { get; set; }

    public RelayGatewayRuntimeLimitOptions Limits { get; set; } = new();

    public static async Task<RelayGatewayConfiguration> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(
                "A relay-gateway configuration path is required.");
        }

        var document = new byte[MaximumConfigurationBytes + 1];
        var documentLength = 0;
        try
        {
            await using var stream = new FileStream(
                Path.GetFullPath(path),
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    BufferSize = 4_096,
                    Options =
                        FileOptions.Asynchronous |
                        FileOptions.SequentialScan
                });
            while (documentLength < document.Length)
            {
                var count = await stream.ReadAsync(
                    document.AsMemory(documentLength),
                    cancellationToken);
                if (count == 0)
                {
                    break;
                }

                documentLength += count;
            }
        }
        catch (Exception ex)
            when (ex is ArgumentException or
                NotSupportedException or
                IOException or
                UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "The relay-gateway configuration could not be read.",
                ex);
        }

        if (documentLength is 0 or > MaximumConfigurationBytes)
        {
            Array.Clear(document);
            throw new InvalidDataException(
                $"The relay-gateway configuration must be between 1 and " +
                $"{MaximumConfigurationBytes} bytes.");
        }

        try
        {
            var options = JsonSerializer.Deserialize<RelayGatewayOptions>(
                document.AsSpan(0, documentLength),
                JsonOptions);
            if (options is null)
            {
                throw new InvalidDataException(
                    "The relay-gateway configuration is empty.");
            }

            return await options.ValidateAsync(cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The relay-gateway configuration is not valid JSON.",
                ex);
        }
        finally
        {
            Array.Clear(document);
        }
    }

    internal async Task<RelayGatewayConfiguration> ValidateAsync(
        CancellationToken cancellationToken = default)
    {
        var limits = (Limits ??
            throw new InvalidDataException(
                "RelayGateway.Limits is required.")).Validate();
        var login = await ValidateEndpointAsync(
            Login,
            RelayGatewayEndpointRole.Login,
            cancellationToken);
        var game = await ValidateEndpointAsync(
            Game,
            RelayGatewayEndpointRole.Game,
            cancellationToken);

        ValidateEndpointCollisions(login, game);
        var aggregateBuffers = checked(
            (long)limits.MaximumConnections *
            2 *
            limits.BufferSizeBytes);
        if (aggregateBuffers > MaximumApplicationBufferBytes)
        {
            throw new InvalidDataException(
                "RelayGateway connection capacity and BufferSizeBytes " +
                "reserve more than 512 MiB of application relay buffers.");
        }

        return new RelayGatewayConfiguration(login, game, limits);
    }

    private static async Task<RelayGatewayEndpointConfiguration>
        ValidateEndpointAsync(
            RelayGatewayEndpointOptions? options,
            RelayGatewayEndpointRole role,
            CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new InvalidDataException(
                $"RelayGateway.{role}. is required.");
        }

        var prefix = $"RelayGateway.{role}.";
        var bindAddress = ParseBindAddress(
            options.BindHost,
            prefix + nameof(options.BindHost));
        RequirePort(options.BindPort, prefix + nameof(options.BindPort));
        RequirePort(
            options.UpstreamPort,
            prefix + nameof(options.UpstreamPort));
        var upstreamAddress = await ResolvePrivateAddressAsync(
            options.UpstreamHost,
            prefix + nameof(options.UpstreamHost),
            cancellationToken);

        return new RelayGatewayEndpointConfiguration(
            role,
            new IPEndPoint(bindAddress, options.BindPort),
            new IPEndPoint(upstreamAddress, options.UpstreamPort));
    }

    private static IPAddress ParseBindAddress(
        string? host,
        string property)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host.Length > 64 ||
            !IPAddress.TryParse(host, out var address))
        {
            throw new InvalidDataException(
                $"{property} must be an exact IP address.");
        }

        return NormalizeAddress(address);
    }

    private static async Task<IPAddress> ResolvePrivateAddressAsync(
        string? host,
        string property,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
        {
            throw new InvalidDataException(
                $"{property} must be a non-empty private host.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [NormalizeAddress(literal)];
        }
        else
        {
            if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                throw new InvalidDataException(
                    $"{property} is not a valid DNS host name.");
            }

            try
            {
                addresses = await Dns.GetHostAddressesAsync(
                    host,
                    cancellationToken);
            }
            catch (Exception ex)
                when (ex is SocketException or ArgumentException)
            {
                throw new InvalidDataException(
                    $"{property} could not be resolved.",
                    ex);
            }
        }

        var resolved = addresses
            .Select(NormalizeAddress)
            .Distinct()
            .OrderBy(static address =>
                address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(static address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (resolved.Length is 0 or > 16)
        {
            throw new InvalidDataException(
                $"{property} must resolve to between 1 and 16 addresses.");
        }
        if (resolved.Any(static address => !IsPrivateOrLoopback(address)))
        {
            throw new InvalidDataException(
                $"{property} must resolve only to loopback, RFC1918, or " +
                "IPv6 unique-local addresses.");
        }

        return resolved[0];
    }

    private static void ValidateEndpointCollisions(
        RelayGatewayEndpointConfiguration login,
        RelayGatewayEndpointConfiguration game)
    {
        if (login.Bind.Port == game.Bind.Port)
        {
            throw new InvalidDataException(
                "RelayGateway login and game bind ports must be distinct.");
        }
        if (SameEndpoint(login.Upstream, game.Upstream))
        {
            throw new InvalidDataException(
                "RelayGateway login and game upstream endpoints must be distinct.");
        }

        var binds = new[] { login.Bind, game.Bind };
        var upstreams = new[] { login.Upstream, game.Upstream };
        foreach (var bind in binds)
        {
            foreach (var upstream in upstreams)
            {
                if (IsLocalRelayLoop(bind, upstream))
                {
                    throw new InvalidDataException(
                        "A relay upstream must not resolve back to a local " +
                        "relay listener.");
                }
            }
        }
    }

    private static bool IsLocalRelayLoop(
        IPEndPoint bind,
        IPEndPoint upstream)
    {
        if (bind.Port != upstream.Port)
        {
            return false;
        }

        return SameAddress(bind.Address, upstream.Address) ||
            (IPAddress.IsLoopback(upstream.Address) &&
                (IPAddress.IsLoopback(bind.Address) ||
                    bind.Address.Equals(IPAddress.Any) ||
                    bind.Address.Equals(IPAddress.IPv6Any)));
    }

    private static bool SameEndpoint(IPEndPoint left, IPEndPoint right) =>
        left.Port == right.Port &&
        SameAddress(left.Address, right.Address);

    private static bool SameAddress(IPAddress left, IPAddress right) =>
        NormalizeAddress(left).Equals(NormalizeAddress(right));

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    internal static bool IsPrivateOrLoopback(IPAddress candidate)
    {
        var address = NormalizeAddress(candidate);
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

    private static void RequirePort(int value, string property)
    {
        if (value is < 1 or > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"{property} must be between 1 and 65535.");
        }
    }
}

internal sealed class RelayGatewayEndpointOptions
{
    public string BindHost { get; set; } = string.Empty;

    public int BindPort { get; set; }

    public string UpstreamHost { get; set; } = string.Empty;

    public int UpstreamPort { get; set; }
}

internal sealed class RelayGatewayRuntimeLimitOptions
{
    public int ListenBacklog { get; set; } = 512;

    public int MaximumConnections { get; set; } = 512;

    public int BufferSizeBytes { get; set; } = 16 * 1024;

    public int ConnectTimeoutMilliseconds { get; set; } = 2_000;

    public int IdleTimeoutMilliseconds { get; set; } = 90_000;

    public int WriteTimeoutMilliseconds { get; set; } = 5_000;

    public int DrainTimeoutMilliseconds { get; set; } = 5_000;

    internal RelayGatewayRuntimeLimits Validate()
    {
        RequireRange(ListenBacklog, 1, 4_096, nameof(ListenBacklog));
        RequireRange(
            MaximumConnections,
            1,
            4_096,
            nameof(MaximumConnections));
        RequireRange(
            BufferSizeBytes,
            1_024,
            64 * 1024,
            nameof(BufferSizeBytes));
        RequireRange(
            ConnectTimeoutMilliseconds,
            50,
            30_000,
            nameof(ConnectTimeoutMilliseconds));
        RequireRange(
            IdleTimeoutMilliseconds,
            1_000,
            10 * 60 * 1_000,
            nameof(IdleTimeoutMilliseconds));
        RequireRange(
            WriteTimeoutMilliseconds,
            50,
            30_000,
            nameof(WriteTimeoutMilliseconds));
        RequireRange(
            DrainTimeoutMilliseconds,
            100,
            60_000,
            nameof(DrainTimeoutMilliseconds));

        return new RelayGatewayRuntimeLimits(
            ListenBacklog,
            MaximumConnections,
            BufferSizeBytes,
            TimeSpan.FromMilliseconds(ConnectTimeoutMilliseconds),
            TimeSpan.FromMilliseconds(IdleTimeoutMilliseconds),
            TimeSpan.FromMilliseconds(WriteTimeoutMilliseconds),
            TimeSpan.FromMilliseconds(DrainTimeoutMilliseconds));
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
                $"RelayGateway.Limits.{property} must be between " +
                $"{minimum} and {maximum}.");
        }
    }
}
