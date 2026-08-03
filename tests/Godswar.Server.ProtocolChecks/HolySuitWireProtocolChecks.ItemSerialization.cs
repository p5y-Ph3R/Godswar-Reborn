using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolySuitWireProtocolChecks
{
    private static void CheckClassSuitAttributeExtensionSerialization()
    {
        const int itemRecordOffset = 24;
        const uint marker = 0x32415747;
        var item = CompactItemEntry.Empty with
        {
            Id = 1035,
            Quality = 20,
            Grade = 25,
            Bound = 1,
            Stack = 1,
            Attribute1 = 10,
            Attribute2 = 40,
            Attribute3 = 60,
            Attribute4 = 80,
            Attribute5 = 130,
            AttributeLevel1 = 25,
            AttributeLevel2 = 25,
            AttributeLevel3 = 25,
            AttributeLevel4 = 25,
            AttributeLevel5 = 25,
            ClassAttribute1 = 200,
            ClassAttribute2 = 210
        };
        var character = new GameCharacter
        {
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                0,
                item.ToCompactString())
        };

        var packet = PacketBuilder.KitBagDetailPages(character)[0];
        var record = packet.AsSpan(itemRecordOffset, 72);
        Check.Equal(
            130,
            BinaryPrimitives.ReadInt32LittleEndian(record.Slice(20, 4)),
            "fifth ordinary attribute retains its native field");
        Check.Equal(
            200,
            BinaryPrimitives.ReadInt32LittleEndian(record.Slice(52, 4)),
            "first Class Suit attribute uses extension field one");
        Check.Equal(
            210,
            BinaryPrimitives.ReadInt32LittleEndian(record.Slice(56, 4)),
            "second Class Suit attribute uses extension field two");
        Check.Equal(
            marker,
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(60, 4)),
            "Class Suit extension requires the GWA2 marker");

        character.KitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            (item with { Id = 2133 }).ToCompactString());
        packet = PacketBuilder.KitBagDetailPages(character)[0];
        record = packet.AsSpan(itemRecordOffset, 72);
        Check.True(
            BinaryPrimitives.ReadInt32LittleEndian(record.Slice(52, 4)) == 200 &&
            BinaryPrimitives.ReadInt32LittleEndian(record.Slice(56, 4)) == 210 &&
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(60, 4)) == marker,
            "Tier III armor carries the same canonical Class Suit extension as a weapon");

        foreach (var ineligibleItemId in new uint[] { 1013, 1032 })
        {
            character.KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                0,
                (item with
                {
                    Id = ineligibleItemId,
                    ClassAttribute2 = null
                }).ToCompactString());
            packet = PacketBuilder.KitBagDetailPages(character)[0];
            record = packet.AsSpan(itemRecordOffset, 72);
            Check.Equal(
                0u,
                BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(60, 4)),
                $"ineligible common/lower-tier item {ineligibleItemId} cannot advertise GWA2");
        }

        character.KitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            (item with
            {
                ClassAttribute1 = null,
                ClassAttribute2 = null
            }).ToCompactString());
        packet = PacketBuilder.KitBagDetailPages(character)[0];
        record = packet.AsSpan(itemRecordOffset, 72);
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(60, 4)),
            "ordinary equipment cannot accidentally advertise GWA2");

        character.KitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            (item with
            {
                ClassAttribute1 = 999,
                ClassAttribute2 = null
            }).ToCompactString());
        packet = PacketBuilder.KitBagDetailPages(character)[0];
        record = packet.AsSpan(itemRecordOffset, 72);
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(60, 4)),
            "unrecognized Class Suit IDs fail closed without an extension marker");
    }

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

        character.KitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            (CompactItemEntry.Empty with
            {
                Id = 9024,
                Quality = 1,
                Grade = 1,
                Bound = 1,
                Stack = 1,
                Exp = capturedExperience,
                ClassAttribute1 = 200,
                ClassAttribute2 = 210
            }).ToCompactString());
        packet = PacketBuilder.KitBagDetailPages(character)[0];
        record = packet.AsSpan(itemRecordOffset, 72);
        Check.Equal(
            capturedFixedPointExperience,
            BinaryPrimitives.ReadUInt32LittleEndian(
                record.Slice(holyBoxExperienceOffset, sizeof(int))),
            "malformed Class Suit state cannot overwrite Holy Box stored EXP");
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(60, 4)),
            "Holy Boxes never advertise the Class Suit extension marker");

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
