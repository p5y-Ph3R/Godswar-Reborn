using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    /// <summary>
    /// Native 10271 stores this absolute stage byte directly in the active
    /// pet bean. Stage 1..6 corresponds to Contract Spirit count 0..5.
    /// </summary>
    public static byte[] PetSoulContract(byte stage)
    {
        if (stage is < 1 or > PetSoulContractPolicy.MaximumStage)
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        const ushort length = 5;
        var packet = new byte[length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetSoulContractResult);
        packet[4] = stage;
        return packet;
    }
}
