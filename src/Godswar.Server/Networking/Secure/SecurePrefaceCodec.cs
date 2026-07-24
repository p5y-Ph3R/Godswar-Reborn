using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure;

internal static class SecurePrefaceCodec
{
    private static ReadOnlySpan<byte> ClientMagic => "GWSC"u8;
    private static ReadOnlySpan<byte> ServerMagic => "GWSS"u8;

    public static bool TryEncodeClient(
        SecureClientPreface? preface,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (preface is null ||
            destination.Length < SecureProtocolConstants.ClientPrefaceBytes)
        {
            return false;
        }

        var output = destination[..SecureProtocolConstants.ClientPrefaceBytes];
        output.Clear();
        ClientMagic.CopyTo(output);
        BinaryPrimitives.WriteUInt16BigEndian(output[4..], 72);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[6..],
            SecureProtocolConstants.ProtocolMajor);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[8..],
            SecureProtocolConstants.ProtocolMinor);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[10..],
            SecureProtocolConstants.ProtocolMinor);
        output[12] = (byte)preface.Role;
        BinaryPrimitives.WriteUInt32BigEndian(
            output[20..],
            SecureProtocolConstants.MaximumPayloadBytes);
        preface.ClientInstanceId.Span.CopyTo(output[24..40]);
        preface.OriginSha256.Span.CopyTo(output[40..72]);
        bytesWritten = output.Length;
        return true;
    }

    public static bool TryDecodeClient(
        ReadOnlySpan<byte> source,
        SecureEndpointRole expectedRole,
        out SecureClientPreface? preface)
    {
        preface = null;
        if (source.Length != SecureProtocolConstants.ClientPrefaceBytes ||
            !SecureProtocolValidation.IsEndpointRole(expectedRole) ||
            !source[..4].SequenceEqual(ClientMagic) ||
            BinaryPrimitives.ReadUInt16BigEndian(source[4..]) != 72 ||
            BinaryPrimitives.ReadUInt16BigEndian(source[6..]) !=
                SecureProtocolConstants.ProtocolMajor ||
            BinaryPrimitives.ReadUInt16BigEndian(source[8..]) !=
                SecureProtocolConstants.ProtocolMinor ||
            BinaryPrimitives.ReadUInt16BigEndian(source[10..]) !=
                SecureProtocolConstants.ProtocolMinor ||
            source[12] != (byte)expectedRole ||
            source[13] != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(source[14..]) != 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(source[16..]) != 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(source[20..]) !=
                SecureProtocolConstants.MaximumPayloadBytes ||
            SecureProtocolValidation.IsAllZero(source[24..40]))
        {
            return false;
        }

        preface = new SecureClientPreface(
            expectedRole,
            source[24..40],
            source[40..72]);
        return true;
    }

    public static SecureDecodeStatus ReadClient(
        ReadOnlySpan<byte> source,
        SecureEndpointRole expectedRole,
        out SecureClientPreface? preface,
        out int bytesConsumed)
    {
        preface = null;
        bytesConsumed = 0;
        if (source.Length < SecureProtocolConstants.ClientPrefaceBytes)
        {
            return SecureDecodeStatus.NeedMore;
        }

        if (!TryDecodeClient(
                source[..SecureProtocolConstants.ClientPrefaceBytes],
                expectedRole,
                out preface))
        {
            return SecureDecodeStatus.Rejected;
        }

        bytesConsumed = SecureProtocolConstants.ClientPrefaceBytes;
        return SecureDecodeStatus.Done;
    }

    public static bool TryEncodeServer(
        SecureServerPreface? preface,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (preface is null ||
            destination.Length < SecureProtocolConstants.ServerPrefaceBytes)
        {
            return false;
        }

        var output = destination[..SecureProtocolConstants.ServerPrefaceBytes];
        output.Clear();
        ServerMagic.CopyTo(output);
        BinaryPrimitives.WriteUInt16BigEndian(output[4..], 40);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[6..],
            SecureProtocolConstants.ProtocolMajor);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[8..],
            SecureProtocolConstants.ProtocolMinor);
        output[10] = (byte)preface.Status;
        output[11] = (byte)preface.Role;
        BinaryPrimitives.WriteUInt32BigEndian(
            output[16..],
            SecureProtocolConstants.MaximumPayloadBytes);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[20..],
            SecureProtocolConstants.HeartbeatSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[22..],
            SecureProtocolConstants.IdleTimeoutSeconds);
        preface.ConnectionId.Span.CopyTo(output[24..40]);
        bytesWritten = output.Length;
        return true;
    }

    public static bool TryDecodeServer(
        ReadOnlySpan<byte> source,
        SecureEndpointRole expectedRole,
        out SecureServerPreface? preface)
    {
        preface = null;
        if (source.Length != SecureProtocolConstants.ServerPrefaceBytes ||
            !SecureProtocolValidation.IsEndpointRole(expectedRole) ||
            !source[..4].SequenceEqual(ServerMagic) ||
            BinaryPrimitives.ReadUInt16BigEndian(source[4..]) != 40 ||
            BinaryPrimitives.ReadUInt16BigEndian(source[6..]) !=
                SecureProtocolConstants.ProtocolMajor ||
            BinaryPrimitives.ReadUInt16BigEndian(source[8..]) !=
                SecureProtocolConstants.ProtocolMinor ||
            !SecureProtocolValidation.IsServerStatus(
                (SecureServerPrefaceStatus)source[10]) ||
            source[11] != (byte)expectedRole ||
            BinaryPrimitives.ReadUInt32BigEndian(source[12..]) != 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(source[16..]) !=
                SecureProtocolConstants.MaximumPayloadBytes ||
            BinaryPrimitives.ReadUInt16BigEndian(source[20..]) !=
                SecureProtocolConstants.HeartbeatSeconds ||
            BinaryPrimitives.ReadUInt16BigEndian(source[22..]) !=
                SecureProtocolConstants.IdleTimeoutSeconds)
        {
            return false;
        }

        var status = (SecureServerPrefaceStatus)source[10];
        var connectionIdIsZero =
            SecureProtocolValidation.IsAllZero(source[24..40]);
        if ((status == SecureServerPrefaceStatus.Ok &&
                connectionIdIsZero) ||
            (status != SecureServerPrefaceStatus.Ok &&
                !connectionIdIsZero))
        {
            return false;
        }

        preface = new SecureServerPreface(
            status,
            expectedRole,
            source[24..40]);
        return true;
    }

    public static SecureDecodeStatus ReadServer(
        ReadOnlySpan<byte> source,
        SecureEndpointRole expectedRole,
        out SecureServerPreface? preface,
        out int bytesConsumed)
    {
        preface = null;
        bytesConsumed = 0;
        if (source.Length < SecureProtocolConstants.ServerPrefaceBytes)
        {
            return SecureDecodeStatus.NeedMore;
        }

        if (!TryDecodeServer(
                source[..SecureProtocolConstants.ServerPrefaceBytes],
                expectedRole,
                out preface))
        {
            return SecureDecodeStatus.Rejected;
        }

        bytesConsumed = SecureProtocolConstants.ServerPrefaceBytes;
        return SecureDecodeStatus.Done;
    }
}
