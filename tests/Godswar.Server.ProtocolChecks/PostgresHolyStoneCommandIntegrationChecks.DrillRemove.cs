using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertDrillAndRemoveAsync(
        string connectionString)
    {
        await AssertBasicDrillMaximumAsync(connectionString);
        await AssertNormalCharacterGearDrillsAsync(connectionString);
        await AssertDrillRejectsNonCharacterGearAsync(connectionString);
        await AssertDrillInsufficientFundsAsync(connectionString);
        await AssertSelectedRemovalAsync(connectionString);
        await AssertFullBagRemovalAsync(connectionString);
    }

    private static async Task AssertDrillRejectsNonCharacterGearAsync(
        string connectionString)
    {
        foreach (var (itemId, description) in new (uint, string)[]
                 {
                     (9030, "non-equipment"),
                     (8000, "stylish"),
                     (6000, "mount"),
                     (14500, "mount gear")
                 })
        {
            var fixture = await CreateFixtureAsync(
                connectionString,
                $"no{itemId}",
                target: SimpleItem(itemId),
                gold: 3000);
            await using var dataSource =
                NpgsqlDataSource.Create(connectionString);
            var executor = CreateExecutor(dataSource);
            var receipt = RequireReceipt(
                await ExecuteAsync(
                    executor,
                    fixture,
                    Guid.NewGuid(),
                    HolyStoneCommandOperation.Drill),
                HolyStoneExecutionDisposition.TerminalRejected,
                HolyStoneCommandResultStatus.TargetNotEquipment,
                $"basic Drill rejects {description}");
            var state = await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Drill);
            Check.True(
                receipt.GoldSpent == 0 &&
                receipt.GoldBefore == 3000 &&
                receipt.GoldAfter == 3000 &&
                state.InventoryRevision == 0 &&
                state.WalletRevision == 0 &&
                state.Gold == 3000 &&
                state.LedgerCount == 0 &&
                state.CurrencyLedgerCount == 0 &&
                state.OutboxCount == 0,
                $"rejected {description} Drill changes no item or currency");
        }
    }

    private static async Task AssertNormalCharacterGearDrillsAsync(
        string connectionString)
    {
        foreach (var (itemId, description) in new (uint, string)[]
                 {
                     (2113, "armor"),
                     (2834, "gloves")
                 })
        {
            var fixture = await CreateFixtureAsync(
                connectionString,
                $"dr{description}",
                target: SimpleItem(itemId),
                gold: 3000);
            await using var dataSource =
                NpgsqlDataSource.Create(connectionString);
            var executor = CreateExecutor(dataSource);

            var firstReceipt = RequireReceipt(
                await ExecuteAsync(
                    executor,
                    fixture,
                    Guid.NewGuid(),
                    HolyStoneCommandOperation.Drill),
                HolyStoneExecutionDisposition.Committed,
                HolyStoneCommandResultStatus.Drilled,
                $"first basic {description} drill");
            var afterFirst = (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                checked((short)fixture.TargetLocation),
                fixture.TargetSlot))!.Value;
            Check.True(
                firstReceipt.GoldSpent ==
                    HolyStoneExecutionReceipt.FirstDrillGoldCost &&
                firstReceipt.GoldBefore == 3000 &&
                firstReceipt.GoldAfter == 2770 &&
                afterFirst.Item.SocketCount == 1,
                $"first {description} Drill opens one socket for 230 Gold");

            var secondReceipt = RequireReceipt(
                await ExecuteAsync(
                    executor,
                    fixture,
                    Guid.NewGuid(),
                    HolyStoneCommandOperation.Drill,
                    expectedTarget: afterFirst.Item.ToCompactString()),
                HolyStoneExecutionDisposition.Committed,
                HolyStoneCommandResultStatus.Drilled,
                $"second basic {description} drill");
            var afterSecond = (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                checked((short)fixture.TargetLocation),
                fixture.TargetSlot))!.Value;
            Check.True(
                secondReceipt.GoldSpent ==
                    HolyStoneExecutionReceipt.SecondDrillGoldCost &&
                secondReceipt.GoldBefore == 2770 &&
                secondReceipt.GoldAfter == 470 &&
                afterSecond.Item.SocketCount == 2,
                $"second {description} Drill opens two sockets for 2300 Gold");

            var state = await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Drill);
            Check.True(
                state.InventoryRevision == 2 &&
                state.WalletRevision == 2 &&
                state.Gold == 470 &&
                state.GoldLedgerDelta == -2530 &&
                state.WalletReconciled &&
                state.InventoryReconciled,
                $"{description} Drill remains atomically reconciled");
        }
    }

    private static async Task AssertBasicDrillMaximumAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "drill",
            target: Weapon(0),
            gold: 3000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var firstReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Drill),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Drilled,
            "first basic drill");
        Check.True(
            firstReceipt.GoldSpent ==
                HolyStoneExecutionReceipt.FirstDrillGoldCost &&
            firstReceipt.GoldBefore == 3000 &&
            firstReceipt.GoldAfter == 2770 &&
            firstReceipt.WalletRevision == 1,
            "first drill receipt records the exact 230 Gold debit");
        var afterFirst = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            checked((short)fixture.TargetLocation),
            fixture.TargetSlot))!.Value;
        Check.Equal(
            fixture.TargetItemId!.Value,
            afterFirst.Id,
            "first drill preserves target identity");
        Check.Equal(1, afterFirst.Item.SocketCount, "first socket");

        var secondReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Drill,
                expectedTarget: afterFirst.Item.ToCompactString()),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Drilled,
            "second basic drill");
        Check.True(
            secondReceipt.GoldSpent ==
                HolyStoneExecutionReceipt.SecondDrillGoldCost &&
            secondReceipt.GoldBefore == 2770 &&
            secondReceipt.GoldAfter == 470 &&
            secondReceipt.WalletRevision == 2,
            "second drill receipt records the exact 2300 Gold debit");
        var afterSecond = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            checked((short)fixture.TargetLocation),
            fixture.TargetSlot))!.Value;
        Check.Equal(2, afterSecond.Item.SocketCount, "second socket");

        var maximumReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Drill,
                expectedTarget: afterSecond.Item.ToCompactString()),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.MaximumSockets,
            "basic drill maximum");
        Check.True(
            maximumReceipt.GoldSpent == 0 &&
            maximumReceipt.GoldBefore == 470 &&
            maximumReceipt.GoldAfter == 470 &&
            maximumReceipt.WalletRevision == 2,
            "maximum-socket rejection preserves the wallet");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Drill);
        Check.Equal(2L, state.InventoryRevision, "two drill revisions");
        Check.Equal(2L, state.WalletRevision, "two wallet revisions");
        Check.Equal(470, state.Gold, "drill charges exact Gold costs");
        Check.Equal(3L, state.AuditCount, "drill durable outcomes");
        Check.Equal(3L, state.InboxCount, "drill durable inbox");
        Check.Equal(2L, state.LedgerCount, "drill ledgers");
        Check.Equal(2L, state.OutboxCount, "drill outbox events");
        Check.Equal(
            2L,
            state.CurrencyLedgerCount,
            "drill currency ledger entries");
        Check.Equal(
            -2530L,
            state.GoldLedgerDelta,
            "drill Gold ledger delta");
        Check.True(
            state.WalletReconciled && state.InventoryReconciled,
            "drill wallet and inventory reconcile");

        var ledgers = await ReadGoldLedgerAsync(
            connectionString,
            fixture);
        Check.True(
            ledgers.Count == 2 &&
            ledgers[0] == new HolyGoldLedgerEntry(
                1,
                -230,
                3000,
                2770,
                "holy_stone_drill") &&
            ledgers[1] == new HolyGoldLedgerEntry(
                2,
                -2300,
                2770,
                470,
                "holy_stone_drill"),
            "drill appends exact immutable Gold evidence");
    }

    private static async Task AssertDrillInsufficientFundsAsync(
        string connectionString)
    {
        await AssertDrillInsufficientFundsAsync(
            connectionString,
            "poor1",
            sockets: 0,
            gold: 229);
        await AssertDrillInsufficientFundsAsync(
            connectionString,
            "poor2",
            sockets: 1,
            gold: 2299);
    }

    private static async Task AssertDrillInsufficientFundsAsync(
        string connectionString,
        string scenario,
        short sockets,
        int gold)
    {
        var target = Weapon(sockets);
        var fixture = await CreateFixtureAsync(
            connectionString,
            scenario,
            target: target,
            gold: gold);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Drill),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.InsufficientFunds,
            $"socket {sockets} insufficient Gold");
        Check.True(
            receipt.NativeResultSubId ==
                HolyStoneNativeResults.InsufficientFundsSubId &&
            receipt.GoldSpent == 0 &&
            receipt.GoldBefore == gold &&
            receipt.GoldAfter == gold &&
            receipt.WalletRevision == 0,
            $"socket {sockets} returns native 1400 without a debit");
        Check.Equal(
            target,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                checked((short)fixture.TargetLocation),
                fixture.TargetSlot))!.Value.Item,
            $"socket {sockets} insufficient Gold preserves target");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Drill),
            $"socket {sockets} insufficient Gold",
            expectedGold: gold);
    }

    private static async Task AssertSelectedRemovalAsync(
        string connectionString)
    {
        var targetBefore = Weapon(
            2,
            effect1: 2,
            level1: 9,
            effect2: 5,
            level2: 4);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "remove",
            target: targetBefore,
            additionalBagItems:
            [
                (0, SimpleItem(9030)),
                (1, SimpleItem(9030))
            ]);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Remove,
                socketIndex: 1),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Removed,
            "selected removal");
        Check.Equal(2, receipt.OutputKitBagSlot, "first empty bag slot");
        Check.True(
            receipt.OutputItemInstanceId.HasValue,
            "removed output has stable identity");
        Check.True(
            receipt.AuthoritativeTargetBeforeCompactItemState
                .Contains(",5,4,", StringComparison.Ordinal),
            "receipt records the removed effect and level");

        var targetAfter = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            checked((short)fixture.TargetLocation),
            fixture.TargetSlot))!.Value;
        Check.Equal(
            fixture.TargetItemId!.Value,
            targetAfter.Id,
            "remove preserves target identity");
        Check.True(
            targetAfter.Item.Socket1EffectId == 2 &&
            targetAfter.Item.Socket1Level == 9 &&
            !targetAfter.Item.Socket2EffectId.HasValue &&
            !targetAfter.Item.Socket2Level.HasValue,
            "remove clears only the selected socket");
        var output = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            2))!.Value;
        Check.Equal(
            receipt.OutputItemInstanceId!.Value,
            output.Id,
            "receipt output identity is authoritative");
        Check.True(
            output.Item.Id == 9030 &&
            output.Item.Grade == 4 &&
            output.Item.Stack == 1,
            "removed level is preserved in the Heated stone grade");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Remove);
        Check.Equal(1L, state.InventoryRevision, "remove revision");
        Check.Equal(1000, state.Gold, "remove has no Gold debit");
        AssertCommittedEvidence(state, expectedLedger: 2, "remove");
    }

    private static async Task AssertFullBagRemovalAsync(
        string connectionString)
    {
        var targetBefore = Weapon(
            1,
            effect1: 2,
            level1: 6);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "bagful",
            target: targetBefore,
            fillBag: true);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Remove,
                socketIndex: 0),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.BagFull,
            "full-bag removal");
        Check.Equal(
            targetBefore,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                checked((short)fixture.TargetLocation),
                fixture.TargetSlot))!.Value.Item,
            "BagFull is decided before clearing the target");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Remove),
            "full-bag removal");
    }
}
