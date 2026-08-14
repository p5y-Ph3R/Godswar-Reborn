using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    /// <summary>
    /// Native narrow pet-EXP projection recovered from the original server:
    /// length, opcode 10261, pet ID, and authoritative total EXP.
    /// </summary>
    public static byte[] PetExperience(long petId, long experience)
    {
        if (petId is <= 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(petId));
        }
        if (experience is < 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(experience));
        }

        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            checked((uint)petId));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            checked((uint)experience));
        return packet;
    }
}
