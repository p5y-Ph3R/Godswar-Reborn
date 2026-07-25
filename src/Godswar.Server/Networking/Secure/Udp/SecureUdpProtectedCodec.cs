using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure.Udp;

internal static class SecureUdpProtectedCodec
{
    private const int ConnectionIdOffset = 12;
    private const int KeyEpochOffset = 28;
    private const int SequenceOffset = 32;
    private const int AckEpochOffset = 40;
    private const int AckSequenceOffset = 44;
    private const int AckMaskOffset = 52;
    private const int MessageTypeOffset = 60;
    private const int ReservedOffset = 61;
    private const int PayloadLengthOffset = 62;

    public static bool TryEncrypt(
        in SecureUdpProtectedHeader header,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> plaintext,
        Span<byte> destination,
        out int bytesWritten,
        out SecureUdpProtectedError error)
    {
        bytesWritten = 0;
        error = SecureUdpProtectedError.InvalidArgument;
        if (key.Length != SecureUdpProtectedConstants.KeyBytes ||
            SecureUdpBindingCodec.IsAllZero(key) ||
            plaintext.Length != header.PayloadBytes ||
            !SecureUdpProtectedPayload.IsValidContent(
                header.MessageType,
                plaintext) ||
            plaintext.Overlaps(destination))
        {
            return false;
        }
        if (destination.Length < header.DatagramBytes)
        {
            error = SecureUdpProtectedError.DestinationTooSmall;
            return false;
        }

        var output = destination[..header.DatagramBytes];
        if (!TryWriteHeader(header, output))
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[
            SecureUdpProtectedConstants.NonceBytes];
        WriteNonce(header.KeyEpoch, header.Sequence, nonce);
        var ciphertext = output.Slice(
            SecureUdpProtectedConstants.HeaderBytes,
            plaintext.Length);
        var tag = output.Slice(
            SecureUdpProtectedConstants.HeaderBytes +
                plaintext.Length,
            SecureUdpProtectedConstants.TagBytes);
        try
        {
            using var aes = new AesGcm(
                key,
                SecureUdpProtectedConstants.TagBytes);
            aes.Encrypt(
                nonce,
                plaintext,
                ciphertext,
                tag,
                output[..SecureUdpProtectedConstants.HeaderBytes]);
            bytesWritten = output.Length;
            error = SecureUdpProtectedError.None;
            return true;
        }
        catch (CryptographicException)
        {
            output.Clear();
            error = SecureUdpProtectedError.AuthenticationFailed;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    public static bool TryDecrypt(
        ReadOnlySpan<byte> datagram,
        ReadOnlySpan<byte> key,
        Span<byte> plaintextDestination,
        out SecureUdpProtectedHeader header,
        out int payloadBytes,
        out SecureUdpProtectedError error)
    {
        header = default;
        payloadBytes = 0;
        error = SecureUdpProtectedError.MalformedDatagram;
        if (!TryDecodeHeader(datagram, out var parsed))
        {
            return false;
        }
        if (key.Length != SecureUdpProtectedConstants.KeyBytes ||
            SecureUdpBindingCodec.IsAllZero(key))
        {
            error = SecureUdpProtectedError.InvalidArgument;
            return false;
        }
        if (plaintextDestination.Length < parsed.PayloadBytes)
        {
            error = SecureUdpProtectedError.DestinationTooSmall;
            return false;
        }
        if (datagram.Overlaps(plaintextDestination))
        {
            error = SecureUdpProtectedError.InvalidArgument;
            return false;
        }

        Span<byte> nonce = stackalloc byte[
            SecureUdpProtectedConstants.NonceBytes];
        WriteNonce(parsed.KeyEpoch, parsed.Sequence, nonce);
        var plaintext =
            plaintextDestination[..parsed.PayloadBytes];
        var ciphertext = datagram.Slice(
            SecureUdpProtectedConstants.HeaderBytes,
            parsed.PayloadBytes);
        var tag = datagram.Slice(
            SecureUdpProtectedConstants.HeaderBytes +
                parsed.PayloadBytes,
            SecureUdpProtectedConstants.TagBytes);
        try
        {
            using var aes = new AesGcm(
                key,
                SecureUdpProtectedConstants.TagBytes);
            aes.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext,
                datagram[..SecureUdpProtectedConstants.HeaderBytes]);
        }
        catch (CryptographicException)
        {
            plaintext.Clear();
            error = SecureUdpProtectedError.AuthenticationFailed;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }

        if (!SecureUdpProtectedPayload.IsValidContent(
                parsed.MessageType,
                plaintext))
        {
            plaintext.Clear();
            error = SecureUdpProtectedError.InvalidPayload;
            return false;
        }

        header = parsed;
        payloadBytes = parsed.PayloadBytes;
        error = SecureUdpProtectedError.None;
        return true;
    }

    public static bool TryDecodeHeader(
        ReadOnlySpan<byte> datagram,
        out SecureUdpProtectedHeader header)
    {
        header = default;
        if (datagram.Length is <
                SecureUdpProtectedConstants.MinimumDatagramBytes or
                > SecureUdpProtectedConstants.MaximumDatagramBytes ||
            BinaryPrimitives.ReadUInt32BigEndian(datagram) !=
                SecureUdpProtectedConstants.Magic ||
            BinaryPrimitives.ReadUInt16BigEndian(datagram[4..]) !=
                SecureUdpProtectedConstants.HeaderBytes ||
            datagram[6] != SecureUdpProtectedConstants.ProtocolMajor ||
            datagram[7] != SecureUdpProtectedConstants.ProtocolMinor ||
            datagram[8] != SecureUdpProtectedConstants.PacketType ||
            datagram[9] != SecureUdpProtectedConstants.Flags ||
            BinaryPrimitives.ReadUInt16BigEndian(datagram[10..]) !=
                datagram.Length ||
            datagram[ReservedOffset] != 0)
        {
            return false;
        }

        var payloadBytes = BinaryPrimitives.ReadUInt16BigEndian(
            datagram[PayloadLengthOffset..]);
        if (payloadBytes >
                SecureUdpProtectedConstants.MaximumPayloadBytes ||
            datagram.Length !=
                SecureUdpProtectedConstants.HeaderBytes +
                payloadBytes +
                SecureUdpProtectedConstants.TagBytes)
        {
            return false;
        }

        if (!SecureUdpConnectionKey.TryCreate(
                datagram.Slice(
                    ConnectionIdOffset,
                    SecureUdpProtectedConstants.ConnectionIdBytes),
                out var connectionId))
        {
            return false;
        }

        var keyEpoch = BinaryPrimitives.ReadUInt32BigEndian(
            datagram[KeyEpochOffset..]);
        if (keyEpoch == 0)
        {
            return false;
        }

        var acknowledgement = new SecureUdpAcknowledgement(
            BinaryPrimitives.ReadUInt32BigEndian(
                datagram[AckEpochOffset..]),
            BinaryPrimitives.ReadUInt64BigEndian(
                datagram[AckSequenceOffset..]),
            BinaryPrimitives.ReadUInt64BigEndian(
                datagram[AckMaskOffset..]));
        if (!acknowledgement.IsValid())
        {
            return false;
        }

        var messageType =
            (SecureUdpProtectedMessageType)datagram[MessageTypeOffset];
        if (!SecureUdpProtectedPayload.IsValidLength(
                messageType,
                payloadBytes))
        {
            return false;
        }

        header = new SecureUdpProtectedHeader(
            connectionId,
            keyEpoch,
            BinaryPrimitives.ReadUInt64BigEndian(
                datagram[SequenceOffset..]),
            acknowledgement,
            messageType,
            payloadBytes);
        return true;
    }

    internal static bool TryWriteHeader(
        in SecureUdpProtectedHeader header,
        Span<byte> destination)
    {
        if ((header.ConnectionId.High | header.ConnectionId.Low) == 0 ||
            header.KeyEpoch == 0 ||
            !header.Acknowledgement.IsValid() ||
            !SecureUdpProtectedPayload.IsValidLength(
                header.MessageType,
                header.PayloadBytes) ||
            destination.Length < header.DatagramBytes ||
            header.DatagramBytes >
                SecureUdpProtectedConstants.MaximumDatagramBytes)
        {
            return false;
        }

        var output =
            destination[..SecureUdpProtectedConstants.HeaderBytes];
        output.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(
            output,
            SecureUdpProtectedConstants.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[4..],
            SecureUdpProtectedConstants.HeaderBytes);
        output[6] = SecureUdpProtectedConstants.ProtocolMajor;
        output[7] = SecureUdpProtectedConstants.ProtocolMinor;
        output[8] = SecureUdpProtectedConstants.PacketType;
        output[9] = SecureUdpProtectedConstants.Flags;
        BinaryPrimitives.WriteUInt16BigEndian(
            output[10..],
            checked((ushort)header.DatagramBytes));
        header.ConnectionId.WriteTo(output[ConnectionIdOffset..]);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[KeyEpochOffset..],
            header.KeyEpoch);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[SequenceOffset..],
            header.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[AckEpochOffset..],
            header.Acknowledgement.KeyEpoch);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[AckSequenceOffset..],
            header.Acknowledgement.Sequence);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[AckMaskOffset..],
            header.Acknowledgement.PreviousMask);
        output[MessageTypeOffset] = (byte)header.MessageType;
        BinaryPrimitives.WriteUInt16BigEndian(
            output[PayloadLengthOffset..],
            header.PayloadBytes);
        return true;
    }

    private static void WriteNonce(
        uint keyEpoch,
        ulong sequence,
        Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, keyEpoch);
        BinaryPrimitives.WriteUInt64BigEndian(
            destination[sizeof(uint)..],
            sequence);
    }
}
