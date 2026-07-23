using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] CapturedMonsterSpawns(IReadOnlyList<CapturedMonsterSpawn> spawns)
    {
        if (spawns.Count == 0)
        {
            return [];
        }

        var length = 0;
        foreach (var spawn in spawns)
        {
            length += spawn.Packet.Length;
        }

        var stream = new byte[length];
        var offset = 0;
        foreach (var spawn in spawns)
        {
            spawn.Packet.CopyTo(stream.AsSpan(offset));
            offset += spawn.Packet.Length;
        }

        return stream;
    }

    public static byte[] CapturedMonsterSpawns(IReadOnlyList<CapturedMonsterAppearanceState> spawns)
    {
        if (spawns.Count == 0)
        {
            return [];
        }

        var packets = new byte[spawns.Count][];
        var length = 0;
        for (var index = 0; index < spawns.Count; index++)
        {
            packets[index] = CapturedMonsterAppearance(spawns[index]);
            length += packets[index].Length;
        }

        var stream = new byte[length];
        var offset = 0;
        foreach (var packet in packets)
        {
            packet.CopyTo(stream.AsSpan(offset));
            offset += packet.Length;
        }

        return stream;
    }

    public static byte[] CapturedMonsterAppearance(CapturedMonsterAppearanceState state)
    {
        var packet = state.Definition.Packet.ToArray();
        if (packet.Length < WorldObjectTemplateOffset)
        {
            throw new ArgumentException(
                $"Monster {state.Definition.ObjectId} appearance packet is too short.",
                nameof(state));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), state.CurrentHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24, 4), state.MaximumHealth);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28, 4), state.X);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36, 4), state.Z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40, 4), state.Facing);
        return packet;
    }

    public static byte[] MonsterMovementStart(
        uint objectId,
        float x,
        float y,
        float z,
        float velocityX,
        float velocityY,
        float velocityZ,
        uint movementMode = 1)
    {
        var packet = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), MonsterMovementStartOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        // Offset 8 is zero in every captured idle-roaming packet.
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), movementMode);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(20, 4), y);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(24, 4), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28, 4), velocityX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32, 4), velocityY);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36, 4), velocityZ);
        return packet;
    }

    public static byte[] MonsterMovementEnd(
        uint objectId,
        uint tickCount,
        float x,
        float y,
        float z,
        float facing)
    {
        var packet = new byte[34];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), MonsterMovementEndOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        // Offset 8 and the trailing UInt16 at offset 32 are zero in the capture.
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), tickCount);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(20, 4), y);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(24, 4), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28, 4), facing);
        return packet;
    }

    public static byte[] MonsterLifecycleMarker(uint objectId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), MonsterLifecycleMarkerOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        return packet;
    }
}
