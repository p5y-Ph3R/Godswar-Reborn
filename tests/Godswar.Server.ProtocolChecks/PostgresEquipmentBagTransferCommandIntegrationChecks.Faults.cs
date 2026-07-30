using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresEquipmentBagTransferCommandIntegrationChecks
{
    private static async Task AssertFaultRecoveryAsync(
        string connectionString)
    {
        PostgresEquipmentBagTransferCommandStage[] rollbackPoints =
        [
            PostgresEquipmentBagTransferCommandStage.AuditInserted,
            PostgresEquipmentBagTransferCommandStage.InboxInserted,
            PostgresEquipmentBagTransferCommandStage
                .CompatibilityAuditInserted,
            PostgresEquipmentBagTransferCommandStage.ItemMoved,
            PostgresEquipmentBagTransferCommandStage
                .InventoryRevisionAdvanced,
            PostgresEquipmentBagTransferCommandStage
                .InventoryLedgerInserted,
            PostgresEquipmentBagTransferCommandStage.OutboxInserted,
            PostgresEquipmentBagTransferCommandStage.BeforeCommit
        ];
        foreach (var stage in rollbackPoints)
        {
            await AssertRollbackAtAsync(connectionString, stage);
        }
        await AssertAfterCommitUncertaintyAsync(connectionString);
    }

    private static async Task AssertRollbackAtAsync(
        string connectionString,
        PostgresEquipmentBagTransferCommandStage stage)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            $"f{(int)stage}",
            kitBagItem: Item(1007));
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                CreateExecutor(
                    dataSource,
                    new ThrowingProbe(stage)),
                fixture,
                Guid.NewGuid()),
            stage,
            $"fault at {stage} rolls back");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(
            0L,
            state.InventoryRevision,
            $"{stage} revision rolls back");
        Check.Equal(0L, state.EquipmentItemId, $"{stage} equipment");
        Check.Equal(
            fixture.KitBagItemId!.Value,
            state.KitBagItemId,
            $"{stage} restores bag identity");
        Check.Equal(
            0L,
            state.TemporaryItemCount,
            $"{stage} leaves no temporary item");
        Check.Equal(0L, state.AuditCount, $"{stage} audit");
        Check.Equal(0L, state.InboxCount, $"{stage} inbox");
        Check.Equal(
            0L,
            state.CompatibilityAuditCount,
            $"{stage} compatibility audit");
        Check.Equal(0L, state.LedgerCount, $"{stage} ledger");
        Check.Equal(0L, state.OutboxCount, $"{stage} outbox");
        Check.True(
            state.IsReconciled,
            $"{stage} remains reconciled");
    }

    private static async Task AssertAfterCommitUncertaintyAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "afterc",
            kitBagItem: Item(1007));
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                CreateExecutor(
                    dataSource,
                    new ThrowingProbe(
                        PostgresEquipmentBagTransferCommandStage
                            .AfterCommit)),
                fixture,
                operationId),
            PostgresEquipmentBagTransferCommandStage.AfterCommit,
            "after-commit uncertainty is surfaced");
        RequireReceipt(
            await CreateExecutor(dataSource).TryReplayAsync(
                fixture.Subject,
                PlayerOwnershipTestFences.ForCharacter(
                    fixture.Subject.CharacterId),
                operationId,
                fixture.EquipmentSlot,
                fixture.KitBagSlot),
            EquipmentBagTransferDisposition.Duplicate,
            EquipmentBagTransferResultStatus.Equipped,
            "after-commit replay");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(1L, state.InventoryRevision, "after revision");
        Check.Equal(
            fixture.KitBagItemId!.Value,
            state.EquipmentItemId,
            "after-commit item identity");
        Check.Equal(0L, state.KitBagItemId, "after-commit bag");
        Check.Equal(1L, state.LedgerCount, "after-commit ledger");
        Check.Equal(1L, state.OutboxCount, "after-commit outbox");
        Check.True(state.IsReconciled, "after reconciliation");
    }

    private static async Task ExpectInjectedFaultAsync(
        Func<Task<EquipmentBagTransferExecutionResult>> action,
        PostgresEquipmentBagTransferCommandStage expectedStage,
        string description)
    {
        try
        {
            _ = await action();
        }
        catch (InjectedTransferFault exception)
            when (exception.Stage == expectedStage)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{nameof(InjectedTransferFault)} at {expectedStage}.");
    }
}
