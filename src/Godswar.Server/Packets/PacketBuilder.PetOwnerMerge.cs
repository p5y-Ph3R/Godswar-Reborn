using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int PetOwnerMergeStatePacketLength = 8;

    /// <summary>
    /// Starts the stock client's native pet-unite presentation. The player
    /// object ID is interpreted in the receiving client's world namespace.
    /// </summary>
    public static byte[] PetOwnerMergeStarted(uint ownerObjectId) =>
        PetOwnerMergeState(
            Opcodes.PetOwnerMergeStarted,
            ownerObjectId,
            nameof(ownerObjectId));

    /// <summary>
    /// Ends the stock client's native pet-unite presentation.
    /// </summary>
    public static byte[] PetOwnerMergeEnded(uint ownerObjectId) =>
        PetOwnerMergeState(
            Opcodes.PetOwnerMergeEnded,
            ownerObjectId,
            nameof(ownerObjectId));

    private static byte[] PetOwnerMergeState(
        ushort opcode,
        uint value,
        string parameterName)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        var packet = new byte[PetOwnerMergeStatePacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            PetOwnerMergeStatePacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), value);
        return packet;
    }
}
