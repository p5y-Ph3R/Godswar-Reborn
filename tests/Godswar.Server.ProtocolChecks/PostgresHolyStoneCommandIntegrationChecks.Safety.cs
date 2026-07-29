using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertTerminalSafetyAsync(
        string connectionString)
    {
        await AssertNoTargetFallbackAsync(connectionString);
        await AssertNoAutomaticDrillAsync(connectionString);
        await AssertDuplicateSpiritAsync(connectionString);
        await AssertMissingStoneAsync(connectionString);
    }

    private static async Task AssertNoTargetFallbackAsync(
        string connectionString)
    {
        var fallback = Weapon(1);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "nofall",
            target: null,
            stone: SimpleItem(9060, grade: 5),
            additionalBagItems:
            [
                (0, fallback)
            ]);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Mount),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.TargetMissing,
            "missing exact target");
        Check.Equal(
            fallback,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                0))!.Value.Item,
            "missing target never falls back to another weapon");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Mount),
            "missing exact target");
    }

    private static async Task AssertNoAutomaticDrillAsync(
        string connectionString)
    {
        var targetBefore = Weapon(0);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "nodril",
            target: targetBefore,
            stone: SimpleItem(9060, grade: 5, stack: 2));
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Mount),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.SocketNotDrilled,
            "mount without drilled socket");
        Check.Equal(
            targetBefore,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                0,
                fixture.TargetSlot))!.Value.Item,
            "mount never automatically drills");
        Check.Equal(
            2,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.StoneSlot))!.Value.Item.Stack,
            "rejected mount consumes no stone");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Mount),
            "mount without drilled socket");
    }

    private static async Task AssertDuplicateSpiritAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "dupspi",
            target: Weapon(2, effect1: 1, level1: 4),
            stone: SimpleItem(9060, grade: 8));
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Mount),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.DuplicateSpirit,
            "duplicate Fire spirit");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Mount),
            "duplicate Fire spirit");
    }

    private static async Task AssertMissingStoneAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "noston",
            target: Weapon(1),
            stone: null);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Mount),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.StoneMissing,
            "missing exact stone");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Mount),
            "missing exact stone");
    }

    private static void AssertTerminalEvidence(
        HolyDurableState state,
        string description,
        int expectedGold = 1000,
        long expectedWalletRevision = 0)
    {
        Check.Equal(
            0L,
            state.InventoryRevision,
            $"{description} has no revision");
        Check.Equal(
            expectedWalletRevision,
            state.WalletRevision,
            $"{description} has no wallet revision");
        Check.Equal(
            expectedGold,
            state.Gold,
            $"{description} has no Gold debit");
        Check.Equal(1L, state.AuditCount, $"{description} audit");
        Check.Equal(1L, state.InboxCount, $"{description} inbox");
        Check.Equal(0L, state.LedgerCount, $"{description} no ledger");
        Check.Equal(
            0L,
            state.CurrencyLedgerCount,
            $"{description} has no currency ledger");
        Check.Equal(
            0L,
            state.GoldLedgerDelta,
            $"{description} has no Gold ledger delta");
        Check.Equal(0L, state.OutboxCount, $"{description} no outbox");
        Check.True(
            state.WalletReconciled && state.InventoryReconciled,
            $"{description} reconciles");
    }
}
