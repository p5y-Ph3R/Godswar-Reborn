using System.Buffers.Binary;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int WarehouseDialogOpenPacketBytes = 48;
    private const int WarehouseDialogMode = 0x20;
    private const int WarehouseSnapshotHeaderBytes = 24;
    private const int WarehouseSnapshotSlotsPerChunk = 12;
    private const int WarehouseSnapshotSelectorStride = 2;
    private const int WarehousePageProjectionBoxCountShift = 4;
    private const int WarehouseNativeBoxCount = 4;
    internal const uint WarehousePageProjectionUserMarker = 0x57485000;

    public static byte[] WarehouseDialogOpenAck(
        uint npcInteractionId,
        string clientScriptKey)
    {
        if (npcInteractionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(npcInteractionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(clientScriptKey);

        var packet = new byte[WarehouseDialogOpenPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            WarehouseDialogOpenPacketBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.NpcDialogOpen);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            npcInteractionId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8, 4),
            WarehouseDialogMode);
        PacketText.WriteFixedAscii(
            packet.AsSpan(16, 32),
            clientScriptKey);
        return packet;
    }

    /// <summary>
    /// Builds every capacity-bounded MSG_STORAGE chunk, including empty-mask
    /// chunks. The current native handler ignores the user field at +8; the
    /// canonical value remains the local-player object alias used elsewhere.
    /// </summary>
    public static byte[][] WarehouseSnapshotPackets(
        WarehouseSnapshot snapshot) =>
        WarehouseSnapshotPackets(snapshot, LocalPlayerObjectId);

    public static byte[][] WarehousePageSnapshotPackets(
        WarehouseSnapshot snapshot,
        int page)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        var unlockedBoxCount = WarehouseCapacityPolicy.BoxNumber(
            snapshot.Capacity);
        if (page < 0 || page >= unlockedBoxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        var firstSlot = checked(page * WarehouseCapacityPolicy.SlotsPerBox);
        var items = snapshot.Items
            .Where(item => item.Slot >= firstSlot &&
                item.Slot < firstSlot + WarehouseCapacityPolicy.SlotsPerBox)
            .Select(item => new WarehouseItemSnapshot(
                item.Slot - firstSlot,
                item.CompactItemState))
            .ToArray();
        var packets = WarehouseSnapshotPackets(
            new WarehouseSnapshot(
                snapshot.AccountId,
                snapshot.CharacterId,
                WarehouseCapacityPolicy.SlotsPerBox,
                snapshot.WarehouseRevision,
                snapshot.InventoryRevision,
                items),
            LocalPlayerObjectId);
        var pageMarker = WarehousePageProjectionUserMarker |
            checked((uint)unlockedBoxCount <<
                WarehousePageProjectionBoxCountShift) |
            checked((uint)page);
        var nativeCapacity = checked((ushort)(Math.Min(
            unlockedBoxCount,
            WarehouseNativeBoxCount) * WarehouseCapacityPolicy.SlotsPerBox));
        foreach (var packet in packets)
        {
            // The audited native handler ignores this header user field.
            // The network proxy uses it to correlate a projected page while
            // item records retain the normal local-player owner alias.
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(8, sizeof(uint)),
                pageMarker);
            // Stock Origin enables only its original four tabs from this
            // header. Keep that truthful native capacity while the payload
            // remains a 40-cell projection of the selected logical box.
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(12, sizeof(ushort)),
                nativeCapacity);
        }
        return packets;
    }

    internal static byte[][] WarehouseSnapshotPackets(
        WarehouseSnapshot snapshot,
        uint userObjectId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        if (userObjectId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userObjectId));
        }

        var items = new Dictionary<int, CompactItemEntry>(
            snapshot.Items.Count);
        foreach (var source in snapshot.Items)
        {
            var item = CompactItemEntry.Parse(source.CompactItemState);
            if (item.IsEmpty)
            {
                throw new InvalidDataException(
                    "A warehouse snapshot item cannot serialize as empty.");
            }

            items.Add(source.Slot, item);
        }

        var chunkCount =
            (snapshot.Capacity + WarehouseSnapshotSlotsPerChunk - 1) /
            WarehouseSnapshotSlotsPerChunk;
        var packets = new byte[chunkCount][];
        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var firstSlot = chunk * WarehouseSnapshotSlotsPerChunk;
            ushort mask = 0;
            var records = new List<CompactItemEntry>(
                WarehouseSnapshotSlotsPerChunk);
            for (var bit = 0;
                 bit < WarehouseSnapshotSlotsPerChunk &&
                 firstSlot + bit < snapshot.Capacity;
                 bit++)
            {
                if (!items.TryGetValue(firstSlot + bit, out var item))
                {
                    continue;
                }

                mask |= checked((ushort)(1 << bit));
                records.Add(item);
            }

            var packet = new byte[
                WarehouseSnapshotHeaderBytes +
                (records.Count * EnterItemRecordLength)];
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(0, 2),
                checked((ushort)packet.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(2, 2),
                Opcodes.WarehouseSnapshot);
            // Stored-money transfer is deliberately unsupported.
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(8, 4),
                userObjectId);
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(12, 2),
                checked((ushort)snapshot.Capacity));
            packet[14] = checked((byte)(
                chunk * WarehouseSnapshotSelectorStride));
            packet[15] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(16, 2),
                mask);
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(18, 2),
                (ushort)WarehouseStorageType.Normal);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(20, 4),
                0);

            for (var record = 0; record < records.Count; record++)
            {
                WriteKitBagItemRecord(
                    packet.AsSpan(
                        WarehouseSnapshotHeaderBytes +
                        (record * EnterItemRecordLength),
                        EnterItemRecordLength),
                    records[record],
                    userObjectId);
            }

            packets[chunk] = packet;
        }

        return packets;
    }

    public static byte[] WarehouseTransferAcknowledgement(
        in WarehouseTransferIntent intent)
    {
        if (!WarehouseWireProtocol.IsValidIntent(intent))
        {
            throw new ArgumentException(
                "The warehouse transfer intent is invalid.",
                nameof(intent));
        }

        var packet = new byte[WarehouseWireProtocol.TransferPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            WarehouseWireProtocol.TransferPacketBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.WarehouseTransfer);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(4, 2),
            checked((short)intent.WarehouseSlot));

        switch (intent.Operation)
        {
            case WarehouseTransferOperation.Deposit:
                WriteKitBagTarget(packet, intent.KitBagSlot);
                packet[16] = 1;
                break;

            case WarehouseTransferOperation.Withdraw
                when intent.KitBagSlot ==
                    WarehouseCapacityPolicy.AutomaticKitBagSlot:
                BinaryPrimitives.WriteInt16LittleEndian(
                    packet.AsSpan(6, 2),
                    -1);
                BinaryPrimitives.WriteInt16LittleEndian(
                    packet.AsSpan(8, 2),
                    -1);
                break;

            case WarehouseTransferOperation.Withdraw:
                WriteKitBagTarget(packet, intent.KitBagSlot);
                break;

            case WarehouseTransferOperation.InternalMove:
                BinaryPrimitives.WriteInt16LittleEndian(
                    packet.AsSpan(6, 2),
                    checked((short)intent.DestinationWarehouseSlot));
                BinaryPrimitives.WriteInt16LittleEndian(
                    packet.AsSpan(8, 2),
                    -1);
                break;
        }

        // +10 and +17 are stock scratch; canonical server acknowledgements
        // zero them. +12 stays zero because money movement is unsupported.
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(18, 2),
            (ushort)WarehouseStorageType.Normal);
        return packet;
    }

    private static void WriteKitBagTarget(byte[] packet, int kitBagSlot)
    {
        var page = Math.DivRem(
            kitBagSlot,
            WarehouseWireProtocol.KitBagSlotsPerPage,
            out var cell);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(6, 2),
            checked((short)page));
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(8, 2),
            checked((short)cell));
    }
}
