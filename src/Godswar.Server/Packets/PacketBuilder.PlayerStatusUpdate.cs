using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const ushort PlayerStatusUpdateOpcode = 0x27B6;
    private const int PlayerStatusMovementSpeedMultiplierOffset = 56;
    private const int PlayerStatusSilverOffset = 120;
    private const int PlayerStatusGoldOffset = 124;
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
        return PlayerStatusUpdate(character, objectId, movementSpeedMultiplier: 1f);
    }

    public static byte[] PlayerStatusUpdate(
        GameCharacter character,
        float movementSpeedMultiplier)
    {
        return PlayerStatusUpdate(
            character,
            LocalPlayerObjectId,
            movementSpeedMultiplier);
    }

    public static byte[] PlayerStatusUpdate(
        GameCharacter character,
        uint objectId,
        float movementSpeedMultiplier)
    {
        if (!float.IsFinite(movementSpeedMultiplier) ||
            movementSpeedMultiplier <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(movementSpeedMultiplier),
                movementSpeedMultiplier,
                "The movement-speed multiplier must be finite and positive.");
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
            movementSpeedMultiplier);
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
