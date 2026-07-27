using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int SceneChangePacketLength = 24;

    /// <summary>
    /// Builds the native local-player scene-load message recovered from the
    /// installed Origin client. Offset 20 is ignored by that client handler
    /// and is deliberately emitted as zero.
    /// </summary>
    public static byte[] SceneChange(
        uint playerObjectId,
        float x,
        float y,
        float z,
        byte mapId)
    {
        if (playerObjectId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playerObjectId),
                "A scene change requires a nonzero local-player object ID.");
        }

        if (!float.IsFinite(x) ||
            !float.IsFinite(y) ||
            !float.IsFinite(z))
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                "Scene-change coordinates must be finite.");
        }

        var packet = new byte[SceneChangePacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            SceneChangePacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.SceneChange);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            playerObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8),
            x);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(12),
            y);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16),
            z);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(20),
            0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(22),
            mapId);
        return packet;
    }
}
