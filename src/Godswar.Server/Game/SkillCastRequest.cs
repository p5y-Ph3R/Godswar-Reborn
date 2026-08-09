using System.Buffers.Binary;

namespace Godswar.Server.Game;

internal readonly record struct SkillCastRequest(
    uint CasterObjectId,
    uint SkillId,
    uint TargetObjectId,
    float CasterX,
    float CasterZ,
    float TargetX,
    float TargetZ,
    bool HasTargetPosition)
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

        var casterX = declaredLength >= 32
            ? BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(24, 4))
            : float.NaN;
        var casterZ = declaredLength >= 32
            ? BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(28, 4))
            : float.NaN;
        var hasTargetPosition = declaredLength >= 40;
        var targetX = hasTargetPosition
            ? BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(32, 4))
            : float.NaN;
        var targetZ = hasTargetPosition
            ? BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(36, 4))
            : float.NaN;

        request = new SkillCastRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(16, 4)),
            casterX,
            casterZ,
            targetX,
            targetZ,
            hasTargetPosition);
        return true;
    }
}
