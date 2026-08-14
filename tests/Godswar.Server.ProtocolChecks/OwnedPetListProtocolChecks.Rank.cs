using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class OwnedPetListProtocolChecks
{
    private static void CheckRankWireSafety()
    {
        var pet = CreateGodlyKingLion() with
        {
            Rank = PetRankWirePolicy.MaximumRank
        };
        var packet = PacketBuilder.OwnedPetList(
            PetContentTestCatalog.Instance,
            [pet],
            openedCellCount: 2);
        Check.Equal(
            ushort.MaxValue,
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(8 + 0x9C, sizeof(ushort))),
            "opcode 10237 preserves the exact maximum native pet rank");

        foreach (var invalidRank in new[] { 655.36m, 1.001m })
        {
            Check.Throws<InvalidDataException>(
                () => PacketBuilder.OwnedPetList(
                    PetContentTestCatalog.Instance,
                    [pet with { Rank = invalidRank }],
                    openedCellCount: 2),
                $"opcode 10237 rejects native-wire-unsafe pet rank {invalidRank}");
        }
    }
}
