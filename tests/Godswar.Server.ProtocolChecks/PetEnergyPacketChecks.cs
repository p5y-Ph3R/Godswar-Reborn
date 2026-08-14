using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class PetEnergyPacketChecks
{
    public static Task RunAsync()
    {
        AssertEnergy(100, 100, 1_800, "full normalized energy");
        AssertEnergy(50, 100, 900, "half normalized energy");
        AssertEnergy(1, 100, 18, "one normalized energy point");
        AssertEnergy(0, 100, 0, "empty normalized energy");
        AssertEnergy(1, 3, 600, "non-100 maximum is proportional");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetEnergy(-1, 100),
            "negative pet energy is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetEnergy(101, 100),
            "pet energy above maximum is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetEnergy(0, 0),
            "non-positive pet maximum energy is rejected");
        return Task.CompletedTask;
    }

    private static void AssertEnergy(
        int current,
        int maximum,
        uint expectedNative,
        string label)
    {
        var packet = PacketBuilder.PetEnergy(current, maximum);
        Check.Equal(8, packet.Length, $"{label} packet length");
        Check.Equal(
            (ushort)8,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            $"{label} declared length");
        Check.Equal(
            Opcodes.PetEnergy,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            $"{label} opcode");
        Check.Equal(
            expectedNative,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)),
            $"{label} native value");
    }
}
