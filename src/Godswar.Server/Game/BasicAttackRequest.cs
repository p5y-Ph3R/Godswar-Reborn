using System.Buffers.Binary;

namespace Godswar.Server.Game;

internal readonly record struct BasicAttackRequest(
    uint AttackerObjectId,
    float AttackerX,
    float AttackerY,
    float AttackerZ,
    uint TargetObjectId)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out BasicAttackRequest request)
    {
        request = default;
        if (packet.Length != 32 ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet[..2]) != 32 ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2, 2)) !=
                Protocol.Opcodes.BasicAttack)
        {
            return false;
        }

        request = new BasicAttackRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(8, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(12, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(16, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(20, 4)));
        return true;
    }
}
