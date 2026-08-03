using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertMountSemanticsAsync(
        string connectionString)
    {
        await AssertStackedMountAsync(connectionString);
        await AssertSingleStoneDeletionAsync(connectionString);
    }

    private static async Task AssertStackedMountAsync(
        string connectionString)
    {
        var fallback = SimpleItem(1007);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "mount",
            target: Weapon(2),
            stone: SimpleItem(9060, grade: 7, stack: 2),
            additionalBagItems:
            [
                (0, fallback)
            ]);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Mount),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Mounted,
            "stacked mount");
        Check.Equal(0, receipt.SocketIndex, "first opened socket");
        Check.Equal(
            fixture.TargetItemId!.Value,
            receipt.TargetItemInstanceId!.Value,
            "mount receipt preserves target identity");
        Check.Equal(
            fixture.StoneItemId!.Value,
            receipt.StoneItemInstanceId!.Value,
            "mount receipt preserves stone identity");

        var target = await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            checked((short)fixture.TargetLocation),
            fixture.TargetSlot);
        Check.Equal(
            fixture.TargetItemId.Value,
            target!.Value.Id,
            "mount updates the exact stable target");
        Check.True(
            target.Value.Item.Socket1EffectId == 1 &&
            target.Value.Item.Socket1Level == 7,
            "mount writes the Fire spirit and level");
        var stone = await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.StoneSlot);
        Check.Equal(
            fixture.StoneItemId.Value,
            stone!.Value.Id,
            "stack decrement preserves stone identity");
        Check.Equal(
            1,
            stone.Value.Item.Stack,
            "mount consumes exactly one stacked stone");
        var fallbackAfter = await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            0);
        Check.Equal(
            fallback,
            fallbackAfter!.Value.Item,
            "mount never mutates a fallback weapon");

        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Mount);
        Check.Equal(1L, state.InventoryRevision, "mount revision");
        Check.Equal(1000, state.Gold, "mount invents no Gold debit");
        AssertCommittedEvidence(state, expectedLedger: 2, "mount");
    }

    private static async Task AssertSingleStoneDeletionAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "delete",
            target: Weapon(1),
            stone: SimpleItem(9061, grade: 9));
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Mount),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Mounted,
            "single-stone mount");
        Check.True(
            await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.StoneSlot) is null,
            "mount deletes an exhausted one-item stack");
        AssertCommittedEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Mount),
            expectedLedger: 2,
            "single-stone mount");
    }

    private static void AssertCommittedEvidence(
        HolyDurableState state,
        long expectedLedger,
        string description)
    {
        Check.Equal(
            0L,
            state.WalletRevision,
            $"{description} has no wallet revision");
        Check.Equal(
            0L,
            state.CurrencyLedgerCount,
            $"{description} has no currency ledger");
        Check.Equal(
            0L,
            state.GoldLedgerDelta,
            $"{description} has no Gold ledger delta");
        Check.Equal(1L, state.AuditCount, $"{description} audit");
        Check.Equal(1L, state.InboxCount, $"{description} inbox");
        Check.Equal(
            expectedLedger,
            state.LedgerCount,
            $"{description} ledger");
        Check.Equal(1L, state.OutboxCount, $"{description} outbox");
        Check.True(
            state.WalletReconciled && state.InventoryReconciled,
            $"{description} reconciles");
    }
}
