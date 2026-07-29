using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure;

internal static class SecureLegacyCommandResultCodec
{
    public static bool TryEncode(
        in SecureLegacyCommandResult result,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (!IsValid(result) ||
            destination.Length <
                SecureProtocolConstants.LegacyCommandResultBytes)
        {
            return false;
        }

        var output =
            destination[..SecureProtocolConstants.LegacyCommandResultBytes];
        output.Clear();
        output[0] = SecureProtocolConstants.LegacyCommandResultVersion;
        output[1] = (byte)result.Disposition;
        BinaryPrimitives.WriteUInt16BigEndian(
            output[2..],
            result.CommandFamily);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[4..],
            result.ResultCode);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[8..],
            result.InventoryRevision);
        if (!result.OperationId.TryWriteBytes(
                output[16..],
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
        out SecureLegacyCommandResult result)
    {
        result = default;
        if (source.Length !=
                SecureProtocolConstants.LegacyCommandResultBytes ||
            source[0] !=
                SecureProtocolConstants.LegacyCommandResultVersion)
        {
            return false;
        }

        var disposition =
            (SecureLegacyCommandDisposition)source[1];
        var commandFamily =
            BinaryPrimitives.ReadUInt16BigEndian(source[2..]);
        var inventoryRevision =
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]);
        var operationId = new Guid(source[16..32], bigEndian: true);
        if (!SecureProtocolValidation.IsLegacyCommandDisposition(
                disposition) ||
            commandFamily == 0 ||
            operationId == Guid.Empty ||
            disposition == SecureLegacyCommandDisposition.Applied &&
                inventoryRevision == 0)
        {
            return false;
        }

        result = new SecureLegacyCommandResult(
            disposition,
            commandFamily,
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            inventoryRevision,
            operationId);
        return true;
    }

    private static bool IsValid(in SecureLegacyCommandResult result)
    {
        return SecureProtocolValidation.IsLegacyCommandDisposition(
                result.Disposition) &&
            result.CommandFamily != 0 &&
            result.OperationId != Guid.Empty &&
            (result.Disposition !=
                SecureLegacyCommandDisposition.Applied ||
                result.InventoryRevision != 0);
    }
}
