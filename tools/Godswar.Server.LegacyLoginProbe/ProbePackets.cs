using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.LegacyLoginProbe;

internal static class ProbePackets
{
    public static byte[] Login(
        string username,
        string password)
    {
        var packet = Create(Opcodes.Login, 68);
        PacketText.WriteFixedAscii(packet.AsSpan(4, 32), username);
        PacketText.WriteFixedAscii(packet.AsSpan(36, 32), password);
        return packet;
    }

    public static byte[] SelectServer(byte realmId)
    {
        var packet = Create(Opcodes.SelectServer, 44);
        packet[36] = realmId;
        return packet;
    }

    public static byte[] LoginReturnInfo() =>
        Create(Opcodes.LoginReturnInfo, 4);

    public static byte[] GameLogin(
        string username,
        string realmIdentifier,
        byte realmId)
    {
        var packet = Create(Opcodes.LoginGameServer, 62);
        PacketText.WriteFixedAscii(packet.AsSpan(4, 32), username);
        PacketText.WriteFixedAscii(packet.AsSpan(36, 25), realmIdentifier);
        packet[61] = realmId;
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
