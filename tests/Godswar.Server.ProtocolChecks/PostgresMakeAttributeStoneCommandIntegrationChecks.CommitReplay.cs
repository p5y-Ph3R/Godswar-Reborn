using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresMakeAttributeStoneCommandIntegrationChecks
{
    private static async Task AssertCommitReplayAndConflictAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "atomic");
        var operationId = Guid.NewGuid();
        var firstEnvelope = CreateEnvelope(fixture, operationId);

        MakeAttributeStoneExecutionResult first;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            first = await CreateExecutor(source)
                .ExecuteAsync(firstEnvelope);
        }

        var committed = RequireReceipt(
            first,
            MakeAttributeStoneExecutionDisposition.Committed,
            "first Make Attribute Stone transaction");
        Check.True(
            committed.CharacterId == fixture.CharacterId &&
            committed.Status ==
                MakeAttributeStoneResultStatus.Succeeded &&
            committed.NativeResultSubId ==
                MakeAttributeStoneNativeResults.SucceededSubId &&
            committed.SelectedKitBagSlot == SelectedKitBagSlot &&
            committed.SourceDustItemId == DustItemId &&
            committed.OutputStoneItemId == AttributeStoneItemId &&
            committed.IsBound == fixture.IsBound &&
            committed.InventoryRevision == 1 &&
            long.TryParse(
                committed.AuditReference,
                out var auditReference) &&
            auditReference > 0 &&
            committed.OutboxEventId.HasValue &&
            committed.OutboxEventId.Value != Guid.Empty,
            "first Make Attribute Stone returns a complete receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedDuplicateCount: 0,
            expectedConflictCount: 0,
            "first transaction atomically consumes 99 Dust and " +
            "commits one bound Attribute Stone");

        var retryEnvelope = CreateEnvelope(
            fixture,
            operationId,
            connectionId: Guid.NewGuid());
        Check.True(
            string.Equals(
                firstEnvelope.OperationId,
                retryEnvelope.OperationId,
                StringComparison.Ordinal) &&
            string.Equals(
                firstEnvelope.RequestHash,
                retryEnvelope.RequestHash,
                StringComparison.Ordinal),
            "a reconnect preserves operation identity and request hash");

        MakeAttributeStoneExecutionResult retry;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            retry = await CreateExecutor(source)
                .ExecuteAsync(retryEnvelope);
        }

        var duplicate = RequireReceipt(
            retry,
            MakeAttributeStoneExecutionDisposition.Duplicate,
            "exact Make Attribute Stone retry");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "exact retry returns the canonical stored receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedDuplicateCount: 1,
            expectedConflictCount: 0,
            "exact retry does not consume Dust or publish twice");

        MakeAttributeStoneExecutionResult replay;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            replay = await CreateExecutor(source).TryReplayAsync(
                new CommandSubject(
                    fixture.AccountId,
                    fixture.CharacterId),
                PlayerOwnershipTestFences.ForCharacter(
                    fixture.CharacterId),
                operationId);
        }

        var replayed = RequireReceipt(
            replay,
            MakeAttributeStoneExecutionDisposition.Duplicate,
            "selection-free Make Attribute Stone replay");
        AssertReceiptsEqual(
            committed,
            replayed,
            "replay lookup needs no ephemeral NPC selection context");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedDuplicateCount: 2,
            expectedConflictCount: 0,
            "selection-free replay changes only bounded duplicate evidence");

        MakeAttributeStoneExecutionResult conflict;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            conflict = await CreateExecutor(source).ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    operationId,
                    GearMentorMakeAttributeStoneCommandEnvelope
                        .AthensGearMentorNpcId,
                    Guid.NewGuid()));
        }

        Check.True(
            conflict.Disposition ==
                MakeAttributeStoneExecutionDisposition
                    .RequestHashConflict &&
            conflict.Receipt is null,
            "same operation UUID with a different request is rejected");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedDuplicateCount: 2,
            expectedConflictCount: 1,
            "request conflict changes only bounded conflict evidence");
    }

    private static async Task AssertConcurrentExecutorsAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "race");
        var operationId = Guid.NewGuid();
        var envelopeA = CreateEnvelope(
            fixture,
            operationId,
            connectionId: Guid.NewGuid());
        var envelopeB = CreateEnvelope(
            fixture,
            operationId,
            connectionId: Guid.NewGuid());

        await using var sourceA =
            NpgsqlDataSource.Create(connectionString);
        await using var sourceB =
            NpgsqlDataSource.Create(connectionString);
        var results = await Task.WhenAll(
            CreateExecutor(sourceA).ExecuteAsync(envelopeA),
            CreateExecutor(sourceB).ExecuteAsync(envelopeB));

        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                MakeAttributeStoneExecutionDisposition.Committed),
            "one concurrent Make Attribute Stone executor commits");
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                MakeAttributeStoneExecutionDisposition.Duplicate),
            "one concurrent Make Attribute Stone executor replays");
        var committed = results.Single(result =>
            result.Disposition ==
            MakeAttributeStoneExecutionDisposition.Committed).Receipt ??
            throw new InvalidOperationException(
                "The concurrent winner returned no receipt.");
        var duplicate = results.Single(result =>
            result.Disposition ==
            MakeAttributeStoneExecutionDisposition.Duplicate).Receipt ??
            throw new InvalidOperationException(
                "The concurrent replay returned no receipt.");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "concurrent executors observe one canonical receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedDuplicateCount: 1,
            expectedConflictCount: 0,
            "concurrent execution produces one recipe and one event");
    }

    private static void AssertCommittedState(
        StoneDurableState state,
        int expectedDuplicateCount,
        int expectedConflictCount,
        string description)
    {
        Check.True(
            state.InventoryRevision == 1 &&
            state.DustQuantity == 0 &&
            state.StoneQuantity == 1 &&
            state.StoneItemCount == 1 &&
            state.StoneBound == 1 &&
            state.StoneSlot == SelectedKitBagSlot &&
            state.TotalBagItemCount == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 1 &&
            state.RecipeLedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == expectedDuplicateCount &&
            state.ConflictCount == expectedConflictCount &&
            state.CommittedInboxCount == 1 &&
            state.RejectedInboxCount == 0 &&
            state.IsReconciled,
            description);
    }

    private static void AssertInitialState(
        StoneDurableState state,
        short expectedDustQuantity,
        string description)
    {
        Check.True(
            state.InventoryRevision == 0 &&
            state.DustQuantity == expectedDustQuantity &&
            state.StoneQuantity == 0 &&
            state.StoneItemCount == 0 &&
            state.StoneBound == -1 &&
            state.StoneSlot == -1 &&
            state.TotalBagItemCount == 1 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.LedgerCount == 0 &&
            state.RecipeLedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.ConflictCount == 0 &&
            state.CommittedInboxCount == 0 &&
            state.RejectedInboxCount == 0 &&
            state.IsReconciled,
            description);
    }
}
