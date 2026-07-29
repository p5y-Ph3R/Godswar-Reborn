using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemMoveCommandIntegrationChecks
{
    private static async Task AssertFaultRecoveryAsync(
        string connectionString)
    {
        (PostgresKitBagItemMoveCommandStage Stage, int? Ordinal)[]
            rollbackPoints =
            [
                (
                    PostgresKitBagItemMoveCommandStage.AuditInserted,
                    null),
                (
                    PostgresKitBagItemMoveCommandStage.InboxInserted,
                    null),
                (
                    PostgresKitBagItemMoveCommandStage
                        .CompatibilityAuditInserted,
                    0),
                (
                    PostgresKitBagItemMoveCommandStage
                        .CompatibilityAuditInserted,
                    1),
                (
                    PostgresKitBagItemMoveCommandStage
                        .SourceMovedToTemporarySlot,
                    null),
                (
                    PostgresKitBagItemMoveCommandStage
                        .DestinationMovedToSourceSlot,
                    null),
                (
                    PostgresKitBagItemMoveCommandStage
                        .SourceMovedToDestinationSlot,
                    null),
                (
                    PostgresKitBagItemMoveCommandStage
                        .InventoryRevisionAdvanced,
                    null),
                (
                    PostgresKitBagItemMoveCommandStage
                        .InventoryLedgerInserted,
                    0),
                (
                    PostgresKitBagItemMoveCommandStage
                        .InventoryLedgerInserted,
                    1),
                (
                    PostgresKitBagItemMoveCommandStage.OutboxInserted,
                    null),
                (
                    PostgresKitBagItemMoveCommandStage.BeforeCommit,
                    null)
            ];
        foreach (var point in rollbackPoints)
        {
            await AssertRollbackAtAsync(
                connectionString,
                point.Stage,
                point.Ordinal);
        }
        await AssertAfterCommitUncertaintyAsync(connectionString);
    }

    private static async Task AssertRollbackAtAsync(
        string connectionString,
        PostgresKitBagItemMoveCommandStage stage,
        int? ordinal)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            $"f{(int)stage}{ordinal}",
            destinationPresent: true);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                CreateExecutor(
                    dataSource,
                    new ThrowingProbe(stage, ordinal)),
                fixture,
                Guid.NewGuid()),
            stage,
            ordinal,
            $"fault at {stage}/{ordinal} rolls back");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(
            0L,
            state.InventoryRevision,
            $"{stage}/{ordinal} revision rolls back");
        Check.Equal(
            fixture.SourceItemId!.Value,
            state.SourceItemId,
            $"{stage}/{ordinal} restores source identity");
        Check.Equal(
            fixture.DestinationItemId!.Value,
            state.DestinationItemId,
            $"{stage}/{ordinal} restores destination identity");
        Check.Equal(
            0L,
            state.TemporaryItemCount,
            $"{stage}/{ordinal} leaves no temporary item");
        Check.Equal(0L, state.AuditCount, $"{stage}/{ordinal} audit");
        Check.Equal(0L, state.InboxCount, $"{stage}/{ordinal} inbox");
        Check.Equal(
            0L,
            state.CompatibilityAuditCount,
            $"{stage}/{ordinal} compatibility audit");
        Check.Equal(0L, state.LedgerCount, $"{stage}/{ordinal} ledger");
        Check.Equal(0L, state.OutboxCount, $"{stage}/{ordinal} outbox");
        Check.True(
            state.IsReconciled,
            $"{stage}/{ordinal} remains reconciled");
    }

    private static async Task AssertAfterCommitUncertaintyAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "afterc",
            destinationPresent: true);
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                CreateExecutor(
                    dataSource,
                    new ThrowingProbe(
                        PostgresKitBagItemMoveCommandStage
                            .AfterCommit)),
                fixture,
                operationId),
            PostgresKitBagItemMoveCommandStage.AfterCommit,
            null,
            "after-commit uncertainty is surfaced");
        var replay = await CreateExecutor(dataSource).TryReplayAsync(
            fixture.Subject,
            operationId,
            fixture.SourceSlot,
            fixture.DestinationSlot);
        RequireReceipt(
            replay,
            KitBagItemMoveExecutionDisposition.Duplicate,
            KitBagItemMoveResultStatus.Swapped,
            "after-commit exact replay");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(1L, state.InventoryRevision, "after-commit revision");
        Check.Equal(
            fixture.DestinationItemId!.Value,
            state.SourceItemId,
            "after-commit swap source");
        Check.Equal(
            fixture.SourceItemId!.Value,
            state.DestinationItemId,
            "after-commit swap destination");
        Check.Equal(0L, state.TemporaryItemCount, "after-commit temp");
        Check.Equal(2L, state.LedgerCount, "after-commit ledgers");
        Check.Equal(1L, state.OutboxCount, "after-commit outbox");
        Check.True(state.IsReconciled, "after-commit reconciliation");
    }

    private static async Task ExpectInjectedFaultAsync(
        Func<Task<KitBagItemMoveExecutionResult>> action,
        PostgresKitBagItemMoveCommandStage expectedStage,
        int? expectedOrdinal,
        string description)
    {
        try
        {
            _ = await action();
        }
        catch (InjectedMoveFault exception)
            when (
                exception.Stage == expectedStage &&
                (!expectedOrdinal.HasValue ||
                 exception.Ordinal == expectedOrdinal.Value))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{nameof(InjectedMoveFault)} at {expectedStage}/" +
            $"{expectedOrdinal}.");
    }
}
