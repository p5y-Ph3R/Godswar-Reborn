using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private const short FairyFeatherSlot = 85;

    private static async Task AssertLegacyPetBasicSavvyPreviewAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        CommandSubject subject,
        CommandConnectionCorrelation rawCorrelation,
        long petId)
    {
        await SeedFairyBasicSavvyResetAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var before = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            before.CompletedPetMerges == 1 &&
            before.BasicTotal == before.HatchBaselineTotal + 6m &&
            before.BirthValues.SequenceEqual(before.RarityValues),
            "Fairy fixture has immutable hatch provenance and exact Merge gains");

        var firstIdentity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        var firstEnvelope = PlayerOwnershipTestFences.Bind(
            PetBasicSavvyResetCommandEnvelope.CreateRawLocal(
                subject,
                rawCorrelation,
                DateTimeOffset.UtcNow,
                new PetBasicSavvyResetCommand(firstIdentity)));
        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(firstEnvelope),
            executor.ExecuteAsync(firstEnvelope));
        AssertCommitAndDuplicate(
            concurrent,
            PetDurableReceiptStatus.PetBasicSavvyPreviewed,
            "concurrent Fairy Basic-Savvy preview");
        var firstReceipt = concurrent.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var firstPreview = firstReceipt.BasicSavvyPreview ??
            throw new InvalidDataException(
                "Fairy Basic-Savvy preview receipt is missing values.");
        var afterFirstPreview = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            firstReceipt.KitBagSlot == FairyFeatherSlot &&
            firstReceipt.PetId == petId &&
            firstPreview.ToOrderedValues().Sum() == before.BasicTotal &&
            SamePetBasicSavvyValues(afterFirstPreview, before) &&
            afterFirstPreview.FeatherStack == 6 &&
            afterFirstPreview.InventoryRevision ==
                before.InventoryRevision + 1 &&
            afterFirstPreview.BasicSavvyAuditCount ==
                before.BasicSavvyAuditCount + 1 &&
            afterFirstPreview.PendingPreviewOperationId ==
                firstIdentity.OperationId &&
            afterFirstPreview.PendingExpectedTotal == before.BasicTotal &&
            afterFirstPreview.PendingPolicyVersion ==
                PetBasicSavvyRedistributionPolicy.Version &&
            afterFirstPreview.EvidenceCount == before.EvidenceCount + 1,
            "Reset consumes one Fairy Feather and stores a fenced preview without mutating Basic");

        var replay = await restarted.ExecuteAsync(firstEnvelope);
        var replayed = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == firstReceipt &&
            SameBasicSavvyResetState(replayed, afterFirstPreview),
            "duplicate Fairy Reset replays without another roll or consumption");

        var secondIdentity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        var secondEnvelope = PlayerOwnershipTestFences.Bind(
            PetBasicSavvyResetCommandEnvelope.CreateRawLocal(
                subject,
                rawCorrelation,
                DateTimeOffset.UtcNow,
                new PetBasicSavvyResetCommand(secondIdentity)));
        var secondResult = await executor.ExecuteAsync(secondEnvelope);
        var secondPreview = secondResult.Receipt?.BasicSavvyPreview ??
            throw new InvalidDataException(
                "Second Fairy Basic-Savvy preview is missing values.");
        var afterSecondPreview = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            secondResult.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            SamePetBasicSavvyValues(afterSecondPreview, before) &&
            afterSecondPreview.FeatherStack == 5 &&
            afterSecondPreview.PendingPreviewOperationId ==
                secondIdentity.OperationId,
            "a deliberate second Fairy Reset replaces only the pending preview");

        var staleAccept = await executor.ExecuteAsync(
            CreateBasicSavvyAcceptEnvelope(
                subject,
                rawCorrelation,
                firstIdentity.OperationId));
        var afterStaleAccept = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            staleAccept.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            staleAccept.Receipt?.Status ==
                PetDurableReceiptStatus.PetBasicSavvyPreviewUnavailable &&
            SameBasicSavvyResetState(
                afterStaleAccept,
                afterSecondPreview),
            "stale Fairy OK cannot apply or delete the newer preview");

        var wrongCorrelation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var wrongSessionAccept = await executor.ExecuteAsync(
            CreateBasicSavvyAcceptEnvelope(
                subject,
                wrongCorrelation,
                secondIdentity.OperationId));
        var afterWrongSession = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            wrongSessionAccept.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            wrongSessionAccept.Receipt?.Status ==
                PetDurableReceiptStatus.PetBasicSavvyPreviewUnavailable &&
            SameBasicSavvyResetState(
                afterWrongSession,
                afterSecondPreview),
            "a different connection cannot accept or delete the Fairy preview");

        var acceptEnvelope = CreateBasicSavvyAcceptEnvelope(
            subject,
            rawCorrelation,
            secondIdentity.OperationId);
        var accepted = await executor.ExecuteAsync(acceptEnvelope);
        var afterAccept = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            accepted.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            accepted.Receipt?.Status ==
                PetDurableReceiptStatus.PetBasicSavvyAccepted &&
            afterAccept.BasicValues.SequenceEqual(
                secondPreview.ToOrderedValues()) &&
            afterAccept.BasicTotal == before.BasicTotal &&
            afterAccept.BirthValues.SequenceEqual(before.BirthValues) &&
            afterAccept.RarityValues.SequenceEqual(before.RarityValues) &&
            afterAccept.HatchBaselineTotal == before.HatchBaselineTotal &&
            afterAccept.CompletedPetMerges == before.CompletedPetMerges &&
            afterAccept.BasicSavvyAuditCount ==
                before.BasicSavvyAuditCount + 3 &&
            afterAccept.LatestAuditPolicyVersion ==
                PetBasicSavvyRedistributionPolicy.Version &&
            afterAccept.LatestAuditReasonCode ==
                "fairy_basic_savvy_accept" &&
            afterAccept.LatestAuditMergeGainTotal == 6m &&
            afterAccept.LatestAuditExpectedTotal == before.BasicTotal &&
            afterAccept.PetRevision == before.PetRevision + 1 &&
            afterAccept.InventoryRevision ==
                afterSecondPreview.InventoryRevision &&
            afterAccept.FeatherStack == 5 &&
            afterAccept.PendingPreviewOperationId is null &&
            afterAccept.StatRevisions.Zip(
                before.StatRevisions,
                static (current, previous) => current == previous + 1)
                .All(static value => value),
            "OK atomically redistributes Basic while preserving hatch and Merge provenance");

        var acceptedReplay = await restarted.ExecuteAsync(acceptEnvelope);
        var afterAcceptReplay = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            acceptedReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            acceptedReplay.Receipt == accepted.Receipt &&
            SameBasicSavvyResetState(afterAcceptReplay, afterAccept),
            "duplicate Fairy OK replays without applying Basic twice");

        var afterLifecycle = await AssertPetBasicSavvyPreviewLifecycleAsync(
            dataSource,
            executor,
            subject,
            rawCorrelation,
            petId,
            afterAccept);
        await DeleteFairyFeathersAsync(dataSource, subject.CharacterId);
        var missingIdentity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        var missing = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetBasicSavvyResetCommandEnvelope.CreateRawLocal(
                    subject,
                    rawCorrelation,
                    DateTimeOffset.UtcNow,
                    new PetBasicSavvyResetCommand(missingIdentity))));
        var unchanged = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            missing.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            missing.Receipt?.Status ==
                PetDurableReceiptStatus.FairyFeatherNotFound &&
            unchanged.FeatherStack == 0 &&
            SameBasicSavvyResetState(
                unchanged,
                afterLifecycle,
                compareFeatherStack: false),
            "missing Fairy Feather cannot mutate or reroll Basic");
    }

    private static CommandEnvelope<PetBasicSavvyResetCommand>
        CreateBasicSavvyAcceptEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation rawCorrelation,
            Guid previewOperationId)
    {
        var identity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        return PlayerOwnershipTestFences.Bind(
            PetBasicSavvyResetCommandEnvelope.CreateRawLocal(
                subject,
                rawCorrelation,
                DateTimeOffset.UtcNow,
                new PetBasicSavvyResetCommand(
                    identity,
                    PetBasicSavvyResetOperation.Accept,
                    previewOperationId)));
    }
}
