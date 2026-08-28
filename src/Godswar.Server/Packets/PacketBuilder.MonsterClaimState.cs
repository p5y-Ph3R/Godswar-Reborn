using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] MonsterClaimState(uint objectId)
    {
        if (objectId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectId));
        }

        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.MonsterClaimState);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            0xFFFFFF01);
        return packet;
    }
}
