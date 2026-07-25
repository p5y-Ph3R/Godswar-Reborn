using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure.Udp;

internal enum SecureUdpTrafficDirection : byte
{
    ClientToServer = 1,
    ServerToClient = 2
}

internal enum SecureUdpPeerRole : byte
{
    Client = 1,
    Server = 2
}

internal enum SecureUdpProtectedMessageType : byte
{
    Ping = 1,
    Pong = 2,
    BindingConfirm = 3
}

internal enum SecureUdpProtectedError : byte
{
    None = 0,
    InvalidArgument = 1,
    DestinationTooSmall = 2,
    MalformedDatagram = 3,
    ConnectionMismatch = 4,
    UnknownKeyEpoch = 5,
    AuthenticationFailed = 6,
    ReplayRejected = 7,
    InvalidPayload = 8,
    InvalidMessageDirection = 9,
    SequenceExhausted = 10,
    EpochExhausted = 11,
    Disposed = 12
}

internal enum SecureUdpKeyRotationStatus : byte
{
    NotDue = 1,
    Rotated = 2,
    EpochExhausted = 3,
    Disposed = 4
}

internal static class SecureUdpProtectedConstants
{
    public const uint Magic = 0x47575350; // GWSP
    public const ushort HeaderBytes = 64;
    public const byte ProtocolMajor = 1;
    public const byte ProtocolMinor = 0;
    public const byte PacketType = 1;
    public const byte Flags = 0;
    public const int ConnectionIdBytes = 16;
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int MinimumDatagramBytes = HeaderBytes + TagBytes;
    public const int MaximumDatagramBytes = 1_200;
    public const int MaximumPayloadBytes =
        MaximumDatagramBytes - HeaderBytes - TagBytes;
    public const int PingPayloadBytes = 16;
    public const int PongPayloadBytes = 32;
    public const int BindingConfirmPayloadBytes = 32;
    public const int ReplayWindowBits = 128;
    public const uint InitialKeyEpoch = 1;
}

internal readonly record struct SecureUdpAcknowledgement(
    uint KeyEpoch,
    ulong Sequence,
    ulong PreviousMask)
{
    public static SecureUdpAcknowledgement None => default;

    public bool IsValid()
    {
        if (KeyEpoch == 0)
        {
            return Sequence == 0 && PreviousMask == 0;
        }

        if (Sequence >= 64)
        {
            return true;
        }

        return (PreviousMask >> checked((int)Sequence)) == 0;
    }
}

internal readonly record struct SecureUdpProtectedHeader(
    SecureUdpConnectionKey ConnectionId,
    uint KeyEpoch,
    ulong Sequence,
    SecureUdpAcknowledgement Acknowledgement,
    SecureUdpProtectedMessageType MessageType,
    ushort PayloadBytes)
{
    public int DatagramBytes => checked(
        SecureUdpProtectedConstants.HeaderBytes +
        PayloadBytes +
        SecureUdpProtectedConstants.TagBytes);
}

internal readonly record struct SecureUdpProtectedSessionSnapshot(
    uint SendKeyEpoch,
    ulong NextSendSequence,
    bool SendSequenceExhausted,
    ulong PacketsSentInEpoch,
    uint ReceiveKeyEpoch,
    uint PreviousReceiveKeyEpoch,
    bool HasReceivedCurrentEpoch,
    ulong HighestReceivedSequence,
    ulong ReceiveReplayBitsLow,
    ulong ReceiveReplayBitsHigh);

internal static class SecureUdpProtectedPayload
{
    public static bool IsValidLength(
        SecureUdpProtectedMessageType type,
        int payloadBytes)
    {
        return type switch
        {
            SecureUdpProtectedMessageType.Ping =>
                payloadBytes ==
                    SecureUdpProtectedConstants.PingPayloadBytes,
            SecureUdpProtectedMessageType.Pong =>
                payloadBytes ==
                    SecureUdpProtectedConstants.PongPayloadBytes,
            SecureUdpProtectedMessageType.BindingConfirm =>
                payloadBytes ==
                    SecureUdpProtectedConstants.BindingConfirmPayloadBytes,
            _ => false
        };
    }

    public static bool IsValidContent(
        SecureUdpProtectedMessageType type,
        ReadOnlySpan<byte> payload)
    {
        if (!IsValidLength(type, payload.Length))
        {
            return false;
        }

        return type switch
        {
            SecureUdpProtectedMessageType.Ping =>
                BinaryPrimitives.ReadUInt64BigEndian(payload) != 0,
            SecureUdpProtectedMessageType.Pong =>
                BinaryPrimitives.ReadUInt64BigEndian(payload) != 0 &&
                BinaryPrimitives.ReadUInt64BigEndian(payload[16..]) != 0 &&
                BinaryPrimitives.ReadUInt64BigEndian(payload[24..]) != 0,
            SecureUdpProtectedMessageType.BindingConfirm =>
                !SecureUdpBindingCodec.IsAllZero(payload[..16]) &&
                BinaryPrimitives.ReadUInt64BigEndian(payload[16..]) != 0 &&
                BinaryPrimitives.ReadUInt64BigEndian(payload[24..]) != 0,
            _ => false
        };
    }
}
