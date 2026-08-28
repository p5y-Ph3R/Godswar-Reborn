using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const ushort SystemAddItemOpcode = 0x27C9;
    private const int SystemAddItemHeaderLength = 8;

    public static byte[] SystemAddItemWithAcquisitionLog(
        CompactItemEntry item)
    {
        if (item.IsEmpty)
        {
            throw new ArgumentException(
                "An item acquisition cannot contain an empty item.",
                nameof(item));
        }
        if (item.Stack is <= 0 or > sbyte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(item),
                "The stock acquisition log reads quantity as a signed byte.");
        }

        var packet = new byte[
            SystemAddItemHeaderLength + EnterItemRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, sizeof(ushort)),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)),
            SystemAddItemOpcode);

        // Physical +4 is an unused DWORD in the native receive branch. The
        // stock client hydrates the 72-byte item at +8, applies it to its local
        // bag, and emits YouObtainItem in the left game log. Callers must evict
        // its transient target slot, then send an authoritative bag refresh.
        WriteKitBagItemRecord(
            packet.AsSpan(
                SystemAddItemHeaderLength,
                EnterItemRecordLength),
            item);
        return packet;
    }
}
