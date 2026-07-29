using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure;

internal static class SecureLegacyCommandOperationCodec
{
    public static bool TryEncode(
        SecureLegacyCommandOperation operation,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (operation.OperationId == Guid.Empty ||
            operation.PacketLength is < 4 or >
                LegacyProtocolLimits.MaxPacketLength ||
            destination.Length <
                SecureProtocolConstants.LegacyCommandOperationBytes)
        {
            return false;
        }

        var output = destination[
            ..SecureProtocolConstants.LegacyCommandOperationBytes];
        output.Clear();
        output[0] =
            SecureProtocolConstants.LegacyCommandOperationVersion;
        BinaryPrimitives.WriteUInt16BigEndian(
            output[2..],
            operation.PacketLength);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[4..],
            operation.Opcode);
        if (!operation.OperationId.TryWriteBytes(
                output[8..],
                bigEndian: true,
                out var guidBytesWritten) ||
            guidBytesWritten != 16)
        {
            output.Clear();
            return false;
        }

        bytesWritten = output.Length;
        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> source,
        out SecureLegacyCommandOperation operation)
    {
        operation = default;
        if (source.Length !=
                SecureProtocolConstants.LegacyCommandOperationBytes ||
            source[0] !=
                SecureProtocolConstants.LegacyCommandOperationVersion ||
            source[1] != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(source[6..]) != 0)
        {
            return false;
        }

        var packetLength =
            BinaryPrimitives.ReadUInt16BigEndian(source[2..]);
        if (packetLength is < 4 or >
            LegacyProtocolLimits.MaxPacketLength)
        {
            return false;
        }

        var operationId = new Guid(source[8..24], bigEndian: true);
        if (operationId == Guid.Empty)
        {
            return false;
        }

        operation = new SecureLegacyCommandOperation(
            operationId,
            packetLength,
            BinaryPrimitives.ReadUInt16BigEndian(source[4..]));
        return true;
    }
}
