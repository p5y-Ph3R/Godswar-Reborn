using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldInstanceSessionRoutingChecks
{
    private static CapturedMonsterSpawn CreateMonster(
        uint objectId,
        string templateKey,
        float x,
        float z)
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            237);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            237);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28, 4),
            x);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32, 4),
            0f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36, 4),
            z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            SharedMapId,
            "RoutingCheck",
            templateKey,
            templateKey,
            objectId,
            x,
            z,
            packet);
    }
}
