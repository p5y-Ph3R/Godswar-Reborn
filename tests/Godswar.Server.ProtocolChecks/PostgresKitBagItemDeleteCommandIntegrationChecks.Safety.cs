using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemDeleteCommandIntegrationChecks
{
    private static async Task
        AssertTerminalRejectionsAndLateItemSafetyAsync(
            string connectionString)
    {
        await AssertEmptySlotAsync(connectionString);
        await AssertStaleSelectionAsync(connectionString);
        await AssertLateItemIsNeverDeletedAsync(connectionString);
    }

    private static async Task AssertEmptySlotAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "empty",
            targetStartsEmpty: true);
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        var result = await ExecuteAsync(
            executor,
            fixture,
            operationId);
        var receipt = RequireReceipt(
            result,
            KitBagItemDeleteExecutionDisposition.TerminalRejected,
            KitBagItemDeleteResultStatus.EmptySlot,
            "empty-slot delete");
        Check.True(
            receipt.ExpectedCompactItemState == "[]" &&
            receipt.AuthoritativeCompactItemState == "[]" &&
            receipt.InventoryRevision == 0 &&
            receipt.OutboxEventId is null,
            "empty-slot receipt records no inventory mutation");

        var replay = await executor.TryReplayAsync(
            fixture.Subject,
            PlayerOwnershipTestFences.ForCharacter(
                fixture.Subject.CharacterId),
            operationId);
        AssertReceiptsEqual(
            receipt,
            RequireReceipt(
                replay,
                KitBagItemDeleteExecutionDisposition.Duplicate,
                KitBagItemDeleteResultStatus.EmptySlot,
                "empty-slot replay"),
            "empty-slot replay is exact");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            state.InventoryRevision == 0 &&
            state.TargetItemCount == 0 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.CompatibilityAuditCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 1 &&
            state.IsReconciled,
            "empty slot writes permanent audit/inbox evidence only");
    }

    private static async Task AssertStaleSelectionAsync(
        string connectionString)
    {
        var actual = Item(4212, 2);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "stale",
            actual);
        var expected = Item(4232, 1).ToCompactString();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        var result = await ExecuteAsync(
            executor,
            fixture,
            Guid.NewGuid(),
            expected);
        var receipt = RequireReceipt(
            result,
            KitBagItemDeleteExecutionDisposition.TerminalRejected,
            KitBagItemDeleteResultStatus.StaleSelection,
            "stale-selection delete");
        Check.True(
            receipt.ExpectedCompactItemState == expected &&
            receipt.AuthoritativeCompactItemState ==
                actual.ToCompactString() &&
            receipt.InventoryRevision == 0 &&
            receipt.OutboxEventId is null,
            "stale receipt captures expected and locked states");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            state.InventoryRevision == 0 &&
            state.TargetItemCount == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.CompatibilityAuditCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.IsReconciled,
            "stale selection writes audit/inbox but preserves item");
    }

    private static async Task AssertLateItemIsNeverDeletedAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "late",
            targetStartsEmpty: true);
        var lateItem = Item(4212, 9, quality: 5, grade: 8);
        await InsertFixtureItemAsync(
            connectionString,
            fixture,
            lateItem);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        var result = await ExecuteAsync(
            executor,
            fixture,
            Guid.NewGuid(),
            expectedState: "[]");
        var receipt = RequireReceipt(
            result,
            KitBagItemDeleteExecutionDisposition.TerminalRejected,
            KitBagItemDeleteResultStatus.StaleSelection,
            "late-item delete");
        Check.True(
            receipt.AuthoritativeCompactItemState ==
                lateItem.ToCompactString(),
            "late-item rejection reports the exact protected item");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            state.InventoryRevision == 0 &&
            state.TargetItemCount == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.CompatibilityAuditCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0,
            "item arriving after selection is never deleted");
    }
}
