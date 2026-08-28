using System.Buffers.Binary;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const ushort ObjectFightStateOpcode = 10025;

    public static byte[] ObjectFightState(
        uint objectId,
        bool engaged)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            ObjectFightStateOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            engaged ? 1u : 0u);
        return packet;
    }
}
