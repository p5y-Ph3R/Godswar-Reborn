using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertPetBasicSavvyResetAsync(
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
            "Fairy fixture starts with immutable hatch provenance and Merge gains");

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
            PetDurableReceiptStatus.PetBasicSavvyAccepted,
            "concurrent one-phase Fairy Basic-Savvy reset");
        var firstReceipt = concurrent.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var firstRoll = firstReceipt.BasicSavvyPreview ??
            throw new InvalidDataException(
                "One-phase Fairy receipt is missing its committed roll.");
        var afterFirst = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            firstReceipt.KitBagSlot == FairyFeatherSlot &&
            firstReceipt.PetId == petId &&
            firstReceipt.PetRevision == before.PetRevision + 1 &&
            afterFirst.BasicValues.SequenceEqual(
                firstRoll.ToOrderedValues()) &&
            afterFirst.BasicTotal == before.BasicTotal &&
            afterFirst.BirthValues.SequenceEqual(before.BirthValues) &&
            afterFirst.RarityValues.SequenceEqual(before.RarityValues) &&
            afterFirst.CompletedPetMerges == before.CompletedPetMerges &&
            afterFirst.FeatherStack == before.FeatherStack - 1 &&
            afterFirst.InventoryRevision == before.InventoryRevision + 1 &&
            afterFirst.PetRevision == before.PetRevision + 1 &&
            RevisionsAdvancedOnce(
                afterFirst.StatRevisions,
                before.StatRevisions) &&
            afterFirst.BasicSavvyAuditCount ==
                before.BasicSavvyAuditCount + 1 &&
            afterFirst.ResetLedgerCount == before.ResetLedgerCount + 1 &&
            afterFirst.LatestAuditPolicyVersion ==
                PetBasicSavvyRedistributionPolicy.Version &&
            afterFirst.LatestAuditReasonCode ==
                "fairy_basic_savvy_reset" &&
            afterFirst.LatestAuditMergeGainTotal == 6m &&
            afterFirst.LatestAuditExpectedTotal == before.BasicTotal &&
            Enum.TryParse<PetSavvyStat>(
                afterFirst.LatestAuditTertiaryFocus,
                out _) &&
            Enum.TryParse<PetSavvyStat>(
                afterFirst.LatestAuditQuaternaryFocus,
                out _) &&
            afterFirst.PendingPreviewOperationId is null &&
            afterFirst.EvidenceCount == before.EvidenceCount + 1,
            "Reset atomically consumes one Feather and commits one exact-total roll");

        var replay = await restarted.ExecuteAsync(firstEnvelope);
        var afterReplay = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == firstReceipt &&
            SameBasicSavvyResetState(afterReplay, afterFirst) &&
            afterReplay.EvidenceCount == afterFirst.EvidenceCount,
            "duplicate Reset replays without consuming or rolling twice");

        var secondIdentity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        var secondEnvelope = PlayerOwnershipTestFences.Bind(
            PetBasicSavvyResetCommandEnvelope.CreateRawLocal(
                subject,
                rawCorrelation,
                DateTimeOffset.UtcNow,
                new PetBasicSavvyResetCommand(secondIdentity)));
        var second = await executor.ExecuteAsync(secondEnvelope);
        var secondRoll = second.Receipt?.BasicSavvyPreview ??
            throw new InvalidDataException(
                "Second one-phase Fairy receipt is missing its roll.");
        var afterSecond = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            second.Disposition == PetDurableExecutionDisposition.Committed &&
            second.Receipt?.Status ==
                PetDurableReceiptStatus.PetBasicSavvyAccepted &&
            afterSecond.BasicValues.SequenceEqual(
                secondRoll.ToOrderedValues()) &&
            afterSecond.BasicTotal == before.BasicTotal &&
            afterSecond.FeatherStack == afterFirst.FeatherStack - 1 &&
            afterSecond.InventoryRevision ==
                afterFirst.InventoryRevision + 1 &&
            afterSecond.PetRevision == afterFirst.PetRevision + 1 &&
            RevisionsAdvancedOnce(
                afterSecond.StatRevisions,
                afterFirst.StatRevisions) &&
            afterSecond.BasicSavvyAuditCount ==
                afterFirst.BasicSavvyAuditCount + 1 &&
            afterSecond.ResetLedgerCount ==
                afterFirst.ResetLedgerCount + 1 &&
            afterSecond.PendingPreviewOperationId is null &&
            afterSecond.EvidenceCount == afterFirst.EvidenceCount + 1,
            "a distinct Reset commits and consumes exactly once again");

        var retiredAccept = await executor.ExecuteAsync(
            CreateBasicSavvyAcceptEnvelope(
                subject,
                rawCorrelation,
                firstIdentity.OperationId));
        var afterRetiredAccept = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            retiredAccept.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            retiredAccept.Receipt?.Status ==
                PetDurableReceiptStatus.PetBasicSavvyPreviewUnavailable &&
            SameBasicSavvyResetState(afterRetiredAccept, afterSecond) &&
            afterRetiredAccept.EvidenceCount == afterSecond.EvidenceCount + 1,
            "retired OK is a durable non-mutating terminal response");

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
                afterRetiredAccept,
                compareFeatherStack: false),
            "missing Fairy Feather cannot mutate Basic Savvy");
    }

    private static bool RevisionsAdvancedOnce(
        IReadOnlyList<long> current,
        IReadOnlyList<long> previous) =>
        current.Count == previous.Count &&
        current.Zip(previous,
                static (next, prior) => next == prior + 1)
            .All(static advanced => advanced);

}
