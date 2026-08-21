using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetOwnerMergeLifecyclePacketChecks
{
    public static Task RunAsync()
    {
        AssertPacket(
            PacketBuilder.PetOwnerMergeStarted(
                0x1448,
                PetAptitude.Smart,
                completedRebirths: 30),
            Opcodes.PetOwnerMergeStarted,
            0x1448,
            "Merge start");
        AssertPacket(
            PacketBuilder.PetOwnerMergeEnded(0x1448),
            Opcodes.PetOwnerMergeEnded,
            0x1448,
            "Merge end");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetOwnerMergeStarted(
                0,
                PetAptitude.Smart,
                completedRebirths: 30),
            "Merge start rejects a zero player object ID");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetOwnerMergeStarted(
                0x1448,
                0,
                completedRebirths: 30),
            "Merge start rejects an unknown aptitude");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetOwnerMergeStarted(
                0x1448,
                PetAptitude.Smart,
                completedRebirths: 101),
            "Merge start rejects an unsupported rebirth count");
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
        var expectedLength = opcode == Opcodes.PetOwnerMergeStarted ? 10 : 8;
        Check.Equal(expectedLength, packet.Length, $"{label} packet length");
        Check.Equal(
            (ushort)expectedLength,
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
        if (opcode == Opcodes.PetOwnerMergeStarted)
        {
            Check.Equal(
                (byte)PetAptitude.Smart,
                packet[8],
                $"{label} aptitude");
            Check.Equal((byte)30, packet[9], $"{label} completed rebirths");
        }
    }
}
