using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] CharacterPreview(GameCharacter character)
    {
        var equipmentIds = ParseEquipmentIds(EquipmentFor(character));
        var payloadLength = 32 + 7 + (equipmentIds.Length * 4) + 48;
        var packet = new byte[payloadLength + 5];

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2712);
        packet[4] = 0x01;

        var offset = 5;
        PacketText.WriteFixedAscii(packet.AsSpan(offset, 32), character.Name);
        offset += 32;

        packet[offset++] = character.Camp;
        packet[offset++] = ToClientProfessionByte(character.Profession);
        packet[offset++] = (byte)Math.Clamp(character.Level, 1, 255);
        packet[offset++] = character.Gender;
        packet[offset++] = character.Hair;
        packet[offset++] = character.Face;
        // All working-original character previews carry a nonzero faith/control
        // value. Legacy imported rows can contain zero; normalize those rows so
        // the client does not dereference an invalid preview particle state.
        packet[offset++] = character.Faith == 0 ? (byte)1 : character.Faith;

        foreach (var itemId in equipmentIds)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), itemId);
            offset += 4;
        }

        return packet;
    }

    public static byte[] EnterPart1(GameCharacter character)
    {
        return EnterStart(character).Part1;
    }

    public static byte[] EnterMain(GameCharacter character)
    {
        var header = CreateEnterPart1Header(character);
        var continuation = ReferencePackets.EnterPart2Unknown;
        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        var packet = new byte[declaredLength];

        header.CopyTo(packet.AsSpan(0, Math.Min(header.Length, packet.Length)));
        var continuationLength = Math.Min(packet.Length - header.Length, continuation.Length);
        continuation[..continuationLength].CopyTo(packet.AsSpan(header.Length, continuationLength));

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), EnterMainOpcode);
        PatchEnterEquipment(packet, character);
        return packet;
    }

    public static byte[] EnterUiBootstrap()
    {
        return EnterUiBootstrapTemplate.ToArray();
    }

    public static byte[] EnterComplete()
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), EnterCompleteOpcode);
        return packet;
    }

    public static (byte[] Part1, byte[] Part2Unknown) EnterStart(GameCharacter character)
    {
        var part1 = CreateEnterPart1Header(character);
        var part2Unknown = ReferencePackets.EnterPart2Unknown.ToArray();
        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(part1.AsSpan(0, 2));
        var continuationLength = Math.Min(declaredLength - part1.Length, part2Unknown.Length);

        var combined = new byte[part1.Length + continuationLength];
        part1.CopyTo(combined.AsSpan(0, part1.Length));
        part2Unknown.AsSpan(0, continuationLength).CopyTo(combined.AsSpan(part1.Length));

        PatchEnterEquipment(combined, character);

        combined.AsSpan(0, part1.Length).CopyTo(part1);
        combined.AsSpan(part1.Length, continuationLength).CopyTo(part2Unknown);
        return (part1, part2Unknown);
    }

    private static byte[] CreateEnterPart1Header(GameCharacter character)
    {
        var packet = ReferencePackets.EnterPart1.ToArray();
        // This is the persistent character key used by the client for per-character
        // UI preferences (including Skill.xml hotkeys). It is not a world object ID.
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterPlayerDatabaseIdOffset, 4), character.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(EnterPlayerObjectIdOffset, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(EnterPositionXOffset, 4), character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(EnterPositionYOffset, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(EnterPositionZOffset, 4), character.PositionZ);
        // The client renders the current fields first and the max fields second.
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterMaxHpOffset, 4), character.MaxHp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterMaxMpOffset, 4), character.MaxMp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterCurrentHpOffset, 4), character.CurrentHp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterCurrentMpOffset, 4), character.CurrentMp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterExperienceOffset, 4), character.Experience);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(EnterNextLevelExperienceOffset, 4),
            PlayerExperienceCatalog.GetNextLevelExperience(character.Level));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(EnterTalentExperienceOffset, 4),
            character.TalentExperience);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterTalentPointsOffset, 4), character.TalentPoints);
        PacketText.WriteFixedAscii(packet.AsSpan(CharacterNameOffsetInEnterTemplate, 32), character.Name);

        var offset = CharacterNameOffsetInEnterTemplate + 32;
        if (packet.Length >= offset + 8)
        {
            packet[offset++] = character.Gender;
            packet[offset++] = character.Camp;
            packet[offset++] = character.Faith;
            packet[offset++] = ToClientProfessionByte(character.Profession);
            packet[offset++] = character.Hair;
            packet[offset++] = character.Face;
            packet[offset++] = character.CurrentMap;
            packet[offset] = 0;
        }

        return packet;
    }

    private static uint[] ParseEquipmentIds(string equipment)
    {
        return equipment.Split('#', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseEquipmentId)
            .ToArray();
    }

    private static uint ParseEquipmentId(string entry)
    {
        if (entry == "[]")
        {
            return uint.MaxValue;
        }

        var clean = entry.Trim('[', ']');
        var idText = clean.Split(',', 2)[0];
        return uint.TryParse(idText, out var id) ? id : uint.MaxValue;
    }

    public static string EnterEquipmentSummary(GameCharacter character)
    {
        return string.Join(
            ",",
            EquipmentItemsBySlot(character)
                .Where(entry => !entry.Item.IsEmpty)
                .Select(entry =>
                {
                    var suit = entry.Item.HolySuitCode > 0 ? $":s{entry.Item.HolySuitCode}:xp{entry.Item.Exp}" : string.Empty;
                    return $"{entry.Slot}:{entry.Item.Id}:q{entry.Item.Quality}:g{entry.Item.Grade}{suit}";
                }));
    }

    private static void PatchEnterEquipment(byte[] packet, GameCharacter character)
    {
        var items = EnterEquipmentSlots
            .Select(slot =>
            {
                var item = EquipmentSlots.GetItem(EquipmentFor(character), character.Profession, slot);
                return (Slot: slot, Item: item);
            })
            .Where(entry => !entry.Item.IsEmpty)
            .ToArray();
        var availableRecords = Math.Max(0, (packet.Length - EnterEquipmentOffset) / EnterItemRecordLength);
        var equipmentMask = 0;
        for (var i = 0; i < availableRecords; i++)
        {
            var offset = EnterEquipmentOffset + (i * EnterItemRecordLength);
            packet.AsSpan(offset, EnterItemRecordLength).Clear();
        }

        for (var i = 0; i < items.Length && i < availableRecords; i++)
        {
            var offset = EnterEquipmentOffset + (i * EnterItemRecordLength);
            equipmentMask |= 1 << items[i].Slot;
            WriteEnterItemRecord(packet.AsSpan(offset, EnterItemRecordLength), items[i].Item);
        }

        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterEquipmentMaskOffset, 4), equipmentMask);
    }

    private static CompactItemEntry[] EnterEquipmentItems(GameCharacter character)
    {
        var equipment = ParseEquipment(character);

        return EnterEquipmentSlots
            .Select(slot => slot < equipment.Length ? equipment[slot] : default)
            .ToArray();
    }
}
