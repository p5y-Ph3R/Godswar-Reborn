using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int PlayerDetailMapIdOffset = 38;
    private const int PlayerDetailMedusaHonorOffset = 124;

    public static byte[] PlayerDetail(GameCharacter character)
    {
        var packet = PlayerDetailTemplate.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerDetailOpcode);
        PatchReferencePlayerPacket(packet, character, nameOffset: 4);
        // MSG_PLAYERDETAIL copies wire offset 4 to GameData+0x25C. The
        // current-map word at GameData+0x27E is therefore wire offset 38.
        // Leaving the captured map-zero value here undoes a scene change and
        // makes subsequent monster appearances query the wrong Monster.ini.
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(PlayerDetailMapIdOffset, sizeof(ushort)),
            character.CurrentMap);
        // MSG_PLAYERDETAIL copies wire offset 4 to GameData+0x25C. Thus the
        // native Money/Stone fields at +0x2CC/+0x2D0 map to physical wire
        // offsets 116/120 respectively.
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(PlayerDetailMaxHpOffset, 4), character.MaxHp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(PlayerDetailMaxMpOffset, 4), character.MaxMp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(PlayerDetailCurrentHpOffset, 4), character.CurrentHp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(PlayerDetailCurrentMpOffset, 4), character.CurrentMp);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerDetailSilverOffset, 4),
            Math.Max(0, character.Silver));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerDetailGoldOffset, 4),
            Math.Max(0, character.Gold));
        // Repetition rewards add both native SimplePoint and HardPoint to
        // GameData+0x2D4. PlayerDetail wire offset 124 initializes that same
        // Honor balance on login and map refresh.
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerDetailMedusaHonorOffset, 4),
            Math.Max(0, character.MedusaHonorPoints));
        return packet;
    }

    public static byte[] PlayerUnknown10098(int value)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerUnknown10098Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), value);
        return packet;
    }

    public static byte[] EquipmentEffectVisibility(
        uint objectId,
        bool visible)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            FashionEffectVisibilityOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            visible ? 1u : 0u);
        return packet;
    }

    public static byte[] PlayerDetailAck(
        ReadOnlySpan<byte> requestPayload)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            PlayerDetailAckOpcode);
        if (requestPayload.Length >= 4)
        {
            requestPayload[..4].CopyTo(packet.AsSpan(4, 4));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(4, 4),
                LocalPlayerObjectId);
        }

        return packet;
    }

    public static byte[] PlayerDetailRefreshAck(uint objectId)
    {
        var packet = PlayerDetailAck([]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            1);
        return packet;
    }

    public static byte[] SelfInfoRefresh(GameCharacter character)
    {
        return EnterMain(character);
    }

    public static int ToClientEquipmentSlot(int equipmentSlot)
    {
        return equipmentSlot;
    }
}
