using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] StorageItemEquipmentBagTransfer(int equipmentSlot, int bagSlot)
    {
        var packet = new byte[42];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2744);

        // Native opcode 10052 always places the equipment descriptor first and
        // the bag descriptor second, regardless of transfer direction.
        var bagPage = Math.DivRem(bagSlot, 24, out var bagIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8, 2), (ushort)equipmentSlot);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), (ushort)bagPage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)bagIndex);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), -1);
        return packet;
    }

    public static byte[] StorageItemKitBagMove(int sourceSlot, int destinationSlot)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2744);

        var sourcePage = Math.DivRem(sourceSlot, 24, out var sourceIndex);
        var destinationPage = Math.DivRem(destinationSlot, 24, out var destinationIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8, 2), (ushort)sourcePage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), (ushort)sourceIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), (ushort)destinationPage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)destinationIndex);
        return packet;
    }

    public static byte[] StorageItemKitBagDelete(int sourceSlot)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2744);

        var sourcePage = Math.DivRem(sourceSlot, 24, out var sourceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8, 2), (ushort)sourcePage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), (ushort)sourceIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), ushort.MaxValue);
        return packet;
    }

    public static byte[] BagItemActionAck(ReadOnlySpan<byte> requestPacket)
    {
        const int packetLength = 40;
        var packet = new byte[packetLength];

        if (requestPacket.Length >= packetLength)
        {
            requestPacket[..packetLength].CopyTo(packet);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), packetLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2748);
        return packet;
    }

    public static byte[] StorageMarker(ushort markerOpcode)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2727);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4, 2), markerOpcode);
        return packet;
    }

    public static byte[][] KitBagDetailPages(GameCharacter character)
    {
        var kitBag = KitBagItems(character);
        var packets = new List<byte[]>(KitBagPageCount * 2);

        for (var page = 0; page < KitBagPageCount; page++)
        {
            for (var half = 0; half < 2; half++)
            {
                var packet = new byte[KitBagDetailPacketLength];
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), KitBagDetailPacketLength);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), KitBagDetailOpcode);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 4);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), 0);
                packet[16] = (byte)page;
                packet[17] = (byte)(half * KitBagDetailRecordsPerPacket);
                packet[18] = 0x58;
                packet[19] = 0x00;
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), 0);

                var firstSlot = (page * KitBagSlotsPerPage) + (half * KitBagDetailRecordsPerPacket);
                for (var record = 0; record < KitBagDetailRecordsPerPacket; record++)
                {
                    var slot = firstSlot + record;
                    var item = slot < kitBag.Length ? kitBag[slot] : default;
                    WriteKitBagItemRecord(packet.AsSpan(KitBagDetailHeaderLength + (record * EnterItemRecordLength), EnterItemRecordLength), item);
                }

                packets.Add(packet);
            }
        }

        return packets.ToArray();
    }

    public static byte[][] KitBagSlotIndexes(GameCharacter character)
    {
        var kitBag = KitBagItems(character);
        var packets = new List<byte[]>(KitBagPageCount * KitBagSlotsPerPage);

        for (var page = 0; page < KitBagPageCount; page++)
        {
            for (var index = 0; index < KitBagSlotsPerPage; index++)
            {
                var slot = (page * KitBagSlotsPerPage) + index;
                var item = slot < kitBag.Length ? kitBag[slot] : default;
                packets.Add(KitBagSlotIndex(slot, item));
            }
        }

        return packets.ToArray();
    }

    public static byte[][] KitBagDeletionAcknowledgements(GameCharacter character)
    {
        var kitBag = KitBagItems(character);
        var packets = new List<byte[]>();
        for (var slot = 0; slot < KitBagPageCount * KitBagSlotsPerPage; slot++)
        {
            if (slot < kitBag.Length && !kitBag[slot].IsEmpty)
            {
                packets.Add(StorageItemKitBagDelete(slot));
            }
        }

        return packets.ToArray();
    }

    public static byte[][] KitBagMutationDeletionAcknowledgements(
        string previousKitBag,
        string updatedKitBag)
    {
        var packets = new List<byte[]>();
        for (var slot = 0; slot < KitBagPageCount * KitBagSlotsPerPage; slot++)
        {
            var previous = KitBagSlots.GetItem(previousKitBag, slot);
            if (previous.IsEmpty || previous == KitBagSlots.GetItem(updatedKitBag, slot))
            {
                continue;
            }

            // Detail/index snapshots do not evict an item object already
            // instantiated by this client. Clear every changed occupied slot
            // with the native source-to-FFFF acknowledgement before hydrating
            // its authoritative replacement (or empty state).
            packets.Add(StorageItemKitBagDelete(slot));
        }

        return packets.ToArray();
    }

    public static byte[] KitBagSlotIndex(GameCharacter character, int slot)
    {
        if (slot is < 0 or >= KitBagPageCount * KitBagSlotsPerPage)
        {
            return [];
        }

        var kitBag = KitBagItems(character);
        var item = slot < kitBag.Length ? kitBag[slot] : default;
        return KitBagSlotIndex(slot, item);
    }

    private static byte[] KitBagSlotIndex(int slot, CompactItemEntry item)
    {
        const int packetLength = 40;
        var packet = new byte[packetLength];
        var page = Math.DivRem(slot, KitBagSlotsPerPage, out var index);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), packetLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), BagItemActionOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), -1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)page);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16, 4), (uint)index);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20, 4), item.IsEmpty ? -1 : unchecked((int)item.Id));
        return packet;
    }
}
