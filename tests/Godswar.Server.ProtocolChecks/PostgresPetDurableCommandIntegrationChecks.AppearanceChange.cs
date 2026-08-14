using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private const short MagicJadeSlot = 83;
    private const int CupidMagicJadeId = 11094;

    private static async Task AssertPetAppearanceChangeAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        GameplayItemContent itemContent,
        IPetContentCatalog petContent)
    {
        var fixture = await CreateFixtureAsync(connectionString);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var hatch = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                BagItemActivationCommandEnvelope.Create(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new BagItemActivationCommand(
                        Guid.NewGuid(),
                        fixture.EggSlot))));
        Check.True(
            hatch is
            {
                Disposition:
                    PetDurableExecutionDisposition.Committed,
                Receipt.Status: PetDurableReceiptStatus.EggHatched
            },
            "appearance fixture hatches one bound pet");
        var petId = hatch.Receipt!.PetId;

        var calledOut = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetPresenceTransitionCommandEnvelope.Create(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new PetPresenceTransitionCommand(
                        Guid.NewGuid(),
                        petId,
                        PetPresenceCommandOperation.CallOut))));
        Check.True(
            calledOut.Receipt is
            {
                Status: PetDurableReceiptStatus.PresenceChanged,
                IsSummoned: true
            },
            "appearance fixture summons the target pet");

        var jadeInstanceId = await SeedMagicJadeAsync(
            dataSource,
            fixture.CharacterId);
        var before = await ReadPetAppearanceStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            before.SpeciesId == 1 &&
            before.IsBound &&
            before.IsSummoned &&
            before.JadeStack == 2,
            "appearance fixture starts with a bound Rock Elf and two Cupid jades");

        var operationId = Guid.NewGuid();
        var envelope = PlayerOwnershipTestFences.Bind(
            PetAppearanceChangeCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new PetAppearanceChangeCommand(
                    PetCommandOperationIdentity.SecureClient(operationId),
                    MagicJadeSlot)));
        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(envelope),
            restarted.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(
            concurrent,
            PetDurableReceiptStatus.PetAppearanceChanged,
            "concurrent Magic Jade appearance change");
        var receipt = concurrent.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var evidence = receipt.AppearanceChange ??
            throw new InvalidDataException(
                "Magic Jade receipt is missing appearance evidence.");
        Check.True(
            receipt.PetId == petId &&
            evidence.IsValid &&
            evidence.OldSpeciesId == 1 &&
            evidence.NewSpeciesId == 45 &&
            evidence.MagicJadeItemId == CupidMagicJadeId &&
            evidence.MagicJadeItemInstanceId == jadeInstanceId &&
            evidence.KitBagSlot == MagicJadeSlot &&
            evidence.PetContentRevision == petContent.Revision.Sha256 &&
            evidence.ItemContentRevision ==
                itemContent.Templates.Revision.Sha256,
            "appearance receipt pins old/new species, selected jade, slot, and content revisions");

        var committed = await ReadPetAppearanceStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            committed.SpeciesId == 45 &&
            committed.PetRevision == before.PetRevision + 1 &&
            committed.JadeStack == 1 &&
            committed.InventoryRevision ==
                before.InventoryRevision + 1 &&
            SameAppearancePayload(before, committed) &&
            committed.CommandAuditCount == 1 &&
            committed.InboxCount == 1 &&
            committed.PetOutboxCount == 1 &&
            committed.InventoryLedgerCount == 1 &&
            committed.InventoryOutboxCount == 1 &&
            committed.CommittedOperationAuditCount == 1 &&
            committed.DuplicateCount == 1,
            "Magic Jade atomically changes species only and consumes one stack with complete durable evidence");
        await AssertPetAppearanceAuditEvidenceAsync(
            dataSource,
            receipt,
            operationId,
            jadeInstanceId,
            petContent.Revision.Sha256,
            itemContent.Templates.Revision.Sha256);

        var replay = await restarted.ExecuteAsync(envelope);
        var replayed = await ReadPetAppearanceStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == receipt &&
            SameAppearanceCommit(committed, replayed) &&
            replayed.DuplicateCount == 2,
            "appearance replay cannot consume or reapply the Magic Jade after restart");

        var sameSpecies = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetAppearanceChangeCommandEnvelope.Create(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new PetAppearanceChangeCommand(
                        PetCommandOperationIdentity.SecureClient(
                            Guid.NewGuid()),
                        MagicJadeSlot))));
        var rejected = await ReadPetAppearanceStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            sameSpecies.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            sameSpecies.Receipt?.Status ==
                PetDurableReceiptStatus.MagicJadeIncompatible &&
            SameAppearanceCommit(replayed, rejected) &&
            rejected.RejectedOperationAuditCount == 1,
            "same-species Magic Jade is terminal and consumes nothing");

        await AssertPetBindAsync(
            connectionString,
            dataSource,
            executor,
            restarted,
            petContent);
    }

    private static bool SameAppearancePayload(
        PetAppearanceDatabaseState before,
        PetAppearanceDatabaseState after) =>
        before.ImmutablePetJson == after.ImmutablePetJson &&
        before.StatValuesJson == after.StatValuesJson &&
        before.SkillsJson == after.SkillsJson &&
        before.CharacterBonusesJson == after.CharacterBonusesJson;

    private static bool SameAppearanceCommit(
        PetAppearanceDatabaseState expected,
        PetAppearanceDatabaseState actual) =>
        expected.SpeciesId == actual.SpeciesId &&
        expected.PetRevision == actual.PetRevision &&
        expected.JadeStack == actual.JadeStack &&
        expected.InventoryRevision == actual.InventoryRevision &&
        SameAppearancePayload(expected, actual) &&
        expected.InventoryLedgerCount == actual.InventoryLedgerCount &&
        expected.InventoryOutboxCount == actual.InventoryOutboxCount &&
        expected.PetOutboxCount == actual.PetOutboxCount &&
        expected.CommittedOperationAuditCount ==
            actual.CommittedOperationAuditCount;
}
