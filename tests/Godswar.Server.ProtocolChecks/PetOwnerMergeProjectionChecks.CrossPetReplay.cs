using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetOwnerMergeProjectionChecks
{
    private static async Task
        CheckHistoricalReceiptPreservesDifferentActivePetAsync()
    {
        var historicalPet = PetPresenceProtocolChecks.CreatePet(
            isCarried: false,
            isSummoned: false,
            revision: 20) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = false
        };
        var activePet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 30) with
        {
            PetId = 2,
            Name = "Rock Elf B",
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            CurrentEnergy = 73,
            MaximumEnergy = 100,
            ContributesToCharacter = true
        };
        var executor = new OwnerMergeLifecycleTestExecutor
        {
            ToggleOwnerMerge = envelope =>
                PetDurableExecutionResult.Duplicate(
                    HistoricalStartReceipt(envelope, historicalPet))
        };
        var character = CreateCharacter();
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [historicalPet, activePet],
            executor,
            petOwnerMergeEnergyInterval:
                TimeSpan.FromMilliseconds(200));
        fixture.Registry.JoinMap(
            fixture.Session,
            AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));

        await fixture.InvokeAsync(Request(Guid.NewGuid()));

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.True(
            packets.Any(packet => packet.SequenceEqual(
                PacketBuilder.PetEnergy(73, 100))) &&
            packets.Any(packet => Opcode(packet) ==
                Opcodes.PetOwnerMergeStarted) &&
            packets.All(packet => Opcode(packet) !=
                Opcodes.PetOwnerMergeEnded) &&
            packets.All(packet => Opcode(packet) != 10237),
            "historical pet-A receipt projects current merged pet B without ending or rebuilding it");
        var context = fixture.Registry
            .GetMapSessions(character.CurrentMap)
            .Single(value => value.Session == fixture.Session);
        Check.True(
            context.PetOwnerMergeActive,
            "historical pet-A receipt preserves pet B Merge presentation");

        await Task.Delay(260);
        Check.Equal(
            1,
            executor.DrainCount,
            "historical pet-A receipt starts pet B's current Merge timer");
        fixture.Registry.Remove(fixture.Session);
    }

    private static PetDurableReceipt HistoricalStartReceipt(
        CommandEnvelope<PetOwnerMergeToggleCommand> envelope,
        PetBootstrapSnapshot historicalPet) =>
        new(
            CommandFamily.PetOwnerMergeToggle,
            PetDurableReceiptStatus.OwnerMerged,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            KitBagSlot: -1,
            EquipmentSlot: -1,
            PetId: historicalPet.PetId,
            PetLevel: historicalPet.Level,
            PetExperience: historicalPet.Experience,
            PetRevision: historicalPet.Revision,
            IsCarried: historicalPet.IsCarried,
            IsSummoned: historicalPet.IsSummoned,
            PresenceOperation: 0,
            AggregateRevision: 20,
            AuditReference: "owner-merge-cross-pet-replay",
            OutboxEventId: Guid.NewGuid());
}
