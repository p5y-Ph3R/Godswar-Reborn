using System.Buffers.Binary;
using System.Text;

namespace Godswar.Server.Networking.Secure;

// Decoded grants are syntactic values, not authorized routes. Signed-manifest,
// redirect, ticket-scope, and channel-phase policy must validate them first.
internal static class SecureGameControlCodec
{
    public static bool TryEncodeGrant(
        SecureGameGrant? grant,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (grant is null)
        {
            return false;
        }

        var routeLength = grant.RouteHost.Length;
        var tlsLength = grant.TlsHost.Length;
        var audienceLength = grant.Audience.Length;
        var requiredBytes =
            SecureProtocolConstants.GameGrantFixedBytes +
            routeLength +
            tlsLength +
            audienceLength;
        if (requiredBytes is <
                SecureProtocolConstants.MinimumGameGrantBytes or >
                SecureProtocolConstants.MaximumGameGrantBytes ||
            destination.Length < requiredBytes)
        {
            return false;
        }

        var output = destination[..requiredBytes];
        output.Clear();
        output[0] = 1;
        output[1] = (byte)routeLength;
        output[2] = (byte)tlsLength;
        output[3] = (byte)audienceLength;
        BinaryPrimitives.WriteUInt16BigEndian(output[4..], grant.RoutePort);
        BinaryPrimitives.WriteUInt16BigEndian(output[6..], grant.TlsPort);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[8..],
            grant.TargetServerId);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[12..],
            grant.ExpiryUnixMilliseconds);
        if (!grant.TryCopySecrets(output[20..36], output[36..68]))
        {
            output.Clear();
            return false;
        }

        var textOffset = SecureProtocolConstants.GameGrantFixedBytes;
        SecureProtocolValidation.WriteAscii(
            grant.RouteHost,
            output.Slice(textOffset, routeLength));
        textOffset += routeLength;
        SecureProtocolValidation.WriteAscii(
            grant.TlsHost,
            output.Slice(textOffset, tlsLength));
        textOffset += tlsLength;
        SecureProtocolValidation.WriteAscii(
            grant.Audience,
            output.Slice(textOffset, audienceLength));
        bytesWritten = requiredBytes;
        return true;
    }

    public static bool TryDecodeGrant(
        ReadOnlySpan<byte> source,
        out SecureGameGrant? grant)
    {
        grant = null;
        if (source.Length is <
                SecureProtocolConstants.MinimumGameGrantBytes or >
                SecureProtocolConstants.MaximumGameGrantBytes ||
            source[0] != 1)
        {
            return false;
        }

        var routeLength = source[1];
        var tlsLength = source[2];
        var audienceLength = source[3];
        var expectedLength =
            SecureProtocolConstants.GameGrantFixedBytes +
            routeLength +
            tlsLength +
            audienceLength;
        if (routeLength is < 1 or > 23 ||
            tlsLength is < 1 or > 253 ||
            audienceLength is < 1 or > 64 ||
            source.Length != expectedLength)
        {
            return false;
        }

        var routePort = BinaryPrimitives.ReadUInt16BigEndian(source[4..]);
        var tlsPort = BinaryPrimitives.ReadUInt16BigEndian(source[6..]);
        var targetServerId = BinaryPrimitives.ReadUInt32BigEndian(source[8..]);
        if (routePort == 0 ||
            tlsPort == 0 ||
            targetServerId == 0 ||
            SecureProtocolValidation.IsAllZero(source[20..36]) ||
            SecureProtocolValidation.IsAllZero(source[36..68]))
        {
            return false;
        }

        var textOffset = SecureProtocolConstants.GameGrantFixedBytes;
        var routeBytes = source.Slice(textOffset, routeLength);
        textOffset += routeLength;
        var tlsBytes = source.Slice(textOffset, tlsLength);
        textOffset += tlsLength;
        var audienceBytes = source.Slice(textOffset, audienceLength);
        if (!SecureProtocolValidation.IsDnsName(routeBytes, 23) ||
            !SecureProtocolValidation.IsDnsName(tlsBytes, 253) ||
            !SecureProtocolValidation.IsAudience(audienceBytes))
        {
            return false;
        }

        grant = new SecureGameGrant(
            Encoding.ASCII.GetString(routeBytes),
            Encoding.ASCII.GetString(tlsBytes),
            Encoding.ASCII.GetString(audienceBytes),
            routePort,
            tlsPort,
            targetServerId,
            BinaryPrimitives.ReadUInt64BigEndian(source[12..]),
            source[20..36],
            source[36..68]);
        return true;
    }

    public static bool TryEncodeBind(
        SecureGameBind? bind,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (bind is null ||
            destination.Length < SecureProtocolConstants.GameBindBytes)
        {
            return false;
        }

        var output = destination[..SecureProtocolConstants.GameBindBytes];
        output.Clear();
        output[0] = 1;
        if (!bind.TryCopySecrets(output[4..20], output[20..52]))
        {
            output.Clear();
            return false;
        }
        bytesWritten = output.Length;
        return true;
    }

    public static bool TryDecodeBind(
        ReadOnlySpan<byte> source,
        out SecureGameBind? bind)
    {
        bind = null;
        if (source.Length != SecureProtocolConstants.GameBindBytes ||
            source[0] != 1 ||
            source[1] != 0 ||
            source[2] != 0 ||
            source[3] != 0 ||
            SecureProtocolValidation.IsAllZero(source[4..20]) ||
            SecureProtocolValidation.IsAllZero(source[20..52]))
        {
            return false;
        }

        bind = new SecureGameBind(source[4..20], source[20..52]);
        return true;
    }

    public static bool TryEncodeBindResult(
        SecureBindResult result,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (!SecureProtocolValidation.IsBindStatus(result.Status) ||
            destination.Length < SecureProtocolConstants.BindResultBytes)
        {
            return false;
        }

        var output = destination[..SecureProtocolConstants.BindResultBytes];
        output.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(
            output,
            (ushort)result.Status);
        bytesWritten = output.Length;
        return true;
    }

    public static bool TryDecodeBindResult(
        ReadOnlySpan<byte> source,
        out SecureBindResult result)
    {
        result = default;
        if (source.Length != SecureProtocolConstants.BindResultBytes ||
            source[2] != 0 ||
            source[3] != 0)
        {
            return false;
        }

        var status = (SecureBindStatus)BinaryPrimitives.ReadUInt16BigEndian(
            source);
        if (!SecureProtocolValidation.IsBindStatus(status))
        {
            return false;
        }

        result = new SecureBindResult(status);
        return true;
    }
}
