using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetOwnerMergeProjectionChecks
{
    private static async Task CheckMergeStartProjectionAsync()
    {
        var pet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 10) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = true
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [pet],
            Executor(PetDurableReceiptStatus.OwnerMerged, pet));

        await fixture.InvokeAsync(Request(Guid.NewGuid()));
        var packets = fixture.Transport.ReadLegacyPackets();
        var energyIndex = packets.FindIndex(packet => Opcode(packet) ==
            Opcodes.PetEnergy);
        var statusIndex = packets.FindIndex(packet => Opcode(packet) ==
            0x27B6);
        var startIndex = packets.FindIndex(packet => Opcode(packet) ==
            Opcodes.PetOwnerMergeStarted);
        Check.True(
            energyIndex >= 0 && statusIndex > energyIndex &&
            startIndex > statusIndex,
            "committed Merge sends energy and status before the native unite effect");
        Check.True(
            packets.All(packet => Opcode(packet) != 10237),
            "committed Merge never rebuilds the active-pet list");
        Check.True(
            packets.Any(packet => Opcode(packet) == 0x27B6),
            "committed Merge refreshes authoritative character stats");
        var start = packets.Single(packet => Opcode(packet) ==
            Opcodes.PetOwnerMergeStarted);
        var energy = packets[energyIndex];
        Check.Equal(
            1_800u,
            BinaryPrimitives.ReadUInt32LittleEndian(energy.AsSpan(4)),
            "full normalized Merge energy projects as native 1800");
        Check.Equal(
            0x1448u,
            BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(4)),
            "self Merge start uses the native local-player object ID");
    }
}
