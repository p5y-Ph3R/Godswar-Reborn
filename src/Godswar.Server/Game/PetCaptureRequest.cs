using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal readonly record struct PetCaptureRequest(
    uint TargetObjectId,
    int KitBagSlot,
    float ReportedPlayerX,
    float ReportedPlayerZ,
    float ReportedTargetX,
    float ReportedTargetZ)
{
    private const int PacketBytes = 28;
    private const int BagPageCount = 4;
    private const int BagSlotsPerPage = 24;

    public static bool TryRead(
        GamePacket packet,
        out PetCaptureRequest request)
    {
        request = default;
        if (packet.Opcode != Opcodes.PetCaptureRequest ||
            packet.Length != PacketBytes ||
            packet.Buffer.Length != PacketBytes ||
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.Buffer.AsSpan(0, 2)) != PacketBytes)
        {
            return false;
        }

        var targetObjectId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Buffer.AsSpan(4, 4));
        var bagPage = BinaryPrimitives.ReadUInt16LittleEndian(
            packet.Buffer.AsSpan(8, 2));
        var pageSlot = BinaryPrimitives.ReadUInt16LittleEndian(
            packet.Buffer.AsSpan(10, 2));
        var playerX = BinaryPrimitives.ReadSingleLittleEndian(
            packet.Buffer.AsSpan(12, 4));
        var playerZ = BinaryPrimitives.ReadSingleLittleEndian(
            packet.Buffer.AsSpan(16, 4));
        var targetX = BinaryPrimitives.ReadSingleLittleEndian(
            packet.Buffer.AsSpan(20, 4));
        var targetZ = BinaryPrimitives.ReadSingleLittleEndian(
            packet.Buffer.AsSpan(24, 4));
        if (targetObjectId == 0 ||
            bagPage >= BagPageCount ||
            pageSlot >= BagSlotsPerPage ||
            !float.IsFinite(playerX) ||
            !float.IsFinite(playerZ) ||
            !float.IsFinite(targetX) ||
            !float.IsFinite(targetZ))
        {
            return false;
        }

        request = new(
            targetObjectId,
            checked((bagPage * BagSlotsPerPage) + pageSlot),
            playerX,
            playerZ,
            targetX,
            targetZ);
        return true;
    }
}
