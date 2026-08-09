using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.LegacyLoginProbe;

internal static class ProbePackets
{
    public static byte[] GameLogin(string username)
    {
        var packet = Create(Opcodes.LoginGameServer, 36);
        PacketText.WriteFixedAscii(packet.AsSpan(4, 32), username);
        return packet;
    }

    public static byte[] EnterGame() => Create(Opcodes.EnterGame, 4);

    public static byte[] ServerTimeRequest() =>
        Create(Opcodes.ServerTimeRequest, 16);

    public static byte[] ClientReady() =>
        Create(Opcodes.ClientReady, 34);

    public static byte[] PlayerDetailRequest()
    {
        var packet = Create(Opcodes.PlayerDetailRequest, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 1);
        return packet;
    }

    public static byte[] EnterUiReady() =>
        Create(Opcodes.EnterUiReady, 8);

    public static byte[] NpcDialogOpen(uint npcId)
    {
        var packet = Create(Opcodes.NpcDialogOpen, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), npcId);
        return packet;
    }

    private static byte[] Create(ushort opcode, int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        return packet;
    }
}
