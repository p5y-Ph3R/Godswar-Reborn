using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    /// <summary>
    /// Builds the exact native Samsara completion frame. The installed client
    /// renders the six result bytes at offsets 4-9 as hundredth-unit Growth
    /// increases, in native stat order. Offset 12 updates the pet's next-level
    /// requirement; offsets 10-11 are reserved.
    /// </summary>
    public static byte[] PetRebirth(
        PetRebirthGrowthEvidence growth,
        int nextLevelExperience)
    {
        ArgumentNullException.ThrowIfNull(growth);
        if (!growth.IsValid)
        {
            throw new InvalidDataException(
                "The native rebirth Growth result is not representable.");
        }
        if (nextLevelExperience < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextLevelExperience));
        }

        const ushort length = 16;
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetRebirthResult);
        packet[4] = ToGrowthHundredths(growth.Increase.Agility);
        packet[5] = ToGrowthHundredths(growth.Increase.Strength);
        packet[6] = ToGrowthHundredths(growth.Increase.Accuracy);
        packet[7] = ToGrowthHundredths(growth.Increase.Technique);
        packet[8] = ToGrowthHundredths(growth.Increase.Wisdom);
        packet[9] = ToGrowthHundredths(growth.Increase.Luck);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12),
            nextLevelExperience);
        return packet;
    }

    private static byte ToGrowthHundredths(decimal value)
    {
        const decimal minimum = 0.01m;
        const decimal maximum = 0.20m;
        const decimal scale = 100m;
        var scaled = value * scale;
        if (value is < minimum or > maximum ||
            scaled != decimal.Truncate(scaled))
        {
            throw new InvalidDataException(
                "The native rebirth Growth result is not representable.");
        }
        return decimal.ToByte(scaled);
    }
}
