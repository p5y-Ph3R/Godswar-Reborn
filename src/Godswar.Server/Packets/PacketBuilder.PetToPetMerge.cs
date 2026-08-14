using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] PetToPetMergeResult(
        long primaryPetId,
        long deputyPetId,
        PetToPetMergeDelta delta)
    {
        if (primaryPetId is <= 0 or > int.MaxValue ||
            deputyPetId is <= 0 or > int.MaxValue ||
            primaryPetId == deputyPetId ||
            !delta.IsValid)
        {
            throw new InvalidDataException(
                "The native pet Merge result is not representable.");
        }

        var packet = new byte[38];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 38);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetToPetMergeResult);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            checked((int)primaryPetId));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            checked((int)deputyPetId));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), delta.Agility);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16), delta.Strength);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20), delta.Accuracy);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24), delta.Technique);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(28), delta.Wisdom);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(32), delta.Luck);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(36), delta.Rank);
        return packet;
    }
}
