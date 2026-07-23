using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] PlayerDetail(GameCharacter character)
    {
        var packet = PlayerDetailTemplate.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerDetailOpcode);
        PatchReferencePlayerPacket(packet, character, nameOffset: 4);
        // MSG_PLAYERDETAIL copies wire offset 4 onward directly into the
        // client's local GameData structure. These six consecutive fields
        // are MaxHP, MaxMP, HP, MP, Money, and Stone in the original layout.
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

    public static byte[] PlayerDetailAck(ReadOnlySpan<byte> requestPayload)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerDetailAckOpcode);
        if (requestPayload.Length >= 4)
        {
            requestPayload[..4].CopyTo(packet.AsSpan(4, 4));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        }

        return packet;
    }

    public static byte[] PlayerDetailRefreshAck()
    {
        return PlayerDetailRefreshAck(LocalPlayerObjectId);
    }

    public static byte[] PlayerDetailRefreshAck(uint objectId)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerDetailAckOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 1);
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
