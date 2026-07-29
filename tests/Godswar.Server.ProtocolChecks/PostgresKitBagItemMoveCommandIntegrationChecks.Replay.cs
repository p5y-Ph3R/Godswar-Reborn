using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemMoveCommandIntegrationChecks
{
    private static async Task AssertReplayAndConflictAsync(
        string connectionString)
    {
        await AssertConcurrentExactReplayAsync(connectionString);
        await AssertWrongOwnerAsync(connectionString);
    }

    private static async Task AssertConcurrentExactReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "replay",
            destinationPresent: true);
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var committed = RequireReceipt(
            await ExecuteAsync(executor, fixture, operationId),
            KitBagItemMoveExecutionDisposition.Committed,
            KitBagItemMoveResultStatus.Swapped,
            "initial replay fixture swap");

        var concurrent = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(
                _ => ExecuteAsync(executor, fixture, operationId)));
        foreach (var result in concurrent)
        {
            var duplicate = RequireReceipt(
                result,
                KitBagItemMoveExecutionDisposition.Duplicate,
                KitBagItemMoveResultStatus.Swapped,
                "concurrent exact replay");
            Check.True(
                duplicate == committed,
                "concurrent replay returns exact canonical receipt");
        }

        var explicitReplay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                operationId,
                fixture.SourceSlot,
                fixture.DestinationSlot),
            KitBagItemMoveExecutionDisposition.Duplicate,
            KitBagItemMoveResultStatus.Swapped,
            "explicit exact replay");
        Check.True(
            explicitReplay == committed,
            "pre-route replay returns exact receipt");

        var pairConflict = await executor.TryReplayAsync(
            fixture.Subject,
            operationId,
            fixture.SourceSlot - 1,
            fixture.DestinationSlot);
        Check.Equal(
            (int)KitBagItemMoveExecutionDisposition
                .RequestHashConflict,
            (int)pairConflict.Disposition,
            "same UUID with a different pair conflicts");
        var hashConflict = await ExecuteAsync(
            executor,
            fixture,
            operationId,
            expectedSource: "[]");
        Check.Equal(
            (int)KitBagItemMoveExecutionDisposition
                .RequestHashConflict,
            (int)hashConflict.Disposition,
            "same pair and UUID with changed state conflicts");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(1L, state.InventoryRevision, "replay revision");
        Check.Equal(
            fixture.DestinationItemId!.Value,
            state.SourceItemId,
            "replay never swaps items back");
        Check.Equal(
            fixture.SourceItemId!.Value,
            state.DestinationItemId,
            "replay preserves one committed swap");
        Check.Equal(5, state.DuplicateCount, "duplicate evidence count");
        Check.Equal(2, state.ConflictCount, "conflict evidence count");
        Check.Equal(2L, state.LedgerCount, "no duplicate ledgers");
        Check.Equal(1L, state.OutboxCount, "no duplicate outbox");
    }

    private static async Task AssertWrongOwnerAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "owner");
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await ExecuteAsync(
            CreateExecutor(dataSource),
            fixture,
            Guid.NewGuid(),
            subject: new CommandSubject(
                fixture.AccountId + 1_000_000,
                fixture.CharacterId));
        Check.Equal(
            (int)KitBagItemMoveExecutionDisposition
                .PreconditionFailed,
            (int)result.Disposition,
            "wrong account cannot own character movement");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(0L, state.InventoryRevision, "wrong-owner revision");
        Check.Equal(0L, state.AuditCount, "wrong-owner audit");
        Check.Equal(0L, state.InboxCount, "wrong-owner inbox");
        Check.Equal(0L, state.LedgerCount, "wrong-owner ledger");
        Check.Equal(0L, state.OutboxCount, "wrong-owner outbox");
        Check.Equal(
            fixture.SourceItemId!.Value,
            state.SourceItemId,
            "wrong owner does not move source");
    }
}
