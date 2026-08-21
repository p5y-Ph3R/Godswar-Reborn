using System.Buffers.Binary;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int PetOwnerMergeStatePacketLength = 8;
    private const int PetOwnerMergeStartedPacketLength = 10;

    /// <summary>
    /// Starts the stock client's native pet-unite presentation. The player
    /// object ID is interpreted in the receiving client's world namespace.
    /// </summary>
    public static byte[] PetOwnerMergeStarted(
        uint ownerObjectId,
        PetAptitude aptitude,
        short completedRebirths)
    {
        if (ownerObjectId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerObjectId));
        }
        if (aptitude is < PetAptitude.Weak or > PetAptitude.Transcendent)
        {
            throw new ArgumentOutOfRangeException(nameof(aptitude));
        }
        if (completedRebirths is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(completedRebirths));
        }

        var packet = new byte[PetOwnerMergeStartedPacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            PetOwnerMergeStartedPacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetOwnerMergeStarted);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            ownerObjectId);
        packet[8] = checked((byte)aptitude);
        packet[9] = checked((byte)completedRebirths);
        return packet;
    }

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
