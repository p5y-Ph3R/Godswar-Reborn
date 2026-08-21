using System.Buffers.Binary;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.CombatDummyHost;

internal static class DummyPackets
{
    public const uint LocalPlayerObjectId = 0x0000_1448;

    internal const string TempestRealmIdentifier =
        "KAL3jcIzqGgKvOf1dbYZKC8cS";

    public static byte[] GameLogin(string username)
    {
        var packet = Create(
            Opcodes.LoginGameServer,
            LegacyGameLoginPacket.PacketLength);
        PacketText.WriteFixedAscii(
            packet.AsSpan(
                LegacyGameLoginPacket.UsernameOffset,
                LegacyGameLoginPacket.UsernameLength),
            username);
        PacketText.WriteFixedAscii(
            packet.AsSpan(
                LegacyGameLoginPacket.IdentifierOffset,
                LegacyGameLoginPacket.IdentifierLength),
            TempestRealmIdentifier);
        packet[LegacyGameLoginPacket.RealmIdOffset] =
            checked((byte)RealmId.Tempest.Value);
        return packet;
    }

    public static byte[] EnterGame() =>
        Create(Opcodes.EnterGame, 4);

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

    public static byte[] Ping() =>
        Create(Opcodes.Ping, 4);

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
