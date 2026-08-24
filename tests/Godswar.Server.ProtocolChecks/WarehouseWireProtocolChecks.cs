using System.Buffers.Binary;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class WarehouseWireProtocolChecks
{
    public const string CheckName =
        "Stock warehouse open, snapshot, and transfer wire protocol";

    public static Task RunAsync()
    {
        CheckOpcodes();
        CheckTransferParsing();
        CheckMalformedTransfersFailClosed();
        CheckDialogOpenVector();
        CheckSnapshotChunks();
        CheckTransferAcknowledgements();
        return Task.CompletedTask;
    }

    private static void CheckOpcodes()
    {
        Check.Equal((ushort)10034, Opcodes.WarehouseSnapshot,
            "MSG_STORAGE uses the installed dispatch-table opcode");
        Check.Equal((ushort)10059, Opcodes.WarehouseTransfer,
            "MSG_STORAGE_ITEM uses its bidirectional opcode");
        Check.Equal("WarehouseSnapshot", Opcodes.Name(10034),
            "snapshot opcode has an unambiguous diagnostic name");
        Check.Equal("WarehouseTransfer", Opcodes.Name(10059),
            "transfer opcode has an unambiguous diagnostic name");
        Check.Equal("Storage", Opcodes.Name(10023),
            "legacy 10023 world marker remains distinct from storage data");
    }

    private static void CheckTransferParsing()
    {
        var deposit = TransferFrame(
            warehouseSlot: -1,
            firstTarget: 3,
            secondTarget: 23,
            direction: 1,
            storageType: 0xBEEF,
            scratch16: 0xA5,
            scratch10: 0x4567);
        Check.True(
            WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(deposit),
                out var depositIntent) &&
            depositIntent == new WarehouseTransferIntent(
                WarehouseTransferOperation.Deposit,
                -1,
                95,
                -1,
                0,
                WarehouseStorageType.Normal),
            "deposit normalizes page/cell and ignores native scratch fields");

        var withdraw = TransferFrame(39, 2, 7, direction: 0);
        Check.True(
            WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(withdraw),
                out var withdrawIntent) &&
            withdrawIntent == new WarehouseTransferIntent(
                WarehouseTransferOperation.Withdraw,
                39,
                55,
                -1,
                0,
                WarehouseStorageType.Normal),
            "explicit withdraw normalizes its destination bag slot");

        var automaticWithdraw = TransferFrame(359, -1, -1, direction: 0);
        Check.True(
            WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(automaticWithdraw),
                out var automaticIntent) &&
            automaticIntent.Operation == WarehouseTransferOperation.Withdraw &&
            automaticIntent.WarehouseSlot == 359 &&
            automaticIntent.KitBagSlot == -1,
            "automatic withdraw preserves both -1 sentinels");

        var internalMove = TransferFrame(
            4,
            97,
            -1,
            direction: 0,
            storageType: 1);
        Check.True(
            WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(internalMove),
                out var internalIntent) &&
            internalIntent == new WarehouseTransferIntent(
                WarehouseTransferOperation.InternalMove,
                4,
                -1,
                97,
                0,
                WarehouseStorageType.Normal),
            "stock warehouse-internal move normalizes its native tail");
    }

    private static void CheckMalformedTransfersFailClosed()
    {
        var wrongOpcode = TransferFrame(0, 0, 0, direction: 1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongOpcode.AsSpan(2, 2),
            Opcodes.StorageItem);
        var wrongDeclaredLength = TransferFrame(0, 0, 0, direction: 1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongDeclaredLength.AsSpan(0, 2),
            19);
        var shortBuffer = TransferFrame(0, 0, 0, direction: 1)[..19];
        var money = TransferFrame(0, 0, 0, direction: 1, money: 1);
        var awardWithdraw = TransferFrame(
            0,
            0,
            0,
            direction: 0,
            storageType: 1);
        var invalidDirection = TransferFrame(0, 0, 0, direction: 2);
        var sameSlot = TransferFrame(7, 7, -1, direction: 0);
        var unknownInternalTail = TransferFrame(
            7,
            8,
            -1,
            direction: 0,
            storageType: 2);
        var invalidBag = TransferFrame(0, 4, 0, direction: 1);

        Check.True(
            !WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(wrongOpcode), out _) &&
            !WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(wrongDeclaredLength), out _) &&
            !WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(shortBuffer), out _) &&
            !WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(money), out _) &&
            !WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(awardWithdraw), out _) &&
            !WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(invalidDirection), out _) &&
            !WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(sameSlot), out _) &&
            !WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(unknownInternalTail), out _) &&
            !WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(invalidBag), out _),
            "noncanonical, money, award, and invalid-slot frames fail closed");
    }

    private static void CheckDialogOpenVector()
    {
        var open = PacketBuilder.WarehouseDialogOpenAck(
            5164,
            "Athens_025");
        Check.Equal(
            "300053272C1400002000000000000000" +
            "417468656E735F30323500000000000000000000000000000000000000000000",
            Convert.ToHexString(open),
            "Athens warehouse advertisement matches the captured mode-32 vector");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.WarehouseDialogOpenAck(0, "Athens_025"),
            "warehouse open rejects the null NPC object identity");
        Check.Throws<ArgumentException>(
            () => PacketBuilder.WarehouseDialogOpenAck(5164, ""),
            "warehouse open rejects an empty client script key");
    }

    private static void CheckSnapshotChunks()
    {
        var empty = Snapshot(40, []);
        var emptyPackets = PacketBuilder.WarehouseSnapshotPackets(empty);
        Check.Equal(4, emptyPackets.Length,
            "one 40-cell box emits four capacity-bounded chunks");
        Check.Equal(
            "180032270000000048140000280000000000000000000000",
            Convert.ToHexString(emptyPackets[0]),
            "empty first chunk uses a 24-byte header, local alias, and zero mask");
        Check.True(
            emptyPackets.Select(static packet => packet.Length)
                .SequenceEqual([24, 24, 24, 24]) &&
            emptyPackets.Select(static packet => packet[14])
                .SequenceEqual(new byte[] { 0, 2, 4, 6 }),
            "empty chunks are not omitted and selectors advance by two");

        var populated = Snapshot(
            40,
            [
                Item(0, 4102, 7),
                Item(11, 2001, 2),
                Item(12, 3001, 3),
                Item(39, 4001, 4)
            ]);
        var packets = PacketBuilder.WarehouseSnapshotPackets(populated);
        Check.True(
            packets.Select(static packet => packet.Length)
                .SequenceEqual([168, 96, 24, 96]),
            "snapshot length is header plus only occupied dense records");
        Check.Equal((ushort)0x0801, ReadUInt16(packets[0], 16),
            "first chunk mask marks slots 0 and 11");
        Check.Equal((ushort)0x0001, ReadUInt16(packets[1], 16),
            "second chunk mask restarts at slot 12");
        Check.Equal((ushort)0x0000, ReadUInt16(packets[2], 16),
            "empty middle chunk retains a zero mask");
        Check.Equal((ushort)0x0008, ReadUInt16(packets[3], 16),
            "final chunk mask marks warehouse slot 39");
        Check.Equal(4102u, ReadUInt32(packets[0], 24),
            "dense records begin with the lowest occupied slot");
        Check.Equal(2001u, ReadUInt32(packets[0], 96),
            "dense records remain in ascending mask-bit order");
        Check.Equal((byte)7, packets[0][24 + 27],
            "warehouse item record carries the authoritative stack byte");
        Check.Equal(0x1448u, ReadUInt32(packets[0], 24 + 68),
            "warehouse item record uses the canonical local object alias");

        var maximumSnapshot = Snapshot(
            360,
            [Item(320, 5001, 5), Item(359, 5002, 6)]);
        var maximum = PacketBuilder.WarehouseSnapshotPackets(
            maximumSnapshot);
        Check.Equal(30, maximum.Length,
            "360 cells require thirty 12-slot chunks");
        Check.Equal((byte)58, maximum[^1][14],
            "maximum-capacity final selector is 58");
        Check.Equal((ushort)360, ReadUInt16(maximum[^1], 12),
            "every chunk repeats the active cell capacity");

        var pageNine = PacketBuilder.WarehousePageSnapshotPackets(
            maximumSnapshot,
            page: 8);
        Check.True(
            pageNine.Length == 4 &&
            pageNine.All(packet =>
                ReadUInt32(packet, 8) ==
                    PacketBuilder.WarehousePageProjectionUserMarker +
                        0x98 &&
                ReadUInt16(packet, 12) == 160) &&
            ReadUInt16(pageNine[0], 16) == 1 &&
            ReadUInt16(pageNine[^1], 16) == 8 &&
            ReadUInt32(pageNine[0], 24) == 5001 &&
            ReadUInt32(pageNine[^1], 24) == 5002,
            "page nine projects logical slots 320..359 with native capacity");
        var projectedNativeCapacities = new[] { 40, 80, 120, 160, 200, 360 }
            .Select(capacity => ReadUInt16(
                PacketBuilder.WarehousePageSnapshotPackets(
                    Snapshot(capacity, []),
                    page: 0)[0],
                12));
        Check.True(
            projectedNativeCapacities.SequenceEqual(
                new ushort[] { 40, 80, 120, 160, 160, 160 }),
            "projected headers enable truthful stock tabs and cap at four");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.WarehousePageSnapshotPackets(
                Snapshot(40, []),
                page: 1),
            "a projected page cannot exceed authoritative capacity");

        var explicitAlias = PacketBuilder.WarehouseSnapshotPackets(
            empty,
            20);
        Check.Equal(20u, ReadUInt32(explicitAlias[0], 8),
            "vector overload writes the supplied ignored user field exactly");
    }

    private static void CheckTransferAcknowledgements()
    {
        CheckAcknowledgement(
            new(
                WarehouseTransferOperation.Deposit,
                -1,
                95,
                -1,
                0,
                WarehouseStorageType.Normal),
            "14004B27FFFF0300170000000000000001000000",
            "automatic deposit ACK keeps the -1 warehouse destination");
        CheckAcknowledgement(
            new(
                WarehouseTransferOperation.Withdraw,
                12,
                55,
                -1,
                0,
                WarehouseStorageType.Normal),
            "14004B270C000200070000000000000000000000",
            "explicit withdraw ACK carries bag page and cell");
        CheckAcknowledgement(
            new(
                WarehouseTransferOperation.Withdraw,
                12,
                -1,
                -1,
                0,
                WarehouseStorageType.Normal),
            "14004B270C00FFFFFFFF00000000000000000000",
            "automatic withdraw ACK keeps both -1 target fields");
        CheckAcknowledgement(
            new(
                WarehouseTransferOperation.InternalMove,
                12,
                -1,
                35,
                0,
                WarehouseStorageType.Normal),
            "14004B270C002300FFFF00000000000000000000",
            "warehouse-internal ACK carries destination and -1 discriminator");

        Check.Throws<ArgumentException>(
            () => PacketBuilder.WarehouseTransferAcknowledgement(
                new(
                    WarehouseTransferOperation.Deposit,
                    360,
                    0,
                    -1,
                    0,
                    WarehouseStorageType.Normal)),
            "ACK builder rejects out-of-range warehouse slots");
    }

    private static void CheckAcknowledgement(
        WarehouseTransferIntent intent,
        string expectedHex,
        string description)
    {
        var packet = PacketBuilder.WarehouseTransferAcknowledgement(intent);
        Check.Equal(expectedHex, Convert.ToHexString(packet), description);
        Check.True(
            WarehouseWireProtocol.TryReadTransfer(
                new GamePacket(packet),
                out var decoded) &&
            decoded == intent,
            $"{description} round-trips through the strict decoder");
    }

    private static WarehouseSnapshot Snapshot(
        int capacity,
        IReadOnlyList<WarehouseItemSnapshot> items) =>
        new(
            AccountId: 10,
            CharacterId: 20,
            Capacity: capacity,
            WarehouseRevision: 3,
            InventoryRevision: 4,
            Items: items);

    private static WarehouseItemSnapshot Item(
        int slot,
        uint id,
        int stack) =>
        new(slot, $"[{id},,,,,,1,1,1,{stack}]");

    private static byte[] TransferFrame(
        short warehouseSlot,
        short firstTarget,
        short secondTarget,
        byte direction,
        int money = 0,
        ushort storageType = 0,
        byte scratch16 = 0,
        short scratch10 = 0)
    {
        var packet = new byte[WarehouseWireProtocol.TransferPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            WarehouseWireProtocol.TransferPacketBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.WarehouseTransfer);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(4, 2),
            warehouseSlot);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(6, 2),
            firstTarget);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(8, 2),
            secondTarget);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(10, 2),
            scratch10);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12, 4),
            money);
        packet[16] = direction;
        packet[17] = scratch16;
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(18, 2),
            storageType);
        return packet;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
