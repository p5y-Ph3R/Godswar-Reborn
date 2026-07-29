using System.Buffers.Binary;

namespace Godswar.Server.Protocol;

internal sealed class GamePacket
{
    public GamePacket(
        byte[] buffer,
        Guid? clientOperationId = null)
    {
        if (buffer.Length < 4)
        {
            throw new ArgumentException("Packet must contain at least a length and opcode.", nameof(buffer));
        }

        Buffer = buffer;
        ClientOperationId = clientOperationId;
        Length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(0, 2));
        Opcode = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(2, 2));
    }

    public ushort Length { get; }

    public ushort Opcode { get; }

    public byte[] Buffer { get; }

    public Guid? ClientOperationId { get; }

    public ReadOnlySpan<byte> Payload => Buffer.AsSpan(4);

    public string ToHexPreview(int maxBytes = 64)
    {
        var count = Math.Min(Buffer.Length, maxBytes);
        return Convert.ToHexString(Buffer.AsSpan(0, count));
    }
}
