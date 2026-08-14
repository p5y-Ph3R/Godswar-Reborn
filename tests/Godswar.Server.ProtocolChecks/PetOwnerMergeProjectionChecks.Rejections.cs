using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetOwnerMergeProjectionChecks
{
    private static async Task CheckEnergyRejectionProjectsCurrentGaugeAsync()
    {
        var pet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 17) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            CurrentEnergy = 31,
            MaximumEnergy = 100
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [pet],
            Executor(
                PetDurableReceiptStatus.OwnerMergeEnergyNotFull,
                pet),
            hasLocalDevelopmentCapability: true);

        await fixture.InvokeAsync(Request(operationId: null));

        var packets = fixture.ReadLegacyPackets();
        Check.True(
            packets is [var energy] &&
            Opcode(energy) == Opcodes.PetEnergy &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                energy.AsSpan(4)) == 558,
            "energy-rejected raw owner-Merge projects current 31/100 gauge only");
        Check.True(
            packets.All(packet =>
                Opcode(packet) != Opcodes.PetOwnerMergeStarted &&
                Opcode(packet) != Opcodes.PetOwnerMergeEnded &&
                Opcode(packet) != 10237),
            "energy-rejected owner-Merge emits no lifecycle or pet-list packet");
    }
}
