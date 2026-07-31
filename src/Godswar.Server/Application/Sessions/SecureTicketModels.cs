using System.Security.Cryptography;

namespace Godswar.Server.Application.Sessions;

internal enum SecureEndpointRole : byte
{
    Login = 1,
    Game = 2
}

[Flags]
internal enum SecureGamePermissions : uint
{
    None = 0,
    EnterWorld = 1
}

internal enum SecureLoginGenerationStatus : byte
{
    Started = 1,
    CapacityExceeded = 2
}

internal enum SecureTicketIssueStatus : byte
{
    Issued = 1,
    GenerationRejected = 2,
    CapacityExceeded = 3
}

internal enum SecureTicketConsumeStatus : byte
{
    Accepted = 1,
    Rejected = 2,
    Expired = 3,
    ScopeRejected = 4,
    NotReady = 5
}

internal sealed class SecureConnectionContext
{
    private readonly byte[] _connectionId;
    private readonly byte[] _clientInstanceId;
    private readonly byte[] _originSha256;

    public SecureConnectionContext(
        SecureEndpointRole role,
        ushort protocolMajor,
        ushort protocolMinor,
        ReadOnlySpan<byte> connectionId,
        ReadOnlySpan<byte> clientInstanceId,
        ReadOnlySpan<byte> originSha256)
    {
        if (!SecureTicketModelValidation.IsEndpointRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
        if (protocolMajor == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(protocolMajor));
        }
        if (connectionId.Length !=
                SecureTicketModelValidation.ConnectionIdBytes ||
            SecureTicketModelValidation.IsAllZero(connectionId))
        {
            throw new ArgumentException(
                "TLS connection ID must be exactly 16 nonzero bytes.",
                nameof(connectionId));
        }
        if (clientInstanceId.Length !=
                SecureTicketModelValidation.ClientInstanceIdBytes ||
            SecureTicketModelValidation.IsAllZero(clientInstanceId))
        {
            throw new ArgumentException(
                "Client-instance ID must be exactly 16 nonzero bytes.",
                nameof(clientInstanceId));
        }
        if (originSha256.Length !=
                SecureTicketModelValidation.BuildHashBytes ||
            SecureTicketModelValidation.IsAllZero(originSha256))
        {
            throw new ArgumentException(
                "Origin SHA-256 must be exactly 32 nonzero bytes.",
                nameof(originSha256));
        }

        Role = role;
        ProtocolMajor = protocolMajor;
        ProtocolMinor = protocolMinor;
        _connectionId = connectionId.ToArray();
        _clientInstanceId = clientInstanceId.ToArray();
        _originSha256 = originSha256.ToArray();
    }

    public SecureEndpointRole Role { get; }

    public ushort ProtocolMajor { get; }

    public ushort ProtocolMinor { get; }

    public ReadOnlyMemory<byte> ConnectionId => _connectionId;

    public ReadOnlyMemory<byte> ClientInstanceId => _clientInstanceId;

    public ReadOnlyMemory<byte> OriginSha256 => _originSha256;
}

internal sealed record SecureGameTarget
{
    public SecureGameTarget(
        string routeHost,
        string tlsHost,
        string audience,
        ushort routePort,
        ushort tlsPort,
        uint serverId,
        SecureGamePermissions permissions =
            SecureGamePermissions.EnterWorld)
    {
        if (!SecureTicketModelValidation.IsDnsName(routeHost, 23))
        {
            throw new ArgumentException(
                "Route host must be a strict ASCII DNS name of at most 23 bytes.",
                nameof(routeHost));
        }
        if (!SecureTicketModelValidation.IsDnsName(tlsHost, 253))
        {
            throw new ArgumentException(
                "TLS host must be a strict ASCII DNS name.",
                nameof(tlsHost));
        }
        if (!SecureTicketModelValidation.IsAudience(audience))
        {
            throw new ArgumentException(
                "Audience must be a bounded ASCII token.",
                nameof(audience));
        }
        if (routePort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(routePort));
        }
        if (tlsPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tlsPort));
        }
        if (serverId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(serverId));
        }
        if (permissions != SecureGamePermissions.EnterWorld)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permissions),
                "Slice 7 permits only the EnterWorld permission.");
        }

        RouteHost = routeHost;
        TlsHost = tlsHost;
        Audience = audience;
        RoutePort = routePort;
        TlsPort = tlsPort;
        ServerId = serverId;
        Permissions = permissions;
    }

    public string RouteHost { get; }

    public string TlsHost { get; }

    public string Audience { get; }

    public ushort RoutePort { get; }

    public ushort TlsPort { get; }

    public uint ServerId { get; }

    public SecureGamePermissions Permissions { get; }
}

internal sealed class SecureLoginGeneration
{
    internal SecureLoginGeneration(
        Guid authorityId,
        Guid generationId,
        int accountId,
        string username)
    {
        AuthorityId = authorityId;
        GenerationId = generationId;
        AccountId = accountId;
        Username = username;
    }

    internal Guid AuthorityId { get; }

    internal Guid GenerationId { get; }

    public int AccountId { get; }

    public string Username { get; }
}

internal sealed class SecureBoundGamePrincipal
{
    internal SecureBoundGamePrincipal(
        int accountId,
        string username,
        SecureGamePermissions permissions,
        Guid loginGenerationId)
    {
        SecureTicketModelValidation.ValidateAccount(accountId, username);
        if (permissions != SecureGamePermissions.EnterWorld)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions));
        }
        if (loginGenerationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Login-generation ID must be nonzero.",
                nameof(loginGenerationId));
        }

        AccountId = accountId;
        Username = username;
        Permissions = permissions;
        LoginGenerationId = loginGenerationId;
    }

    public int AccountId { get; }

    public string Username { get; }

    public SecureGamePermissions Permissions { get; }

    internal Guid LoginGenerationId { get; }

    public override string ToString() =>
        nameof(SecureBoundGamePrincipal);
}

internal readonly record struct SecureLoginGenerationResult(
    SecureLoginGenerationStatus Status,
    SecureLoginGeneration? Generation)
{
    public bool IsStarted =>
        Status == SecureLoginGenerationStatus.Started &&
        Generation is not null;
}

internal readonly record struct SecureTicketIssueResult(
    SecureTicketIssueStatus Status,
    SecureGameGrantLease? Lease)
{
    public bool IsIssued =>
        Status == SecureTicketIssueStatus.Issued &&
        Lease is not null;
}

internal readonly record struct SecureTicketConsumeResult(
    SecureTicketConsumeStatus Status,
    SecureBoundGamePrincipal? Principal)
{
    public bool IsAccepted =>
        Status == SecureTicketConsumeStatus.Accepted &&
        Principal is not null;
}

internal readonly record struct SecureGameTicketStoreSnapshot(
    int Capacity,
    int ActiveGenerations,
    int OutstandingTickets);

internal static class SecureTicketModelValidation
{
    public const int ConnectionIdBytes = 16;
    public const int ClientInstanceIdBytes = 16;
    public const int BuildHashBytes = 32;
    public const int GrantIdBytes = 16;
    public const int TicketBytes = 32;

    public static bool IsEndpointRole(SecureEndpointRole role) =>
        role is SecureEndpointRole.Login or SecureEndpointRole.Game;

    public static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (item != 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsDnsName(string? value, int maximumBytes)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > maximumBytes)
        {
            return false;
        }

        Span<byte> bytes = value.Length <= 253
            ? stackalloc byte[value.Length]
            : new byte[value.Length];
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character > 0x7F)
            {
                return false;
            }
            bytes[index] = (byte)character;
        }

        var labelLength = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            var character = bytes[index];
            if (character == (byte)'.')
            {
                if (labelLength == 0 ||
                    bytes[index - 1] == (byte)'-')
                {
                    return false;
                }
                labelLength = 0;
                continue;
            }

            var isLowerLetter =
                character is >= (byte)'a' and <= (byte)'z';
            var isDigit =
                character is >= (byte)'0' and <= (byte)'9';
            if (!isLowerLetter &&
                !isDigit &&
                character != (byte)'-')
            {
                return false;
            }
            if (labelLength == 0 && character == (byte)'-')
            {
                return false;
            }

            labelLength++;
            if (labelLength > 63)
            {
                return false;
            }
        }

        return labelLength > 0 && bytes[^1] != (byte)'-';
    }

    public static bool IsAudience(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isLetter =
                character is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z';
            var isDigit = character is >= '0' and <= '9';
            if (!isLetter &&
                !isDigit &&
                character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static void ValidateAccount(int accountId, string username)
    {
        if (accountId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(accountId));
        }
        if (string.IsNullOrWhiteSpace(username) ||
            username.Length > 32)
        {
            throw new ArgumentException(
                "Canonical username must contain 1..32 characters.",
                nameof(username));
        }

        if (!string.Equals(username, username.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Canonical username cannot have leading or trailing whitespace.",
                nameof(username));
        }

        foreach (var character in username)
        {
            if (character is < (char)0x20 or > (char)0x7E)
            {
                throw new ArgumentException(
                    "Canonical username must contain printable ASCII.",
                    nameof(username));
            }
        }
    }

    public static Guid CreateNonzeroId()
    {
        Span<byte> bytes = stackalloc byte[16];
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                RandomNumberGenerator.Fill(bytes);
                if (!IsAllZero(bytes))
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
            "CSPRNG returned repeated invalid identifiers.");
    }
}
