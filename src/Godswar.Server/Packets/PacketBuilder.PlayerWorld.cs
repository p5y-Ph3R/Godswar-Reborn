using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] EquipmentVisualRefresh(GameCharacter character)
    {
        return EquipmentVisualRefresh(character, LocalPlayerObjectId);
    }

    public static byte[] EquipmentVisualRefresh(GameCharacter character, uint objectId)
    {
        var packet = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x27D9);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        // Captured 0x27D9 packets carry the avatar hair/model byte followed by
        // the one-based gender, not a constant hair id and profession.
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), character.Hair);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)character.Gender + 1u);

        var equipment = ParseEquipment(character);
        for (var slot = EquipmentSlots.Head; slot <= EquipmentSlots.Shield; slot++)
        {
            var itemId = slot < equipment.Length ? equipment[slot].Id : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16 + (slot * 4), 4), itemId);
        }

        return packet;
    }

    public static byte[] PlayerWorldSpawn(
        GameCharacter character,
        uint objectId,
        IReadOnlyList<ClientStatusEffect>? effects = null)
    {
        effects ??= [];
        if (effects.Count > PlayerWorldStatusMaximumCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effects),
                effects.Count,
                $"The player-spawn packet supports at most {PlayerWorldStatusMaximumCount} statuses.");
        }

        var packet = new byte[PlayerWorldExtendedLength];
        PlayerWorldSpawnTemplate.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerWorldSpawnOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), (uint)Math.Max(character.Id, 0));
        PacketText.WriteFixedAscii(packet.AsSpan(12, 32), character.Name);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(44, 4), Math.Max(1, character.CurrentHp));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(48, 4), Math.Max(1, character.MaxHp));
        packet[52] = character.Gender;
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(54, 2),
            (ushort)Math.Clamp(character.Level, 1, ushort.MaxValue));
        packet[56] = character.Face;
        packet[58] = ToWorldProfessionByte(character.Profession);
        packet[59] = character.Hair;
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(60, 4), character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(64, 4), character.PositionZ);
        // The third captured coordinate is terrain height. It is not persisted by
        // GameCharacter, so use the neutral value rather than shifting Z into it.
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(68, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(72, 4), 1f);
        PatchPlayerWorldAppearance(packet, character);
        packet.AsSpan(
            PlayerWorldStatusIdsOffset,
            PlayerWorldNativeLength - PlayerWorldStatusIdsOffset).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(PlayerWorldStatusCountOffset, sizeof(ushort)),
            checked((ushort)effects.Count));
        for (var index = 0; index < effects.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(
                    PlayerWorldStatusIdsOffset + (index * sizeof(uint)),
                    sizeof(uint)),
                effects[index].StatusId);
        }

        return packet;
    }

    public static byte[] PlayerAppearanceExtras(GameCharacter character, uint objectId)
    {
        // This legacy call site has no authoritative pet snapshot. A zero pet
        // ID makes the native handler ignore the packet for remote players.
        return BuildPetWorldPresence(petId: 0, objectId);
    }

    public static byte[] PetWorldPresence(
        uint petId,
        uint ownerObjectId)
    {
        if (petId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(petId),
                petId,
                "A visible pet must have a non-zero native ID.");
        }

        return BuildPetWorldPresence(petId, ownerObjectId);
    }

    private static byte[] BuildPetWorldPresence(
        uint petId,
        uint ownerObjectId)
    {
        var packet = new byte[108];

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2808);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), petId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            ownerObjectId);
        // The native 0x2808 handler consumes only pet and owner IDs before
        // selecting and calling out the already-loaded owned-pet record.
        // Preserve the remaining captured neutral body.
        packet[64] = 1;
        return packet;
    }

    public static byte[] PlayerTitleInfo(GameCharacter character, uint objectId)
    {
        var packet = new byte[80];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x27D7);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        // Offset 8 is the selected title text and offset 76 is a title id in the
        // working captures. Neither is the character id. Until titles are modeled,
        // the all-zero untitled body is the only truthful representation.
        return packet;
    }

    public static byte[] PlayerWorldMovement(ReadOnlySpan<byte> clientWalkPacket, uint objectId)
    {
        var packet = clientWalkPacket.ToArray();
        if (packet.Length < 8)
        {
            return packet;
        }

        var clientMovementState = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));
        var serverMovementState = (clientMovementState & 0xFFFF0000) | (objectId & 0xFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), serverMovementState);
        return packet;
    }

    public static byte[] PlayerWorldPosition(GameCharacter character, uint objectId)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x27D2);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), 0x00020000 | (objectId & 0xFFFF));
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8, 4), character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12, 4), character.PositionZ);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16, 4), 1f);
        return packet;
    }

    public static byte[] RemoveWorldObjects(params uint[] objectIds)
    {
        var packet = new byte[8 + (Math.Max(0, objectIds.Length) * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), WorldObjectRemoveOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)objectIds.Length);

        for (var i = 0; i < objectIds.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8 + (i * 4), 4), objectIds[i]);
        }

        return packet;
    }

    private static void PatchPlayerWorldAppearance(byte[] packet, GameCharacter character)
    {
        packet.AsSpan(PlayerWorldVisualFlagsOffset, PlayerWorldVisualFlagsLength).Clear();
        packet.AsSpan(PlayerWorldAttributeCountsOffset, PlayerWorldAttributeCountsLength).Clear();
        packet.AsSpan(PlayerWorldEquipmentIdsOffset, PlayerWorldEquipmentIdsLength * 2).Clear();
        packet.AsSpan(PlayerWorldFullVisualQualityOffset, PlayerWorldEquipmentIdsLength).Clear();
        packet.AsSpan(PlayerWorldFullVisualGradeOffset, PlayerWorldEquipmentIdsLength).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerWorldFullVisualMarkerOffset, sizeof(uint)),
            PlayerWorldFullVisualMarker);

        var visualIndex = 0;
        var equipmentMask = 0u;
        foreach (var (slot, item) in PlayerWorldEquipmentItems(character))
        {
            if (item.IsEmpty || visualIndex >= PlayerWorldEquipmentIdsLength)
            {
                continue;
            }

            if (slot < sizeof(uint) * 8)
            {
                equipmentMask |= 1u << slot;
            }

            packet[PlayerWorldVisualFlagsOffset + visualIndex] = PackWorldItemVisual(item);
            packet[PlayerWorldFullVisualQualityOffset + visualIndex] =
                (byte)Math.Clamp(item.Quality, (short)0, (short)byte.MaxValue);
            packet[PlayerWorldFullVisualGradeOffset + visualIndex] =
                (byte)Math.Clamp(item.Grade, (short)0, (short)byte.MaxValue);
            if (visualIndex < PlayerWorldAttributeCountsLength)
            {
                packet[PlayerWorldAttributeCountsOffset + visualIndex] = WorldItemAttributeCount(item);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(PlayerWorldEquipmentIdsOffset + (visualIndex * 2), 2),
                (ushort)Math.Min(item.Id, ushort.MaxValue));
            visualIndex++;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerWorldEquipmentMaskOffset, sizeof(uint)),
            equipmentMask);
    }

    private static byte PackWorldItemVisual(CompactItemEntry item)
    {
        // Captures pair each equipment id with (grade << 4) | quality. The
        // packed quality nibble safely carries the supported Q13 forge ceiling;
        // the GWX1 tail still carries the uncapped full-byte visual values.
        var grade = (int)Math.Clamp(item.Grade, (short)0, CapturedWorldVisualGradeCap);
        var quality = (int)Math.Clamp(item.Quality, (short)0, CapturedWorldVisualQualityCap);
        return (byte)((grade << 4) | quality);
    }

    private static byte WorldItemAttributeCount(CompactItemEntry item)
    {
        var count = 0;
        count += HasWorldItemAttribute(item.Attribute1) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute2) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute3) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute4) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute5) ? 1 : 0;
        return (byte)count;
    }

    private static bool HasWorldItemAttribute(int? attribute)
    {
        // Captured item records use -1 as the absent sentinel; compact records use null.
        return attribute is >= 0;
    }
}
