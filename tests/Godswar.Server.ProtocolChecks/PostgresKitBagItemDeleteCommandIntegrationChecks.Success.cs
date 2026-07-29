using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemDeleteCommandIntegrationChecks
{
    private static async Task AssertSuccessAndReconciliationAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "success");
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        var missing = await executor.TryReplayAsync(
            fixture.Subject,
            operationId);
        Check.Equal(
            (int)KitBagItemDeleteExecutionDisposition.ReplayNotFound,
            (int)missing.Disposition,
            "delete replay is absent before first execution");

        var committed = await ExecuteAsync(
            executor,
            fixture,
            operationId);
        var receipt = RequireReceipt(
            committed,
            KitBagItemDeleteExecutionDisposition.Committed,
            KitBagItemDeleteResultStatus.Deleted,
            "item delete");
        Check.True(
            receipt.CharacterId == fixture.CharacterId &&
            receipt.KitBagSlot == fixture.TargetSlot &&
            receipt.ExpectedCompactItemState ==
                fixture.InitialItemState &&
            receipt.AuthoritativeCompactItemState ==
                fixture.InitialItemState &&
            receipt.InventoryRevision == 1 &&
            receipt.OutboxEventId.HasValue,
            "delete receipt captures exact locked item and revision");

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
            "delete commits one mutation and reconciles inventory");

        var replay = await executor.TryReplayAsync(
            fixture.Subject,
            operationId);
        var replayReceipt = RequireReceipt(
            replay,
            KitBagItemDeleteExecutionDisposition.Duplicate,
            KitBagItemDeleteResultStatus.Deleted,
            "delete replay");
        AssertReceiptsEqual(
            receipt,
            replayReceipt,
            "delete replay returns the exact canonical receipt");

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
            "exact replay increments only bounded duplicate evidence");
    }
}
