using System.Buffers.Binary;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpProtectedTestData
{
    public const uint ServerId = 0x01020304;

    public static readonly byte[] BindingSecret =
        Enumerable.Range(0, 32)
            .Select(static value => checked((byte)value))
            .ToArray();

    public static readonly byte[] ConnectionId =
        Enumerable.Range(0x10, 16)
            .Select(static value => checked((byte)value))
            .ToArray();

    public static SecureUdpConnectionKey ConnectionKey
    {
        get
        {
            Check.True(
                SecureUdpConnectionKey.TryCreate(
                    ConnectionId,
                    out var value),
                "protected UDP test connection ID");
            return value;
        }
    }

    public static byte[] CreatePing(
        ulong pingId = 1,
        ulong monotonicMilliseconds = 123_456_789)
    {
        var payload = new byte[
            SecureUdpProtectedConstants.PingPayloadBytes];
        BinaryPrimitives.WriteUInt64BigEndian(payload, pingId);
        BinaryPrimitives.WriteUInt64BigEndian(
            payload.AsSpan(sizeof(ulong)),
            monotonicMilliseconds);
        return payload;
    }

    public static byte[] CreatePong(
        ulong pingId = 1,
        ulong monotonicMilliseconds = 123_456_789)
    {
        var ping = CreatePing(pingId, monotonicMilliseconds);
        var payload = new byte[
            SecureUdpProtectedConstants.PongPayloadBytes];
        ping.CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt64BigEndian(
            payload.AsSpan(16),
            1_700_000_000_000);
        BinaryPrimitives.WriteUInt64BigEndian(
            payload.AsSpan(24),
            1_700_000_000_001);
        return payload;
    }

    public static byte[] CreateBindingConfirm(
        ulong revision = 1)
    {
        var payload = new byte[
            SecureUdpProtectedConstants.BindingConfirmPayloadBytes];
        for (var index = 0; index < 16; index++)
        {
            payload[index] = checked((byte)(0xA0 + index));
        }
        BinaryPrimitives.WriteUInt64BigEndian(
            payload.AsSpan(16),
            revision);
        BinaryPrimitives.WriteUInt64BigEndian(
            payload.AsSpan(24),
            1_700_000_000_000);
        return payload;
    }

    public static byte[] DeriveKey(
        SecureUdpTrafficDirection direction,
        uint keyEpoch = 1)
    {
        var key = new byte[SecureUdpProtectedConstants.KeyBytes];
        Check.True(
            SecureUdpTrafficKeyDerivation.TryDeriveKey(
                BindingSecret,
                ConnectionId,
                ServerId,
                direction,
                keyEpoch,
                key),
            "protected UDP test key derivation");
        return key;
    }

    public static SecureUdpProtectedHeader CreateHeader(
        SecureUdpProtectedMessageType messageType,
        int payloadBytes,
        uint keyEpoch = 1,
        ulong sequence = 0,
        SecureUdpAcknowledgement acknowledgement = default)
    {
        return new SecureUdpProtectedHeader(
            ConnectionKey,
            keyEpoch,
            sequence,
            acknowledgement,
            messageType,
            checked((ushort)payloadBytes));
    }
}
