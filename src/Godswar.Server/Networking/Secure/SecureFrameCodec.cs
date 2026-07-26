using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure;

// Syntax and stream-boundary validation only. The caller owns every buffer and
// must apply the channel-phase state machine before dispatching any payload.
internal static class SecureFrameCodec
{
    public static bool TryDecodeHeader(
        ReadOnlySpan<byte> source,
        SecureEndpointRole endpointRole,
        SecureFrameDirection direction,
        ulong expectedSequence,
        out SecureFrameHeader header)
    {
        header = default;
        if (source.Length != SecureProtocolConstants.FrameHeaderBytes ||
            !SecureProtocolValidation.IsEndpointRole(endpointRole) ||
            !SecureProtocolValidation.IsFrameDirection(direction) ||
            expectedSequence == 0)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(source);
        var type = (SecureFrameType)BinaryPrimitives.ReadUInt16BigEndian(
            source[4..]);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(source[6..]);
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(source[8..]);
        if (flags != 0 ||
            sequence != expectedSequence ||
            payloadLength > SecureProtocolConstants.MaximumPayloadBytes ||
            !IsPayloadValid(
                type,
                endpointRole,
                direction,
                payloadLength))
        {
            return false;
        }

        header = new SecureFrameHeader(payloadLength, type, sequence);
        return true;
    }

    public static bool TryEncodeHeader(
        SecureFrameHeader header,
        SecureEndpointRole endpointRole,
        SecureFrameDirection direction,
        Span<byte> destination)
    {
        if (destination.Length < SecureProtocolConstants.FrameHeaderBytes ||
            header.Sequence == 0 ||
            header.PayloadLength > SecureProtocolConstants.MaximumPayloadBytes ||
            !SecureProtocolValidation.IsEndpointRole(endpointRole) ||
            !SecureProtocolValidation.IsFrameDirection(direction) ||
            !IsPayloadValid(
                header.Type,
                endpointRole,
                direction,
                header.PayloadLength))
        {
            return false;
        }

        var output = destination[..SecureProtocolConstants.FrameHeaderBytes];
        output.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(output, header.PayloadLength);
        BinaryPrimitives.WriteUInt16BigEndian(output[4..], (ushort)header.Type);
        BinaryPrimitives.WriteUInt64BigEndian(output[8..], header.Sequence);
        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> source,
        SecureEndpointRole endpointRole,
        SecureFrameDirection direction,
        ulong expectedSequence,
        out SecureFrameHeader header)
    {
        var status = Read(
            source,
            endpointRole,
            direction,
            expectedSequence,
            out header,
            out var bytesConsumed);
        if (status != SecureDecodeStatus.Done ||
            bytesConsumed != source.Length)
        {
            header = default;
            return false;
        }

        return true;
    }

    public static SecureDecodeStatus Read(
        ReadOnlySpan<byte> source,
        SecureEndpointRole endpointRole,
        SecureFrameDirection direction,
        ulong expectedSequence,
        out SecureFrameHeader header,
        out int bytesConsumed)
    {
        header = default;
        bytesConsumed = 0;
        if (source.Length < SecureProtocolConstants.FrameHeaderBytes)
        {
            return SecureDecodeStatus.NeedMore;
        }

        if (!TryDecodeHeader(
                source[..SecureProtocolConstants.FrameHeaderBytes],
                endpointRole,
                direction,
                expectedSequence,
                out header))
        {
            return SecureDecodeStatus.Rejected;
        }

        var requiredBytes =
            SecureProtocolConstants.FrameHeaderBytes +
            (int)header.PayloadLength;
        if (source.Length < requiredBytes)
        {
            header = default;
            return SecureDecodeStatus.NeedMore;
        }

        bytesConsumed = requiredBytes;
        return SecureDecodeStatus.Done;
    }

    public static bool TryEncode(
        SecureFrameHeader header,
        ReadOnlySpan<byte> payload,
        SecureEndpointRole endpointRole,
        SecureFrameDirection direction,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (payload.Length != header.PayloadLength)
        {
            return false;
        }

        var requiredBytes =
            SecureProtocolConstants.FrameHeaderBytes +
            payload.Length;
        if (destination.Length < requiredBytes)
        {
            return false;
        }

        var output = destination[..requiredBytes];
        if (!TryEncodeHeader(
                header,
                endpointRole,
                direction,
                output[..SecureProtocolConstants.FrameHeaderBytes]))
        {
            return false;
        }

        payload.CopyTo(
            output[SecureProtocolConstants.FrameHeaderBytes..]);
        bytesWritten = requiredBytes;
        return true;
    }

    public static bool TryGetNextSequence(
        ulong currentSequence,
        out ulong nextSequence)
    {
        nextSequence = 0;
        if (currentSequence == 0 || currentSequence == ulong.MaxValue)
        {
            return false;
        }

        nextSequence = currentSequence + 1;
        return true;
    }

    private static bool IsPayloadValid(
        SecureFrameType type,
        SecureEndpointRole endpointRole,
        SecureFrameDirection direction,
        uint payloadLength)
    {
        return type switch
        {
            SecureFrameType.Ping =>
                direction == SecureFrameDirection.ServerToClient &&
                payloadLength == 8,
            SecureFrameType.Pong =>
                direction == SecureFrameDirection.ClientToServer &&
                payloadLength == 8,
            SecureFrameType.Close => payloadLength == 4,
            SecureFrameType.LegacyBytes =>
                payloadLength is >= 1 and <=
                    SecureProtocolConstants.MaximumPayloadBytes,
            SecureFrameType.GameGrant =>
                endpointRole == SecureEndpointRole.Login &&
                direction == SecureFrameDirection.ServerToClient &&
                payloadLength is >=
                    SecureProtocolConstants.MinimumGameGrantBytes and <=
                    SecureProtocolConstants.MaximumGameGrantBytes,
            SecureFrameType.GameBind =>
                endpointRole == SecureEndpointRole.Game &&
                direction == SecureFrameDirection.ClientToServer &&
                payloadLength == SecureProtocolConstants.GameBindBytes,
            SecureFrameType.BindResult =>
                endpointRole == SecureEndpointRole.Game &&
                direction == SecureFrameDirection.ServerToClient &&
                payloadLength == SecureProtocolConstants.BindResultBytes,
            SecureFrameType.UdpBindingGrant =>
                endpointRole == SecureEndpointRole.Game &&
                direction == SecureFrameDirection.ServerToClient &&
                payloadLength ==
                    SecureProtocolConstants.UdpBindingGrantBytes,
            SecureFrameType.RealtimeMovementInput =>
                endpointRole == SecureEndpointRole.Game &&
                direction == SecureFrameDirection.ClientToServer &&
                payloadLength ==
                    SecureProtocolConstants.RealtimeMovementInputBytes,
            _ => false
        };
    }
}
