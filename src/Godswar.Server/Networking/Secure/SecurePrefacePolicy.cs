using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure;

internal static class SecurePrefacePolicy
{
    private static ReadOnlySpan<byte> ClientMagic => "GWSC"u8;

    public static SecurePrefaceOutcome Evaluate(
        ReadOnlySpan<byte> bytes,
        SecureEndpointRole expectedRole,
        IReadOnlySet<string> allowedOriginSha256,
        out SecureClientPreface? preface)
    {
        preface = null;
        ArgumentNullException.ThrowIfNull(allowedOriginSha256);
        if (bytes.Length != SecureProtocolConstants.ClientPrefaceBytes ||
            !SecureProtocolValidation.IsEndpointRole(expectedRole) ||
            !bytes[..4].SequenceEqual(ClientMagic) ||
            BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]) !=
                SecureProtocolConstants.ClientPrefaceBytes ||
            bytes[13] != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(bytes[14..]) != 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) != 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]) !=
                SecureProtocolConstants.MaximumPayloadBytes ||
            SecureProtocolValidation.IsAllZero(bytes[24..40]))
        {
            return SecurePrefaceOutcome.Malformed;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]) !=
                SecureProtocolConstants.ProtocolMajor ||
            BinaryPrimitives.ReadUInt16BigEndian(bytes[8..]) !=
                SecureProtocolConstants.ProtocolMinor ||
            BinaryPrimitives.ReadUInt16BigEndian(bytes[10..]) !=
                SecureProtocolConstants.ProtocolMinor)
        {
            return SecurePrefaceOutcome.UnsupportedVersion;
        }

        if (bytes[12] != (byte)expectedRole)
        {
            return SecurePrefaceOutcome.WrongEndpoint;
        }

        if (!allowedOriginSha256.Contains(
                Convert.ToHexString(bytes[40..72])))
        {
            return SecurePrefaceOutcome.UnsupportedBuild;
        }

        if (!SecurePrefaceCodec.TryDecodeClient(
                bytes,
                expectedRole,
                out preface))
        {
            return SecurePrefaceOutcome.PolicyRejected;
        }

        return SecurePrefaceOutcome.Accepted;
    }

    public static SecureServerPrefaceStatus ToServerStatus(
        SecurePrefaceOutcome outcome)
    {
        return outcome switch
        {
            SecurePrefaceOutcome.Accepted => SecureServerPrefaceStatus.Ok,
            SecurePrefaceOutcome.UnsupportedVersion =>
                SecureServerPrefaceStatus.UnsupportedVersion,
            SecurePrefaceOutcome.WrongEndpoint =>
                SecureServerPrefaceStatus.WrongEndpoint,
            SecurePrefaceOutcome.UnsupportedBuild =>
                SecureServerPrefaceStatus.UnsupportedBuild,
            SecurePrefaceOutcome.Malformed or
            SecurePrefaceOutcome.PolicyRejected =>
                SecureServerPrefaceStatus.PolicyRejected,
            SecurePrefaceOutcome.DeadlineExceeded =>
                SecureServerPrefaceStatus.PolicyRejected,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }
}
