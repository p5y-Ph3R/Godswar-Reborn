using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int NativePetMaximumEnergy = 1_800;

    /// <summary>
    /// Projects the authoritative normalized pet-energy value into the stock
    /// client's 0..1800 current-energy field.
    /// </summary>
    public static byte[] PetEnergy(
        int currentEnergy,
        int maximumEnergy)
    {
        if (maximumEnergy <= 0 ||
            currentEnergy < 0 ||
            currentEnergy > maximumEnergy)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentEnergy),
                "Pet energy must be between zero and its positive maximum.");
        }

        var nativeEnergy = checked((uint)(
            ((long)currentEnergy * NativePetMaximumEnergy +
             (maximumEnergy / 2L)) /
            maximumEnergy));
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetEnergy);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            nativeEnergy);
        return packet;
    }
}
