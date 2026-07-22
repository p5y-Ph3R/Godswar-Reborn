using System.Buffers.Binary;

namespace Godswar.Server.Game;

internal readonly record struct ZodiacSyncRequest(
    uint PlayerId,
    ushort Module,
    ushort Sid,
    int Value1,
    int Value2,
    int Value3)
{
    private const int PacketLength = 24;

    public bool IsFullSync => Module == 0 && Sid == 1;

    public static bool TryParse(ReadOnlySpan<byte> packet, out ZodiacSyncRequest request)
    {
        request = default;
        if (packet.Length < PacketLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet[..2]) != PacketLength)
        {
            return false;
        }

        request = new ZodiacSyncRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(8, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(12, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(16, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(20, 4)));
        return true;
    }
}
