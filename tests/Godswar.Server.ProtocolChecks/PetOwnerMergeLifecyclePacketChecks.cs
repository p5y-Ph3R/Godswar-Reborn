using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class PetOwnerMergeLifecyclePacketChecks
{
    public static Task RunAsync()
    {
        AssertPacket(
            PacketBuilder.PetOwnerMergeStarted(0x1448),
            Opcodes.PetOwnerMergeStarted,
            0x1448,
            "Merge start");
        AssertPacket(
            PacketBuilder.PetOwnerMergeEnded(0x1448),
            Opcodes.PetOwnerMergeEnded,
            0x1448,
            "Merge end");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetOwnerMergeStarted(0),
            "Merge start rejects a zero player object ID");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetOwnerMergeEnded(0),
            "Merge end rejects a zero player object ID");
        return Task.CompletedTask;
    }

    private static void AssertPacket(
        byte[] packet,
        ushort opcode,
        uint value,
        string label)
    {
        Check.Equal(8, packet.Length, $"{label} packet length");
        Check.Equal(
            (ushort)8,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            $"{label} encoded length");
        Check.Equal(
            opcode,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            $"{label} opcode");
        Check.Equal(
            value,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)),
            $"{label} value");
    }
}
