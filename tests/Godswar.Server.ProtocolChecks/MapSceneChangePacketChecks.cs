using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class MapSceneChangePacketChecks
{
    public static Task RunAsync()
    {
        const uint playerObjectId = 0x1448;
        const float x = 93.5f;
        const float y = 1.25f;
        const float z = -227.75f;
        const byte mapId = 12;

        var packet = PacketBuilder.SceneChange(
            playerObjectId,
            x,
            y,
            z,
            mapId);

        Check.Equal(24, packet.Length, "scene-change packet length");
        Check.Equal(
            (ushort)packet.Length,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "scene-change declared length");
        Check.Equal(
            Opcodes.SceneChange,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "scene-change opcode");
        Check.Equal(
            playerObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)),
            "scene-change local-player object ID");
        Check.Equal(
            x,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(8)),
            "scene-change X");
        Check.Equal(
            y,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(12)),
            "scene-change Y");
        Check.Equal(
            z,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(16)),
            "scene-change Z");
        Check.Equal(
            (ushort)0,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(20)),
            "scene-change ignored field remains neutral");
        Check.Equal(
            (ushort)mapId,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(22)),
            "scene-change runtime map ID");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.SceneChange(0, x, y, z, mapId),
            "scene change rejects object ID zero");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.SceneChange(
                playerObjectId,
                float.NaN,
                y,
                z,
                mapId),
            "scene change rejects non-finite coordinates");

        return Task.CompletedTask;
    }
}
