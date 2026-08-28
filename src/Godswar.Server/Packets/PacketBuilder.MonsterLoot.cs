using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int MonsterLootHeaderLength = 12;
    private const int MonsterLootItemLength = 72;

    public static byte[] MonsterLoot(
        uint monsterObjectId,
        IReadOnlyList<MonsterLootEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (monsterObjectId == 0 || entries.Count > 32 ||
            entries.Any(static entry =>
                entry.ItemId == 0 || entry.Quantity is < 1 or > 255))
        {
            throw new ArgumentOutOfRangeException(nameof(entries));
        }

        var packet = new byte[
            MonsterLootHeaderLength + entries.Count * MonsterLootItemLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.MonsterDrops);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            monsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            checked((uint)entries.Count));

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var item = packet.AsSpan(
                MonsterLootHeaderLength + index * MonsterLootItemLength,
                MonsterLootItemLength);
            BinaryPrimitives.WriteUInt32LittleEndian(item, entry.ItemId);
            for (var sentinel = 1; sentinel <= 5; sentinel++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    item.Slice(sentinel * sizeof(uint)),
                    uint.MaxValue);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(
                item.Slice(24),
                checked(((uint)entry.Quantity << 24) | 0x0000_0101u));
        }
        return packet;
    }

    public static byte[] MonsterLootPickup(
        uint playerObjectId,
        uint monsterObjectId,
        int pickupIndex)
    {
        if (playerObjectId == 0 || monsterObjectId == 0 ||
            pickupIndex is < 0 or >= 32)
        {
            throw new ArgumentOutOfRangeException(nameof(pickupIndex));
        }

        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PickupDrops);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            playerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            monsterObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12),
            pickupIndex);
        return packet;
    }
}
