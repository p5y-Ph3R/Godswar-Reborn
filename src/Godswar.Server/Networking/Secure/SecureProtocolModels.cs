using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure;

internal enum SecureServerPrefaceStatus : byte
{
    Ok = 0,
    UnsupportedVersion = 1,
    WrongEndpoint = 2,
    UnsupportedBuild = 3,
    ServerBusy = 4,
    PolicyRejected = 5
}

internal enum SecureFrameDirection
{
    ClientToServer,
    ServerToClient
}

internal enum SecureDecodeStatus
{
    NeedMore,
    Done,
    Rejected
}

internal enum SecureFrameType : ushort
{
    Ping = 0x0001,
    Pong = 0x0002,
    Close = 0x0003,
    LegacyBytes = 0x0100,
    LegacyCommandOperation = 0x0101,
    LegacyCommandResult = 0x0102,
    GameGrant = 0x0200,
    GameBind = 0x0201,
    BindResult = 0x0202,
    UdpBindingGrant = 0x0203,
    RealtimeMovementInput = 0x0300
}

internal enum SecureBindStatus : ushort
{
    Accepted = 0,
    Rejected = 1,
    ServerBusy = 2,
    PolicyRejected = 3
}

internal static class SecureProtocolConstants
{
    public const ushort ProtocolMajor = 1;
    public const ushort ProtocolMinor = 0;
    public const int ClientPrefaceBytes = 72;
    public const int ServerPrefaceBytes = 40;
    public const int FrameHeaderBytes = 16;
    public const int MaximumPayloadBytes = 16_384;
    public const ushort HeartbeatSeconds = 30;
    public const ushort IdleTimeoutSeconds = 90;
    public const int ConnectionIdBytes = 16;
    public const int ClientInstanceIdBytes = 16;
    public const int BuildHashBytes = 32;
    public const int GrantIdBytes = 16;
    public const int TicketBytes = 32;
    public const int GameGrantFixedBytes = 68;
    public const int MinimumGameGrantBytes = 71;
    public const int MaximumGameGrantBytes = 408;
    public const int GameBindBytes = 52;
    public const int BindResultBytes = 4;
    public const int UdpBindingGrantBytes = 72;
    public const int RealtimeMovementInputBytes = 52;
    public const int LegacyCommandOperationBytes = 24;
    public const byte LegacyCommandOperationVersion = 1;
    public const int LegacyCommandResultBytes = 32;
    public const byte LegacyCommandResultVersion = 1;
}

internal readonly record struct SecureLegacyCommandOperation(
    Guid OperationId,
    ushort PacketLength,
    ushort Opcode);

internal sealed class SecureClientPreface
{
    private readonly byte[] _clientInstanceId;
    private readonly byte[] _originSha256;

    public SecureClientPreface(
        SecureEndpointRole role,
        ReadOnlySpan<byte> clientInstanceId,
        ReadOnlySpan<byte> originSha256)
    {
        if (!SecureProtocolValidation.IsEndpointRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
        if (clientInstanceId.Length != SecureProtocolConstants.ClientInstanceIdBytes)
        {
            throw new ArgumentException(
                "Client-instance ID must be exactly 16 bytes.",
                nameof(clientInstanceId));
        }
        if (SecureProtocolValidation.IsAllZero(clientInstanceId))
        {
            throw new ArgumentException(
                "Client-instance ID must be nonzero.",
                nameof(clientInstanceId));
        }
        if (originSha256.Length != SecureProtocolConstants.BuildHashBytes)
        {
            throw new ArgumentException(
                "Origin SHA-256 must be exactly 32 bytes.",
                nameof(originSha256));
        }

        Role = role;
        _clientInstanceId = clientInstanceId.ToArray();
        _originSha256 = originSha256.ToArray();
    }

    public static SecureClientPreface Create(
        SecureEndpointRole role,
        ReadOnlySpan<byte> originSha256)
    {
        var clientInstanceId = new byte[
            SecureProtocolConstants.ClientInstanceIdBytes];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            RandomNumberGenerator.Fill(clientInstanceId);
            if (!SecureProtocolValidation.IsAllZero(clientInstanceId))
            {
                return new SecureClientPreface(
                    role,
                    clientInstanceId,
                    originSha256);
            }
        }

        throw new CryptographicException(
            "CSPRNG returned an invalid client-instance ID.");
    }

    public SecureEndpointRole Role { get; }

    public ReadOnlyMemory<byte> ClientInstanceId => _clientInstanceId;

    public ReadOnlyMemory<byte> OriginSha256 => _originSha256;
}

internal sealed class SecureServerPreface
{
    private readonly byte[] _connectionId;

    public SecureServerPreface(
        SecureServerPrefaceStatus status,
        SecureEndpointRole role,
        ReadOnlySpan<byte> connectionId)
    {
        if (!SecureProtocolValidation.IsServerStatus(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (!SecureProtocolValidation.IsEndpointRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
        if (connectionId.Length != SecureProtocolConstants.ConnectionIdBytes)
        {
            throw new ArgumentException(
                "TLS connection ID must be exactly 16 bytes.",
                nameof(connectionId));
        }
        var connectionIdIsZero =
            SecureProtocolValidation.IsAllZero(connectionId);
        if ((status == SecureServerPrefaceStatus.Ok &&
                connectionIdIsZero) ||
            (status != SecureServerPrefaceStatus.Ok &&
                !connectionIdIsZero))
        {
            throw new ArgumentException(
                "Success requires a nonzero connection ID; rejection requires zero.",
                nameof(connectionId));
        }

        Status = status;
        Role = role;
        _connectionId = connectionId.ToArray();
    }

    public SecureServerPrefaceStatus Status { get; }

    public SecureEndpointRole Role { get; }

    public ReadOnlyMemory<byte> ConnectionId => _connectionId;
}

internal readonly record struct SecureFrameHeader(
    uint PayloadLength,
    SecureFrameType Type,
    ulong Sequence);

internal readonly record struct SecureBindResult(SecureBindStatus Status)
{
    public bool IsAccepted => Status == SecureBindStatus.Accepted;
}
