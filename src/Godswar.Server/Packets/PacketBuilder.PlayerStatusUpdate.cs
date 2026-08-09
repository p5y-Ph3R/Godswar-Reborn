using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const ushort PlayerStatusUpdateOpcode = 0x27B6;
    private const int PlayerStatusMovementSpeedMultiplierOffset = 56;
    // Wire offset 60 is copied to GameData+0x290. Although that dword looks
    // unused in the status panel, the native NPC interaction path reads byte
    // GameData+0x292 as the local interaction identity/faction. Writing a
    // float here can therefore make every NPC unselectable client-side.
    private const int PlayerStatusInteractionIdentityOffset = 60;
    private const int PlayerStatusSilverOffset = 120;
    private const int PlayerStatusGoldOffset = 124;
    private const int PlayerStatusPhysicalDefenseOffset = 164;
    private const int PlayerStatusMagicDefenseOffset = 172;
    private const int PlayerStatusHitOffset = 176;
    private const int PlayerStatusDodgeOffset = 180;
    private const int PlayerStatusCriticalOffset = 184;
    private const int PlayerStatusCriticalResistanceOffset = 188;
    private const int PlayerStatusTalentPointsOffset = 228;

    private static readonly byte[] PlayerStatusUpdateTemplate = Convert.FromHexString(
        "EC00B6271D01000074657374696E6739000000000000000000000000000000000000000000000000" +
        "0100000000002543000000000000C2C20000803F0000000000000000000000000000000000000000" +
        "000000002800000001000000010000000000000001000000330500007C0100000100DC0535000000" +
        "000000000000000000000000000000000000000000000000330500007C010000320000002F000000" +
        "14000000220000000F00000006000000140000001D00000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000001000000DC0500000000000005000000");

    public static byte[] PlayerStatusUpdate(GameCharacter character, uint objectId)
    {
        return PlayerStatusUpdate(
            character,
            objectId,
            ClientStatusAggregate.Empty);
    }

    public static byte[] PlayerStatusUpdate(
        GameCharacter character,
        float movementSpeedMultiplier)
    {
        return PlayerStatusUpdate(
            character,
            LocalPlayerObjectId,
            ClientStatusAggregate.Empty with
            {
                MovementSpeedMultiplier = movementSpeedMultiplier
            });
    }

    public static byte[] PlayerStatusUpdate(
        GameCharacter character,
        uint objectId,
        float movementSpeedMultiplier)
    {
        return PlayerStatusUpdate(
            character,
            objectId,
            ClientStatusAggregate.Empty with
            {
                MovementSpeedMultiplier = movementSpeedMultiplier
            });
    }

    public static byte[] PlayerStatusUpdate(
        GameCharacter character,
        ClientStatusAggregate aggregate)
    {
        return PlayerStatusUpdate(
            character,
            LocalPlayerObjectId,
            aggregate);
    }

    public static byte[] PlayerStatusUpdate(
        GameCharacter character,
        uint objectId,
        ClientStatusAggregate aggregate)
    {
        if (!float.IsFinite(aggregate.MovementSpeedMultiplier) ||
            aggregate.MovementSpeedMultiplier <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregate),
                aggregate.MovementSpeedMultiplier,
                "The movement-speed multiplier must be finite and positive.");
        }

        if (!float.IsFinite(aggregate.EquippedRidingSpeedBonus) ||
            aggregate.EquippedRidingSpeedBonus is < 0f or > 9f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregate),
                aggregate.EquippedRidingSpeedBonus,
                "The equipped riding-speed bonus must be finite and between zero and nine.");
        }

        var packet = PlayerStatusUpdateTemplate.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerStatusUpdateOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        PatchReferencePlayerPacket(packet, character, nameOffset: 8);
        // MSG_SYN_GAMEDATA copies this field into the local GameData movement
        // speed. Opcode 10167 carries the status/aura snapshot, but it does not
        // update the locomotion value used by client movement prediction.
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(PlayerStatusMovementSpeedMultiplierOffset, 4),
            aggregate.MovementSpeedMultiplier);
        // Preserve the stock-zero interaction identity dword for both local
        // and remote status packets. Riding speed must use a separately proven
        // client channel; encoding it here suppresses opcode 10067 at source.
        packet.AsSpan(PlayerStatusInteractionIdentityOffset, sizeof(uint)).Clear();
        var stats = character.CalculatedStats ??
            CharacterStats.FromCharacter(character);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusPhysicalDefenseOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.PhysicalDefense,
                aggregate.PhysicalDefense));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusMagicDefenseOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.MagicDefense,
                aggregate.MagicDefense));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusHitOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.Hit,
                aggregate.Hit));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusDodgeOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.Dodge,
                aggregate.Dodge));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusCriticalOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.Critical,
                aggregate.CriticalAppend));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(
                PlayerStatusCriticalResistanceOffset,
                sizeof(int)),
            SaturatingStatusValue(
                stats.CriticalResistance,
                aggregate.CriticalResistance));
        if (objectId == LocalPlayerObjectId)
        {
            // MSG_SYN_GAMEDATA copies these wire fields into the local
            // GameData Money/Stone values that drive the wallet UI.
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusSilverOffset, 4),
                Math.Max(0, character.Silver));
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusGoldOffset, 4),
                Math.Max(0, character.Gold));
        }

        if (packet.Length >= PlayerStatusTalentPointsOffset + 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusTalentPointsOffset, 4),
                character.TalentPoints);
        }

        return packet;
    }
}
