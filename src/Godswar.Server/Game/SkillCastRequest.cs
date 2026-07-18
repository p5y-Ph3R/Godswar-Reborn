using System.Buffers.Binary;

namespace Godswar.Server.Game;

internal readonly record struct SkillCastRequest(
    uint CasterObjectId,
    uint SkillId,
    uint TargetObjectId,
    float CasterX,
    float CasterZ,
    float TargetX,
    float TargetZ)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out SkillCastRequest request)
    {
        request = default;
        if (packet.Length < 20)
        {
            return false;
        }

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[..2]);
        if (declaredLength < 20 || declaredLength > packet.Length)
        {
            return false;
        }

        var casterX = packet.Length >= 32
            ? BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(24, 4))
            : float.NaN;
        var casterZ = packet.Length >= 32
            ? BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(28, 4))
            : float.NaN;
        var targetX = packet.Length >= 40
            ? BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(32, 4))
            : casterX;
        var targetZ = packet.Length >= 40
            ? BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(36, 4))
            : casterZ;

        request = new SkillCastRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(16, 4)),
            casterX,
            casterZ,
            targetX,
            targetZ);
        return true;
    }
}
