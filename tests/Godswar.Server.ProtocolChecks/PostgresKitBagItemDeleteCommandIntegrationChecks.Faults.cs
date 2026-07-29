using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemDeleteCommandIntegrationChecks
{
    private static async Task AssertFaultRecoveryAsync(
        string connectionString)
    {
        await AssertRollbackBeforeCommitAsync(connectionString);
        await AssertCommitUncertaintyAsync(connectionString);
    }

    private static async Task AssertRollbackBeforeCommitAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "rollbk");
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(
            dataSource,
            new ThrowingProbe(
                PostgresKitBagItemDeleteCommandStage.BeforeCommit));

        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                executor,
                fixture,
                Guid.NewGuid()),
            PostgresKitBagItemDeleteCommandStage.BeforeCommit,
            "fault before commit rolls back item deletion");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            state.InventoryRevision == 0 &&
            state.TargetItemCount == 1 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.CompatibilityAuditCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.IsReconciled,
            "pre-commit failure rolls back every durable write");
    }

    private static async Task AssertCommitUncertaintyAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "uncert");
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var uncertainExecutor = CreateExecutor(
            dataSource,
            new ThrowingProbe(
                PostgresKitBagItemDeleteCommandStage.AfterCommit));

        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                uncertainExecutor,
                fixture,
                operationId),
            PostgresKitBagItemDeleteCommandStage.AfterCommit,
            "fault after commit exposes an uncertain acknowledgement");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.TargetItemCount == 0 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.CompatibilityAuditCount == 1 &&
            state.LedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.IsReconciled,
            "post-commit fault preserves the one committed mutation");

        var replay = await CreateExecutor(dataSource).TryReplayAsync(
            fixture.Subject,
            operationId);
        var receipt = RequireReceipt(
            replay,
            KitBagItemDeleteExecutionDisposition.Duplicate,
            KitBagItemDeleteResultStatus.Deleted,
            "uncertain delete replay");
        Check.True(
            receipt.InventoryRevision == 1 &&
            receipt.OutboxEventId.HasValue,
            "retry after uncertain commit recovers canonical receipt");

        var replayState = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            replayState.InventoryRevision == 1 &&
            replayState.TargetItemCount == 0 &&
            replayState.AuditCount == 1 &&
            replayState.InboxCount == 1 &&
            replayState.CompatibilityAuditCount == 1 &&
            replayState.LedgerCount == 1 &&
            replayState.OutboxCount == 1 &&
            replayState.DuplicateCount == 1 &&
            replayState.IsReconciled,
            "retry after uncertain commit cannot repeat deletion");
    }

    private static async Task ExpectInjectedFaultAsync(
        Func<Task<KitBagItemDeleteExecutionResult>> action,
        PostgresKitBagItemDeleteCommandStage expectedStage,
        string description)
    {
        try
        {
            _ = await action();
        }
        catch (InjectedDeleteFault exception)
            when (exception.Stage == expectedStage)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{nameof(InjectedDeleteFault)} at {expectedStage}.");
    }
}
