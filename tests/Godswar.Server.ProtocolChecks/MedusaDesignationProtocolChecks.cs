using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaDesignationProtocolChecks
{
    public const string CheckName = "Medusa owned-title dialog protocol";

    public static Task RunAsync()
    {
        var packet = PacketBuilder.MedusaDesignationInfo(
            selectedTitleId: 5152,
            ownedTitleIds: [5152, 5011]);

        Check.Equal(36, packet.Length, "two-title designation packet length");
        Check.Equal(
            (ushort)packet.Length,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "designation packet declares its exact length");
        Check.Equal(
            Opcodes.DesignationInfo,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "designation packet uses the native owned-title opcode");
        Check.Equal(
            5152U,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)),
            "designation header carries the selected title");
        Check.Equal(
            2,
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)),
            "designation header carries the owned-title count");

        AssertPermanentTitle(packet, offset: 12, expectedTitleId: 5011);
        AssertPermanentTitle(packet, offset: 24, expectedTitleId: 5152);

        var empty = PacketBuilder.MedusaDesignationInfo(0, []);
        Check.Equal(12, empty.Length, "empty ownership remains a clearing frame");
        Check.Equal(
            0,
            BinaryPrimitives.ReadInt32LittleEndian(empty.AsSpan(8)),
            "empty ownership declares zero records");
        return Task.CompletedTask;
    }

    private static void AssertPermanentTitle(
        byte[] packet,
        int offset,
        uint expectedTitleId)
    {
        Check.Equal(
            expectedTitleId,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset)),
            "designation record carries the exact title ID");
        Check.Equal(
            (byte)4,
            packet[offset + 4],
            "Medusa title uses the client's special-title category");
        Check.Equal(
            (byte)1,
            packet[offset + 5],
            "Medusa title is permanent");
        Check.Equal(
            0,
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset + 8)),
            "permanent title carries no expiry countdown");
    }
}
