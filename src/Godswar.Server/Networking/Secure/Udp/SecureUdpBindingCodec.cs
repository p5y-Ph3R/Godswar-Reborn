using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure.Udp;

internal static class SecureUdpBindingCodec
{
    private const int ConnectionIdOffset = 12;
    private const int KeyEpochOffset = 28;
    private const int SequenceOffset = 32;
    private const int PayloadLengthOffset = 40;
    private const int ReservedOffset = 42;
    private const int ClientNonceOffset = 48;
    private const int IssuedAtOffset = 64;
    private const int PaddingOffset = 72;
    private const int AuthenticatorOffset = 96;

    public static bool TryEncode(
        SecureUdpBindingType type,
        ReadOnlySpan<byte> connectionId,
        uint keyEpoch,
        ulong sequence,
        ReadOnlySpan<byte> clientNonce,
        long issuedAtUnixSeconds,
        ReadOnlySpan<byte> authenticator,
        Span<byte> destination,
        out int bytesWritten)
    {
        if (type == SecureUdpBindingType.AuthenticatedClientProof)
        {
            bytesWritten = 0;
            return false;
        }

        return TryEncodeCore(
            type,
            connectionId,
            keyEpoch,
            sequence,
            clientNonce,
            issuedAtUnixSeconds,
            ReadOnlySpan<byte>.Empty,
            authenticator,
            destination,
            out bytesWritten);
    }

    public static bool TryEncodeAuthenticatedClientProof(
        ReadOnlySpan<byte> connectionId,
        uint keyEpoch,
        ulong sequence,
        ReadOnlySpan<byte> clientNonce,
        long issuedAtUnixSeconds,
        ReadOnlySpan<byte> tlsProofAuthenticator,
        ReadOnlySpan<byte> cookieAuthenticator,
        Span<byte> destination,
        out int bytesWritten)
    {
        return TryEncodeCore(
            SecureUdpBindingType.AuthenticatedClientProof,
            connectionId,
            keyEpoch,
            sequence,
            clientNonce,
            issuedAtUnixSeconds,
            tlsProofAuthenticator,
            cookieAuthenticator,
            destination,
            out bytesWritten);
    }

    private static bool TryEncodeCore(
        SecureUdpBindingType type,
        ReadOnlySpan<byte> connectionId,
        uint keyEpoch,
        ulong sequence,
        ReadOnlySpan<byte> clientNonce,
        long issuedAtUnixSeconds,
        ReadOnlySpan<byte> tlsProofAuthenticator,
        ReadOnlySpan<byte> cookieAuthenticator,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (!IsKnownType(type) ||
            connectionId.Length !=
                SecureUdpBindingConstants.ConnectionIdBytes ||
            clientNonce.Length !=
                SecureUdpBindingConstants.ClientNonceBytes ||
            IsAllZero(connectionId) ||
            IsAllZero(clientNonce) ||
            destination.Length < SecureUdpBindingConstants.DatagramBytes ||
            sequence != 0)
        {
            return false;
        }

        var isHello = type == SecureUdpBindingType.ClientHello;
        if (isHello)
        {
            if (keyEpoch != 0 ||
                issuedAtUnixSeconds != 0 ||
                !tlsProofAuthenticator.IsEmpty ||
                !cookieAuthenticator.IsEmpty)
            {
                return false;
            }
        }
        else if (keyEpoch == 0 ||
            issuedAtUnixSeconds <= 0 ||
            cookieAuthenticator.Length !=
                SecureUdpBindingConstants.CookieTagBytes ||
            IsAllZero(cookieAuthenticator))
        {
            return false;
        }

        var isAuthenticatedProof =
            type == SecureUdpBindingType.AuthenticatedClientProof;
        if (isAuthenticatedProof)
        {
            if (tlsProofAuthenticator.Length !=
                    SecureUdpBindingConstants.TlsProofTagBytes ||
                IsAllZero(tlsProofAuthenticator))
            {
                return false;
            }
        }
        else if (!tlsProofAuthenticator.IsEmpty)
        {
            return false;
        }

        var output =
            destination[..SecureUdpBindingConstants.DatagramBytes];
        output.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(
            output,
            SecureUdpBindingConstants.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[4..],
            SecureUdpBindingConstants.HeaderBytes);
        output[6] = SecureUdpBindingConstants.ProtocolMajor;
        output[7] = SecureUdpBindingConstants.ProtocolMinor;
        output[8] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(
            output[10..],
            SecureUdpBindingConstants.DatagramBytes);
        connectionId.CopyTo(output[ConnectionIdOffset..]);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[KeyEpochOffset..],
            keyEpoch);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[SequenceOffset..],
            sequence);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[PayloadLengthOffset..],
            SecureUdpBindingConstants.PayloadBytes);
        clientNonce.CopyTo(output[ClientNonceOffset..]);
        BinaryPrimitives.WriteInt64BigEndian(
            output[IssuedAtOffset..],
            issuedAtUnixSeconds);
        if (isAuthenticatedProof)
        {
            tlsProofAuthenticator.CopyTo(output[PaddingOffset..]);
        }
        if (!isHello)
        {
            cookieAuthenticator.CopyTo(output[AuthenticatorOffset..]);
        }

        bytesWritten = output.Length;
        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> source,
        out SecureUdpBindingView binding)
    {
        binding = default;
        if (source.Length != SecureUdpBindingConstants.DatagramBytes ||
            BinaryPrimitives.ReadUInt32BigEndian(source) !=
                SecureUdpBindingConstants.Magic ||
            BinaryPrimitives.ReadUInt16BigEndian(source[4..]) !=
                SecureUdpBindingConstants.HeaderBytes ||
            source[6] != SecureUdpBindingConstants.ProtocolMajor ||
            source[7] != SecureUdpBindingConstants.ProtocolMinor ||
            source[9] != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(source[10..]) !=
                SecureUdpBindingConstants.DatagramBytes ||
            BinaryPrimitives.ReadUInt16BigEndian(
                source[PayloadLengthOffset..]) !=
                SecureUdpBindingConstants.PayloadBytes ||
            !IsAllZero(source.Slice(ReservedOffset, 6)))
        {
            return false;
        }

        var type = (SecureUdpBindingType)source[8];
        if (!IsKnownType(type))
        {
            return false;
        }

        var encodedTlsProof = source.Slice(
            PaddingOffset,
            SecureUdpBindingConstants.TlsProofTagBytes);
        var isAuthenticatedProof =
            type == SecureUdpBindingType.AuthenticatedClientProof;
        if (isAuthenticatedProof
                ? IsAllZero(encodedTlsProof)
                : !IsAllZero(encodedTlsProof))
        {
            return false;
        }

        var connectionId = source.Slice(
            ConnectionIdOffset,
            SecureUdpBindingConstants.ConnectionIdBytes);
        var nonce = source.Slice(
            ClientNonceOffset,
            SecureUdpBindingConstants.ClientNonceBytes);
        var keyEpoch = BinaryPrimitives.ReadUInt32BigEndian(
            source[KeyEpochOffset..]);
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(
            source[SequenceOffset..]);
        var issuedAt = BinaryPrimitives.ReadInt64BigEndian(
            source[IssuedAtOffset..]);
        var authenticator = source.Slice(
            AuthenticatorOffset,
            SecureUdpBindingConstants.CookieTagBytes);
        if (IsAllZero(connectionId) ||
            IsAllZero(nonce) ||
            sequence != 0)
        {
            return false;
        }

        if (type == SecureUdpBindingType.ClientHello)
        {
            if (keyEpoch != 0 ||
                issuedAt != 0 ||
                !IsAllZero(authenticator))
            {
                return false;
            }
        }
        else if (keyEpoch == 0 ||
            issuedAt <= 0 ||
            IsAllZero(authenticator))
        {
            return false;
        }

        binding = new SecureUdpBindingView(
            type,
            connectionId,
            keyEpoch,
            sequence,
            nonce,
            issuedAt,
            isAuthenticatedProof
                ? encodedTlsProof
                : ReadOnlySpan<byte>.Empty,
            authenticator);
        return true;
    }

    internal static bool IsAllZero(ReadOnlySpan<byte> value)
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

    private static bool IsKnownType(SecureUdpBindingType type) =>
        type is SecureUdpBindingType.ClientHello or
            SecureUdpBindingType.ServerChallenge or
            SecureUdpBindingType.ClientProof or
            SecureUdpBindingType.AuthenticatedClientProof;
}
