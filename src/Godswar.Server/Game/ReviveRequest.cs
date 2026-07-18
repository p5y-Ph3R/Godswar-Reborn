using System.Buffers.Binary;

namespace Godswar.Server.Game;

internal readonly record struct ReviveRequest(
    uint PlayerObjectId,
    int ReviveType)
{
    private const int PacketLength = 12;

    public static bool TryParse(ReadOnlySpan<byte> packet, out ReviveRequest request)
    {
        request = default;
        if (packet.Length < PacketLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet[..2]) != PacketLength)
        {
            return false;
        }

        request = new ReviveRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(8, 4)));
        return true;
    }
}
