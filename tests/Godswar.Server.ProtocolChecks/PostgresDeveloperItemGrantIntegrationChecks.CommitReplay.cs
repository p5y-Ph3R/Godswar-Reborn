using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresDeveloperItemGrantIntegrationChecks
{
    private static async Task AssertCommitReplayAndConflictAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "atomic");
        var operationId = Guid.NewGuid();
        var firstEnvelope = CreateEnvelope(fixture, operationId);

        DeveloperItemGrantExecutionResult first;
        await using (var firstSource =
                     NpgsqlDataSource.Create(connectionString))
        {
            first = await CreateExecutor(firstSource)
                .ExecuteAsync(firstEnvelope);
        }

        var committed = RequireReceipt(
            first,
            DeveloperItemGrantExecutionDisposition.Committed,
            "first developer-item grant");
        Check.True(
            committed.CharacterId == fixture.CharacterId &&
            committed.ItemId == MaterialItemId &&
            committed.GrantedQuantity == GrantQuantity &&
            committed.InventoryRevision == 1 &&
            long.TryParse(
                committed.AuditReference,
                out var auditReference) &&
            auditReference > 0 &&
            committed.OutboxEventId != Guid.Empty,
            "first grant returns a complete durable receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedQuantity: GrantQuantity,
            expectedDuplicateCount: 0,
            expectedConflictCount: 0,
            "first grant atomically commits item, revision, evidence, " +
            "ledger, and outbox");

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
            "reconnect preserves the client operation and request hash");

        DeveloperItemGrantExecutionResult retry;
        await using (var retrySource =
                     NpgsqlDataSource.Create(connectionString))
        {
            retry = await CreateExecutor(retrySource)
                .ExecuteAsync(retryEnvelope);
        }

        var duplicate = RequireReceipt(
            retry,
            DeveloperItemGrantExecutionDisposition.Duplicate,
            "exact developer-item retry");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "exact retry recovers the canonical stored receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedQuantity: GrantQuantity,
            expectedDuplicateCount: 1,
            expectedConflictCount: 0,
            "exact retry creates no second item, revision, ledger, or " +
            "outbox row");

        DeveloperItemGrantExecutionResult conflict;
        await using (var conflictSource =
                     NpgsqlDataSource.Create(connectionString))
        {
            conflict = await CreateExecutor(conflictSource).ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    operationId,
                    quantity: GrantQuantity + 1,
                    connectionId: Guid.NewGuid()));
        }

        Check.True(
            conflict.Disposition ==
                DeveloperItemGrantExecutionDisposition
                    .RequestHashConflict &&
            conflict.Receipt is null,
            "same operation UUID with a different request is rejected");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedQuantity: GrantQuantity,
            expectedDuplicateCount: 1,
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
                DeveloperItemGrantExecutionDisposition.Committed),
            "one concurrent grant executor commits");
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                DeveloperItemGrantExecutionDisposition.Duplicate),
            "one concurrent grant executor replays");

        var committed = results.Single(result =>
            result.Disposition ==
            DeveloperItemGrantExecutionDisposition.Committed).Receipt ??
            throw new InvalidOperationException(
                "The concurrent grant winner returned no receipt.");
        var duplicate = results.Single(result =>
            result.Disposition ==
            DeveloperItemGrantExecutionDisposition.Duplicate).Receipt ??
            throw new InvalidOperationException(
                "The concurrent grant retry returned no receipt.");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "concurrent executors observe one canonical receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedQuantity: GrantQuantity,
            expectedDuplicateCount: 1,
            expectedConflictCount: 0,
            "concurrent execution produces one grant and one event");
    }

    private static async Task AssertInsufficientCapacityAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "full",
            fillKitBag: true);
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(
            fixture,
            operationId,
            quantity: 1);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(source).ExecuteAsync(
            envelope);

        Check.True(
            result.Disposition ==
                DeveloperItemGrantExecutionDisposition
                    .PreconditionFailed &&
            result.Receipt is null,
            "a full authoritative kitbag rejects the grant");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 0 &&
            state.GrantedQuantity == 0 &&
            state.GrantedItemCount == 0 &&
            state.TotalItemCount == 96 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.RequestConflictCount == 0 &&
            state.IsReconciled,
            "capacity rejection persists terminal identity without " +
            "changing inventory");

        await DeleteOneFixtureBagItemAsync(
            connectionString,
            fixture.CharacterId);
        await using var retrySource =
            NpgsqlDataSource.Create(connectionString);
        var retry = await CreateExecutor(retrySource).ExecuteAsync(
            CreateEnvelope(
                fixture,
                operationId,
                quantity: 1,
                connectionId: Guid.NewGuid()));
        Check.True(
            retry.Disposition ==
                DeveloperItemGrantExecutionDisposition
                    .PreconditionFailed &&
            retry.Receipt is null,
            "same capacity-failure UUID replays after space opens");
        var replayState =
            await ReadStateAsync(connectionString, fixture);
        Check.True(
            replayState.InventoryRevision == 0 &&
            replayState.GrantedQuantity == 0 &&
            replayState.GrantedItemCount == 0 &&
            replayState.TotalItemCount == 95 &&
            replayState.AuditCount == 1 &&
            replayState.InboxCount == 1 &&
            replayState.LedgerCount == 0 &&
            replayState.OutboxCount == 0 &&
            replayState.DuplicateCount == 1 &&
            replayState.RequestConflictCount == 0,
            "terminal capacity replay never grants into later space");
    }

    private static void AssertCommittedState(
        GrantDurableState state,
        long expectedQuantity,
        int expectedDuplicateCount,
        int expectedConflictCount,
        string description)
    {
        Check.True(
            state.InventoryRevision == 1 &&
            state.GrantedQuantity == expectedQuantity &&
            state.GrantedItemCount == 1 &&
            state.TotalItemCount == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == expectedDuplicateCount &&
            state.RequestConflictCount == expectedConflictCount &&
            state.IsReconciled,
            description);
    }

    private static void AssertEmptyGrantState(
        GrantDurableState state,
        string description)
    {
        Check.True(
            state.InventoryRevision == 0 &&
            state.GrantedQuantity == 0 &&
            state.GrantedItemCount == 0 &&
            state.TotalItemCount == 0 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.RequestConflictCount == 0 &&
            state.IsReconciled,
            description);
    }
}
