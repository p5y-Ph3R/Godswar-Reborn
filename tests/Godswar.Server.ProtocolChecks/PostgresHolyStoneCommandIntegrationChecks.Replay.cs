using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertReplayConflictAndConcurrencyAsync(
        string connectionString)
    {
        await AssertCommittedReplayAndConflictAsync(connectionString);
        await AssertTerminalReplayAsync(connectionString);
        await AssertConcurrentDuplicateAsync(connectionString);
        await AssertDrillReplayDoesNotRedebitAsync(connectionString);
        await AssertConcurrentDrillDebitsOnceAsync(connectionString);
        await AssertCrossCityReplayAsync(connectionString);
    }

    private static async Task AssertCommittedReplayAndConflictAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "replay",
            target: Weapon(1),
            stone: SimpleItem(9060, grade: 6, stack: 2));
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var committed = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                operationId,
                HolyStoneCommandOperation.Mount),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Mounted,
            "committed replay seed");
        var replayed = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                HolyStoneCommandOperation.Mount,
                operationId),
            HolyStoneExecutionDisposition.Duplicate,
            HolyStoneCommandResultStatus.Mounted,
            "committed replay");
        Check.Equal(
            committed.AuditReference,
            replayed.AuditReference,
            "replay returns the original receipt");

        var conflict = await ExecuteAsync(
            executor,
            fixture,
            operationId,
            HolyStoneCommandOperation.Mount,
            expectedTarget: "[]");
        Check.Equal(
            (int)HolyStoneExecutionDisposition.RequestHashConflict,
            (int)conflict.Disposition,
            "same operation with different state conflicts");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Mount);
        Check.Equal(1L, state.InventoryRevision, "replay mutates once");
        Check.Equal(1, state.DuplicateCount, "replay count");
        Check.Equal(1, state.ConflictCount, "conflict count");
        Check.Equal(2L, state.LedgerCount, "replay has one ledger set");
        Check.Equal(1L, state.OutboxCount, "replay has one outbox");
    }

    private static async Task AssertTerminalReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "termre",
            target: Weapon(0),
            stone: SimpleItem(9060, grade: 3));
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                operationId,
                HolyStoneCommandOperation.Mount),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.SocketNotDrilled,
            "terminal replay seed");
        RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                HolyStoneCommandOperation.Mount,
                operationId),
            HolyStoneExecutionDisposition.Duplicate,
            HolyStoneCommandResultStatus.SocketNotDrilled,
            "terminal replay");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Mount);
        Check.Equal(1, state.DuplicateCount, "terminal replay count");
        AssertTerminalEvidence(state, "terminal replay");
    }

    private static async Task AssertConcurrentDuplicateAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "concur",
            target: Weapon(1),
            stone: SimpleItem(9061, grade: 4, stack: 3));
        var operationId = Guid.NewGuid();
        await using var firstSource =
            NpgsqlDataSource.Create(connectionString);
        await using var secondSource =
            NpgsqlDataSource.Create(connectionString);
        var results = await Task.WhenAll(
            ExecuteAsync(
                CreateExecutor(firstSource),
                fixture,
                operationId,
                HolyStoneCommandOperation.Mount),
            ExecuteAsync(
                CreateExecutor(secondSource),
                fixture,
                operationId,
                HolyStoneCommandOperation.Mount));
        Check.Equal(
            1,
            results.Count(
                result =>
                    result.Disposition ==
                    HolyStoneExecutionDisposition.Committed),
            "concurrent operation commits once");
        Check.Equal(
            1,
            results.Count(
                result =>
                    result.Disposition ==
                    HolyStoneExecutionDisposition.Duplicate),
            "concurrent operation replays once");
        var stone = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.StoneSlot))!.Value.Item;
        Check.Equal(2, stone.Stack, "concurrency consumes one stone");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Mount);
        Check.Equal(1L, state.InventoryRevision, "concurrent revision");
        Check.Equal(2L, state.LedgerCount, "concurrent ledger");
        Check.Equal(1L, state.OutboxCount, "concurrent outbox");
    }

    private static async Task AssertDrillReplayDoesNotRedebitAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "drilrp",
            target: Weapon(0),
            gold: 500);
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var committed = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                operationId,
                HolyStoneCommandOperation.Drill),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Drilled,
            "drill replay seed");
        var replay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                HolyStoneCommandOperation.Drill,
                operationId),
            HolyStoneExecutionDisposition.Duplicate,
            HolyStoneCommandResultStatus.Drilled,
            "drill replay");
        Check.Equal(
            committed,
            replay,
            "drill replay returns immutable wallet evidence");

        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Drill);
        Check.True(
            state.Gold == 270 &&
            state.WalletRevision == 1 &&
            state.InventoryRevision == 1 &&
            state.CurrencyLedgerCount == 1 &&
            state.GoldLedgerDelta == -230 &&
            state.LedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1 &&
            state.WalletReconciled &&
            state.InventoryReconciled,
            "drill replay neither redebits nor remutates");
    }

    private static async Task AssertConcurrentDrillDebitsOnceAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "drilcc",
            target: Weapon(0),
            gold: 500);
        var operationId = Guid.NewGuid();
        await using var firstSource =
            NpgsqlDataSource.Create(connectionString);
        await using var secondSource =
            NpgsqlDataSource.Create(connectionString);
        var results = await Task.WhenAll(
            ExecuteAsync(
                CreateExecutor(firstSource),
                fixture,
                operationId,
                HolyStoneCommandOperation.Drill),
            ExecuteAsync(
                CreateExecutor(secondSource),
                fixture,
                operationId,
                HolyStoneCommandOperation.Drill));
        Check.Equal(
            1,
            results.Count(
                result =>
                    result.Disposition ==
                    HolyStoneExecutionDisposition.Committed),
            "concurrent drill commits once");
        Check.Equal(
            1,
            results.Count(
                result =>
                    result.Disposition ==
                    HolyStoneExecutionDisposition.Duplicate),
            "concurrent drill replays once");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Drill);
        Check.True(
            state.Gold == 270 &&
            state.WalletRevision == 1 &&
            state.InventoryRevision == 1 &&
            state.CurrencyLedgerCount == 1 &&
            state.GoldLedgerDelta == -230 &&
            state.LedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1 &&
            state.WalletReconciled &&
            state.InventoryReconciled,
            "concurrent drill has one wallet and inventory transition");
    }

    private static async Task AssertCrossCityReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "cityrp",
            target: Weapon(1),
            stone: SimpleItem(9060, grade: 4, stack: 2));
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var committed = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                operationId,
                HolyStoneCommandOperation.Mount,
                npcId: HolyStoneCommandEnvelope.SpartaNpcId),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Mounted,
            "Sparta Holy Stone seed");
        var replay = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                operationId,
                HolyStoneCommandOperation.Mount,
                npcId: HolyStoneCommandEnvelope.AthensNpcId),
            HolyStoneExecutionDisposition.Duplicate,
            HolyStoneCommandResultStatus.Mounted,
            "Athens replay of Sparta operation");
        Check.Equal(
            HolyStoneCommandEnvelope.SpartaNpcId,
            replay.NpcId,
            "cross-city replay returns the original receipt");
        Check.Equal(
            committed,
            replay,
            "equivalent artisan endpoints share one durable operation");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Mount);
        Check.True(
            state.InventoryRevision == 1 &&
            state.LedgerCount == 2 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1 &&
            state.ConflictCount == 0 &&
            state.WalletReconciled &&
            state.InventoryReconciled,
            "cross-city retry does not remutate or conflict");
    }
}
