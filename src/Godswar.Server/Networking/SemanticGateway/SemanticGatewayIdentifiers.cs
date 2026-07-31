using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.SemanticGateway;

/// <summary>
/// Stable identity assigned by the gateway to one accepted client connection.
/// It is independent of the remote address because NAT peers can share an IP.
/// </summary>
internal readonly record struct GatewayConnectionId
{
    public GatewayConnectionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Gateway connection IDs cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsValid => Value != Guid.Empty;

    public static GatewayConnectionId New() =>
        new(SemanticGatewayIdFactory.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Identifies one login generation. Starting another generation for the same
/// authenticated account invalidates this generation and all its admissions.
/// </summary>
internal readonly record struct GatewayLoginGenerationId
{
    public GatewayLoginGenerationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Gateway login-generation IDs cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsValid => Value != Guid.Empty;

    public static GatewayLoginGenerationId New() =>
        new(SemanticGatewayIdFactory.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Opaque identity of one bounded gateway-to-worker admission.
/// </summary>
internal readonly record struct GatewayAdmissionId
{
    public GatewayAdmissionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Gateway admission IDs cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsValid => Value != Guid.Empty;

    public static GatewayAdmissionId New() =>
        new(SemanticGatewayIdFactory.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Authenticated account identity supplied by the secure session boundary.
/// The username is already canonical; this type validates rather than
/// silently changing it.
/// </summary>
internal readonly record struct SemanticGatewayPrincipal
{
    public const int MaximumUsernameLength = 32;

    public SemanticGatewayPrincipal(
        int accountId,
        string canonicalUsername)
    {
        if (accountId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(accountId));
        }

        ArgumentNullException.ThrowIfNull(canonicalUsername);
        if (canonicalUsername.Length is < 1 or > MaximumUsernameLength ||
            canonicalUsername.Any(static value => value is < '!' or > '~'))
        {
            throw new ArgumentException(
                $"Canonical username must contain 1.." +
                $"{MaximumUsernameLength} printable ASCII characters.",
                nameof(canonicalUsername));
        }

        AccountId = accountId;
        CanonicalUsername = canonicalUsername;
    }

    public int AccountId { get; }

    public string? CanonicalUsername { get; }

    public bool IsValid =>
        AccountId > 0 &&
        !string.IsNullOrEmpty(CanonicalUsername);

    public override string ToString() => nameof(SemanticGatewayPrincipal);
}

/// <summary>
/// A source binding always combines a gateway connection ID with a normalized
/// IP address. The address is defense-in-depth context and is never accepted
/// as the connection or player identity by itself.
/// </summary>
internal readonly record struct SemanticGatewayConnectionSource
{
    public SemanticGatewayConnectionSource(
        GatewayConnectionId connectionId,
        IPAddress address)
    {
        if (!connectionId.IsValid)
        {
            throw new ArgumentException(
                "A valid gateway connection ID is required.",
                nameof(connectionId));
        }

        ArgumentNullException.ThrowIfNull(address);
        var normalized = Normalize(address);
        if (normalized.Equals(IPAddress.Any) ||
            normalized.Equals(IPAddress.None) ||
            normalized.Equals(IPAddress.IPv6Any) ||
            normalized.Equals(IPAddress.IPv6None))
        {
            throw new ArgumentException(
                "A concrete client source IP address is required.",
                nameof(address));
        }

        ConnectionId = connectionId;
        Address = normalized;
    }

    public GatewayConnectionId ConnectionId { get; }

    public IPAddress? Address { get; }

    public bool IsValid =>
        ConnectionId.IsValid &&
        Address is not null;

    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return new IPAddress(address.MapToIPv4().GetAddressBytes());
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 =>
                new IPAddress(address.GetAddressBytes()),
            _ => throw new ArgumentException(
                "Only IPv4 and IPv6 source addresses are supported.",
                nameof(address))
        };
    }

    public override string ToString() =>
        $"{nameof(SemanticGatewayConnectionSource)}:{ConnectionId}";
}

internal static class SemanticGatewayIdFactory
{
    public static Guid NewGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                RandomNumberGenerator.Fill(bytes);
                var anyNonzero = false;
                foreach (var value in bytes)
                {
                    anyNonzero |= value != 0;
                }
                if (anyNonzero)
                {
                    return new Guid(bytes);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        throw new CryptographicException(
            "The random source returned repeated invalid identifiers.");
    }
}
