using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresEquipmentBagTransferCommandIntegrationChecks
{
    private static async Task AssertReplayAndConflictAsync(
        string connectionString)
    {
        await AssertConcurrentExactReplayAsync(connectionString);
        await AssertTerminalLateReplacementAsync(connectionString);
        await AssertWrongOwnerAsync(connectionString);
    }

    private static async Task AssertConcurrentExactReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "replay",
            kitBagItem: Item(1007));
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var concurrent = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(
                _ => ExecuteAsync(executor, fixture, operationId)));
        EquipmentBagTransferExecutionReceipt? committed = null;
        var committedCount = 0;
        foreach (var result in concurrent)
        {
            if (result.Disposition ==
                EquipmentBagTransferDisposition.Committed)
            {
                committed = RequireReceipt(
                    result,
                    EquipmentBagTransferDisposition.Committed,
                    EquipmentBagTransferResultStatus.Equipped,
                    "concurrent first commit");
                committedCount++;
            }
        }
        Check.Equal(
            1,
            committedCount,
            "exactly one concurrent first delivery commits");
        Check.Equal(
            3,
            concurrent.Count(result =>
                result.Disposition ==
                    EquipmentBagTransferDisposition.Duplicate),
            "remaining concurrent first deliveries replay");
        foreach (var result in concurrent.Where(
                     result => result.Disposition ==
                         EquipmentBagTransferDisposition.Duplicate))
        {
            var duplicate = RequireReceipt(
                result,
                EquipmentBagTransferDisposition.Duplicate,
                EquipmentBagTransferResultStatus.Equipped,
                "concurrent duplicate");
            Check.True(
                duplicate == committed,
                "concurrent replay returns canonical receipt");
        }

        var explicitReplay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                operationId,
                fixture.EquipmentSlot,
                fixture.KitBagSlot),
            EquipmentBagTransferDisposition.Duplicate,
            EquipmentBagTransferResultStatus.Equipped,
            "explicit replay");
        Check.True(
            explicitReplay == committed,
            "pre-route replay returns exact receipt");

        var pairConflict = await executor.TryReplayAsync(
            fixture.Subject,
            operationId,
            fixture.EquipmentSlot,
            fixture.KitBagSlot + 1);
        Check.Equal(
            (int)EquipmentBagTransferDisposition
                .RequestHashConflict,
            (int)pairConflict.Disposition,
            "same UUID with different slots conflicts");
        var hashConflict = await ExecuteAsync(
            executor,
            fixture,
            operationId,
            expectedKitBag: "[]");
        Check.Equal(
            (int)EquipmentBagTransferDisposition
                .RequestHashConflict,
            (int)hashConflict.Disposition,
            "same UUID with changed state conflicts");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(1L, state.InventoryRevision, "replay revision");
        Check.Equal(
            fixture.KitBagItemId!.Value,
            state.EquipmentItemId,
            "replay never moves item back");
        Check.Equal(4, state.DuplicateCount, "duplicate evidence");
        Check.Equal(2, state.ConflictCount, "conflict evidence");
        Check.Equal(1L, state.LedgerCount, "one replay ledger");
        Check.Equal(1L, state.OutboxCount, "one replay outbox");
    }

    private static async Task AssertTerminalLateReplacementAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "late");
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var terminal = RequireReceipt(
            await ExecuteAsync(executor, fixture, operationId),
            EquipmentBagTransferDisposition.TerminalRejected,
            EquipmentBagTransferResultStatus.BothEmpty,
            "both empty");

        long replacementId;
        await using (var connection =
                     new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var transaction =
                await connection.BeginTransactionAsync();
            replacementId = await InsertItemAsync(
                connection,
                transaction,
                fixture.CharacterId,
                location: 1,
                fixture.KitBagSlot,
                Item(1007));
            await transaction.CommitAsync();
        }

        var replay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                operationId,
                fixture.EquipmentSlot,
                fixture.KitBagSlot),
            EquipmentBagTransferDisposition.Duplicate,
            EquipmentBagTransferResultStatus.BothEmpty,
            "late replacement replay");
        Check.True(
            replay == terminal,
            "late replacement returns stored terminal receipt");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(
            replacementId,
            state.KitBagItemId,
            "terminal replay never equips late replacement");
        Check.Equal(0L, state.LedgerCount, "terminal no ledger");
    }

    private static async Task AssertWrongOwnerAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "owner",
            kitBagItem: Item(1007));
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
            (int)EquipmentBagTransferDisposition.PreconditionFailed,
            (int)result.Disposition,
            "wrong account cannot own transfer");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(0L, state.InventoryRevision, "wrong-owner revision");
        Check.Equal(0L, state.AuditCount, "wrong-owner audit");
        Check.Equal(0L, state.InboxCount, "wrong-owner inbox");
        Check.Equal(0L, state.LedgerCount, "wrong-owner ledger");
        Check.Equal(0L, state.OutboxCount, "wrong-owner outbox");
        Check.Equal(
            fixture.KitBagItemId!.Value,
            state.KitBagItemId,
            "wrong owner does not move bag item");
    }
}
