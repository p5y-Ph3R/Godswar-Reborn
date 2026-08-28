using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] ServerNote(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Any(static character => character > sbyte.MaxValue) ||
            message.Length > 255)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "Native server notices require at most 255 ASCII bytes.");
        }

        var packet = new byte[260];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.ServerNote);
        PacketText.WriteFixedAscii(packet.AsSpan(4, 256), message);
        return packet;
    }
}
