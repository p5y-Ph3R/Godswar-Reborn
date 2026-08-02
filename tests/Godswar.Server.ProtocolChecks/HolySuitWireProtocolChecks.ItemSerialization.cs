using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolySuitWireProtocolChecks
{
    private static void CheckHolyBoxStoredExperienceSerialization()
    {
        const int itemRecordOffset = 24;
        const int equipmentExperienceOffset = 28;
        const int holyBoxExperienceOffset = 56;
        const int capturedExperience = 400_000_000;
        const uint capturedFixedPointExperience = 4_000_000_000;

        var character = new GameCharacter
        {
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                0,
                $"[9024,,,,,,1,1,1,1,{capturedExperience}]")
        };
        var packet = PacketBuilder.KitBagDetailPages(character)[0];
        var record = packet.AsSpan(itemRecordOffset, 72);

        Check.Equal(
            (ushort)10033,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "Holy Box is hydrated through the native item-detail opcode");
        Check.Equal(
            9024u,
            BinaryPrimitives.ReadUInt32LittleEndian(record),
            "Holy Box item ID");
        Check.Equal(
            0,
            BinaryPrimitives.ReadInt32LittleEndian(
                record.Slice(equipmentExperienceOffset, sizeof(int))),
            "Holy Box does not misuse the equipment EXP field");
        Check.Equal(
            capturedFixedPointExperience,
            BinaryPrimitives.ReadUInt32LittleEndian(
                record.Slice(holyBoxExperienceOffset, sizeof(int))),
            "Holy Box accumulated EXP uses the captured fixed-point field");
        Check.Equal(
            capturedExperience,
            checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(
                record.Slice(holyBoxExperienceOffset, sizeof(int))) / 10u)),
            "stock-client fixed-point decoding restores Holy Box EXP");
        Check.True(
            record.Slice(holyBoxExperienceOffset, sizeof(int))
                .SequenceEqual(Convert.FromHexString("00286BEE")),
            "Holy Box accumulated EXP matches the working-server golden bytes");

        foreach (var (itemId, capacity, fixedPointHex) in
                 new (uint ItemId, int Capacity, string FixedPointHex)[]
                 {
                     (9020, 100_000, "40420F00"),
                     (9021, 1_000_000, "80969800"),
                     (9022, 10_000_000, "00E1F505"),
                     (9023, 100_000_000, "00CA9A3B"),
                     (9024, 400_000_000, "00286BEE")
                 })
        {
            character.KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                0,
                $"[{itemId},,,,,,1,1,1,1,{capacity}]");
            packet = PacketBuilder.KitBagDetailPages(character)[0];
            record = packet.AsSpan(itemRecordOffset, 72);
            Check.Equal(
                checked((uint)((long)capacity * 10L)),
                BinaryPrimitives.ReadUInt32LittleEndian(
                    record.Slice(holyBoxExperienceOffset, sizeof(int))),
                $"full Holy Box {itemId} serializes its fixed-point capacity");
            Check.Equal(
                capacity,
                checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(
                    record.Slice(holyBoxExperienceOffset, sizeof(int))) / 10u)),
                $"stock client displays full Holy Box {itemId} capacity");
            Check.True(
                record.Slice(holyBoxExperienceOffset, sizeof(int))
                    .SequenceEqual(Convert.FromHexString(fixedPointHex)),
                $"full Holy Box {itemId} matches its golden fixed-point bytes");
        }

        character.KitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            "[2007,10,30,100,120,,20,25,1,1,123456]");
        packet = PacketBuilder.KitBagDetailPages(character)[0];
        record = packet.AsSpan(itemRecordOffset, 72);
        Check.Equal(
            123_456,
            BinaryPrimitives.ReadInt32LittleEndian(
                record.Slice(equipmentExperienceOffset, sizeof(int))),
            "equipment EXP retains its existing offset 28 wire contract");
        Check.Equal(
            0,
            BinaryPrimitives.ReadInt32LittleEndian(
                record.Slice(holyBoxExperienceOffset, sizeof(int))),
            "ordinary gear does not populate the Holy Box EXP field");
    }
}
