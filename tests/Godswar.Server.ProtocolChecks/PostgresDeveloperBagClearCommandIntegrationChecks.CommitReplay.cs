using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresDeveloperBagClearCommandIntegrationChecks
{
    private static async Task
        AssertCommitReplayAndLateItemSafetyAsync(
            string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "commit",
            bagSlots: [0, 7]);
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(fixture, operationId);

        DeveloperBagClearExecutionResult first;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            first = await CreateExecutor(source)
                .ExecuteAsync(envelope);
        }

        var committed = RequireReceipt(
            first,
            DeveloperBagClearExecutionDisposition.Committed,
            "first developer bag clear");
        Check.True(
            committed.CharacterId == fixture.CharacterId &&
            committed.RemovedSlots.SequenceEqual(
                new short[] { 0, 7 }) &&
            committed.InventoryRevision == 1,
            "bag clear returns the deleted slots and next revision");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedLedgerCount: 2,
            expectedItemAuditCount: 2,
            expectedDuplicateCount: 0,
            "bag clear deletes bag items and preserves equipment");

        var laterItemInstanceId = await InsertLateBagItemAsync(
            connectionString,
            fixture.CharacterId);

        DeveloperBagClearExecutionResult retry;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            retry = await CreateExecutor(source).ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    operationId,
                    connectionId: Guid.NewGuid()));
        }

        var duplicate = RequireReceipt(
            retry,
            DeveloperBagClearExecutionDisposition.Duplicate,
            "exact developer bag-clear retry");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "exact bag-clear retry returns the canonical receipt");

        var replayState = await ReadStateAsync(
            connectionString,
            fixture,
            laterItemInstanceId);
        Check.True(
            replayState.InventoryRevision == 1 &&
            replayState.BagItemCount == 1 &&
            replayState.LateItemCount == 1 &&
            replayState.EquipmentItemCount == 1 &&
            replayState.CommandAuditCount == 1 &&
            replayState.InboxCount == 1 &&
            replayState.LedgerCount == 2 &&
            replayState.ItemAuditCount == 2 &&
            replayState.OutboxCount == 1 &&
            replayState.DuplicateCount == 1,
            "replay never deletes an item added after the first commit");
    }

    private static async Task AssertConcurrentExactRetriesAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "race",
            bagSlots: [1, 9, 15]);
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
                DeveloperBagClearExecutionDisposition.Committed),
            "one concurrent bag-clear executor commits");
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                DeveloperBagClearExecutionDisposition.Duplicate),
            "one concurrent bag-clear executor replays");

        var committed = results.Single(result =>
            result.Disposition ==
            DeveloperBagClearExecutionDisposition.Committed).Receipt ??
            throw new InvalidOperationException(
                "The concurrent bag-clear winner returned no receipt.");
        var duplicate = results.Single(result =>
            result.Disposition ==
            DeveloperBagClearExecutionDisposition.Duplicate).Receipt ??
            throw new InvalidOperationException(
                "The concurrent bag-clear retry returned no receipt.");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "concurrent bag clears observe one canonical receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedLedgerCount: 3,
            expectedItemAuditCount: 3,
            expectedDuplicateCount: 1,
            "concurrent execution produces one clear and one replay");
    }

    private static async Task AssertEmptyBagTerminalReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "empty",
            bagSlots: []);
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(fixture, operationId);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(source).ExecuteAsync(
            envelope);

        Check.True(
            result.Disposition ==
                DeveloperBagClearExecutionDisposition
                    .PreconditionFailed &&
            result.Receipt is null,
            "empty authoritative bag rejects the clear");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 0 &&
            state.BagItemCount == 0 &&
            state.EquipmentItemCount == 1 &&
            state.CommandAuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 0 &&
            state.ItemAuditCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.IsReconciled,
            "empty-bag rejection persists terminal identity without " +
            "an inventory mutation");

        var laterItemInstanceId = await InsertLateBagItemAsync(
            connectionString,
            fixture.CharacterId);
        await using var retrySource =
            NpgsqlDataSource.Create(connectionString);
        var retry = await CreateExecutor(retrySource).ExecuteAsync(
            CreateEnvelope(
                fixture,
                operationId,
                connectionId: Guid.NewGuid()));
        Check.True(
            retry.Disposition ==
                DeveloperBagClearExecutionDisposition
                    .PreconditionFailed &&
            retry.Receipt is null,
            "same empty-bag UUID replays after an item is added");
        var replayState = await ReadStateAsync(
            connectionString,
            fixture,
            laterItemInstanceId);
        Check.True(
            replayState.InventoryRevision == 0 &&
            replayState.BagItemCount == 1 &&
            replayState.LateItemCount == 1 &&
            replayState.EquipmentItemCount == 1 &&
            replayState.CommandAuditCount == 1 &&
            replayState.InboxCount == 1 &&
            replayState.LedgerCount == 0 &&
            replayState.ItemAuditCount == 0 &&
            replayState.OutboxCount == 0 &&
            replayState.DuplicateCount == 1,
            "terminal empty-bag replay never deletes the later item");
    }

    private static void AssertCommittedState(
        ClearDurableState state,
        long expectedLedgerCount,
        long expectedItemAuditCount,
        int expectedDuplicateCount,
        string description)
    {
        Check.True(
            state.InventoryRevision == 1 &&
            state.BagItemCount == 0 &&
            state.EquipmentItemCount == 1 &&
            state.CommandAuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == expectedLedgerCount &&
            state.ItemAuditCount == expectedItemAuditCount &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == expectedDuplicateCount &&
            state.IsReconciled,
            description);
    }
}
