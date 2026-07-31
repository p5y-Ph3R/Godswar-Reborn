using System.Net;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.Networking.Backhaul;

internal static class BackhaulProtocolConstants
{
    public const uint Magic = 0x47574248; // GWBH
    public const ushort Version = 1;
    public const int HeaderBytes = 12;
    public const int UsernameBytes = 32;
    public const int ServerNodeIdBytes = 64;
    public const int OpenSessionPayloadBytes = 211;
    public const int OpenSessionFrameBytes =
        HeaderBytes + OpenSessionPayloadBytes;
    public const int AdmissionResponsePayloadBytes = 20;
    public const int AdmissionResponseFrameBytes =
        HeaderBytes + AdmissionResponsePayloadBytes;
    public const int MaximumFrameBytes = OpenSessionFrameBytes;

    public static readonly TimeSpan MinimumAdmissionLifetime =
        TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaximumAdmissionLifetime =
        TimeSpan.FromMinutes(5);
}

internal enum BackhaulMessageType : ushort
{
    OpenSession = 1,
    AdmissionResponse = 2
}

internal enum BackhaulAdmissionStatus : ushort
{
    Accepted = 0,
    Malformed = 1,
    VersionRejected = 2,
    PolicyRejected = 3,
    RouteRejected = 4,
    ReplayRejected = 5,
    CapacityExceeded = 6,
    Expired = 7,
    AccountAlreadyActive = 8,
    Draining = 9
}

internal enum BackhaulDecodeFailure : byte
{
    None = 0,
    InvalidLength = 1,
    InvalidMagic = 2,
    UnsupportedVersion = 3,
    WrongMessageType = 4,
    InvalidPayloadLength = 5,
    InvalidReservedBytes = 6,
    InvalidAdmission = 7,
    UnknownStatus = 8
}

internal enum BackhaulTimeoutStage : byte
{
    Connect = 1,
    TlsHandshake = 2,
    OpenSessionWrite = 3,
    AdmissionResponseRead = 4,
    WorkerOpenSessionRead = 5,
    WorkerAdmissionResponseWrite = 6,
    TransportWrite = 7
}

internal sealed class BackhaulTimeoutException :
    TimeoutException
{
    public BackhaulTimeoutException(
        BackhaulTimeoutStage stage)
        : base($"Backhaul deadline exceeded at {stage}.")
    {
        Stage = stage;
    }

    public BackhaulTimeoutStage Stage { get; }
}

/// <summary>
/// Authenticated metadata supplied by the gateway to exactly one worker
/// session. It is authoritative only after mutual TLS and worker admission.
/// </summary>
internal sealed record GatewayWorldAdmission
{
    public GatewayWorldAdmission(
        Guid gatewayBootId,
        Guid connectionId,
        Guid loginGenerationId,
        int accountId,
        int characterId,
        string username,
        RealmId realmId,
        MapId mapId,
        WorldInstanceId worldInstanceId,
        ServerNodeId targetNodeId,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        IPEndPoint observedClientSource)
    {
        RequireNonempty(gatewayBootId, nameof(gatewayBootId));
        RequireNonempty(connectionId, nameof(connectionId));
        RequireNonempty(
            loginGenerationId,
            nameof(loginGenerationId));
        SecureTicketModelValidation.ValidateAccount(
            accountId,
            username);
        if (username.Any(static character =>
                character is < '!' or > '~'))
        {
            throw new ArgumentException(
                "Gateway usernames must contain printable non-space ASCII.",
                nameof(username));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(characterId);
        if (!realmId.IsValid)
        {
            throw new ArgumentException(
                "A valid realm ID is required.",
                nameof(realmId));
        }
        if (!worldInstanceId.IsValid)
        {
            throw new ArgumentException(
                "A valid world-instance ID is required.",
                nameof(worldInstanceId));
        }
        if (!mapId.IsValid)
        {
            throw new ArgumentException(
                "A valid map ID is required.",
                nameof(mapId));
        }
        if (!targetNodeId.IsValid)
        {
            throw new ArgumentException(
                "A valid target node ID is required.",
                nameof(targetNodeId));
        }

        var issued = RequireWholeUtcMillisecond(
            issuedAtUtc,
            nameof(issuedAtUtc));
        var expires = RequireWholeUtcMillisecond(
            expiresAtUtc,
            nameof(expiresAtUtc));
        var lifetime = expires - issued;
        if (lifetime <
                BackhaulProtocolConstants.MinimumAdmissionLifetime ||
            lifetime >
                BackhaulProtocolConstants.MaximumAdmissionLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "Admission lifetime must be between one second and " +
                "five minutes.");
        }

        var source = NormalizeAndValidateSource(
            observedClientSource);

        GatewayBootId = gatewayBootId;
        ConnectionId = connectionId;
        LoginGenerationId = loginGenerationId;
        AccountId = accountId;
        CharacterId = characterId;
        Username = username;
        RealmId = realmId;
        MapId = mapId;
        WorldInstanceId = worldInstanceId;
        TargetNodeId = targetNodeId;
        IssuedAtUtc = issued;
        ExpiresAtUtc = expires;
        ObservedClientSource = source;
    }

    public Guid GatewayBootId { get; }

    public Guid ConnectionId { get; }

    public Guid LoginGenerationId { get; }

    public int AccountId { get; }

    /// <summary>
    /// Zero is reserved for a characterless account at bootstrap.
    /// </summary>
    public int CharacterId { get; }

    public string Username { get; }

    public RealmId RealmId { get; }

    public MapId MapId { get; }

    public WorldInstanceId WorldInstanceId { get; }

    public ServerNodeId TargetNodeId { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public IPEndPoint ObservedClientSource { get; }

    public SecureBoundGamePrincipal CreatePrincipal() =>
        new(
            AccountId,
            Username,
            SecureGamePermissions.EnterWorld,
            LoginGenerationId);

    private static void RequireNonempty(
        Guid value,
        string parameter)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Backhaul identifiers must be nonzero.",
                parameter);
        }
    }

    private static DateTimeOffset RequireWholeUtcMillisecond(
        DateTimeOffset value,
        string parameter)
    {
        DateTimeOffset utc;
        try
        {
            utc = DateTimeOffset.FromUnixTimeMilliseconds(
                value.ToUnixTimeMilliseconds());
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new ArgumentException(
                "Admission timestamps must fit Unix milliseconds.",
                parameter,
                error);
        }

        if (utc != value.ToUniversalTime())
        {
            throw new ArgumentException(
                "Admission timestamps must have whole-millisecond " +
                "precision.",
                parameter);
        }

        return utc;
    }

    private static IPEndPoint NormalizeAndValidateSource(
        IPEndPoint source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                "The observed client source port is invalid.");
        }

        var address = source.Address.IsIPv4MappedToIPv6
            ? source.Address.MapToIPv4()
            : source.Address;
        if (address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6None) ||
            IsMulticast(address) ||
            IsIpv4Broadcast(address) ||
            IsUnroutableScopedAddress(address))
        {
            throw new ArgumentException(
                "The observed client source must be a concrete unicast " +
                "address.",
                nameof(source));
        }

        return new IPEndPoint(address, source.Port);
    }

    private static bool IsMulticast(IPAddress address)
    {
        if (address.IsIPv6Multicast)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] is >= 224 and <= 239;
    }

    private static bool IsIpv4Broadcast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
            bytes.All(static value => value == byte.MaxValue);
    }

    private static bool IsUnroutableScopedAddress(
        IPAddress address)
    {
        if (address.IsIPv6LinkLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 0;
    }
}

internal readonly record struct BackhaulAdmissionResponse
{
    public BackhaulAdmissionResponse(
        BackhaulAdmissionStatus status,
        Guid connectionId)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (status == BackhaulAdmissionStatus.Accepted &&
            connectionId == Guid.Empty)
        {
            throw new ArgumentException(
                "An accepted response requires the admitted connection ID.",
                nameof(connectionId));
        }

        Status = status;
        ConnectionId = connectionId;
    }

    public BackhaulAdmissionStatus Status { get; }

    public Guid ConnectionId { get; }

    public bool IsAccepted =>
        Status == BackhaulAdmissionStatus.Accepted;
}

internal readonly record struct BackhaulRuntimeLimits(
    TimeSpan ConnectTimeout,
    TimeSpan TlsHandshakeTimeout,
    TimeSpan OpenSessionTimeout,
    TimeSpan WriteTimeout)
{
    public static BackhaulRuntimeLimits Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5));

    public BackhaulRuntimeLimits Validate()
    {
        Require(
            ConnectTimeout,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(30),
            nameof(ConnectTimeout));
        Require(
            TlsHandshakeTimeout,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30),
            nameof(TlsHandshakeTimeout));
        Require(
            OpenSessionTimeout,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(30),
            nameof(OpenSessionTimeout));
        Require(
            WriteTimeout,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(30),
            nameof(WriteTimeout));
        return this;
    }

    private static void Require(
        TimeSpan value,
        TimeSpan minimum,
        TimeSpan maximum,
        string parameter)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                $"Backhaul deadlines must be between {minimum} and " +
                $"{maximum}.");
        }
    }
}

/// <summary>
/// Narrow marker used by ClientSession integration. It deliberately does not
/// imply support for client-facing TLS control frames or secure UDP.
/// </summary>
internal interface IAuthenticatedGameTransport :
    ISecureLegacyByteTransport
{
    SecureBoundGamePrincipal BoundGamePrincipal { get; }

    GatewayWorldAdmission WorldAdmission { get; }
}
