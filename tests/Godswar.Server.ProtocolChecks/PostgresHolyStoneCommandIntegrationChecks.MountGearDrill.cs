using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertMountGearDrillAsync(
        string connectionString)
    {
        await AssertMountGearDrillSuccessReplayAndMaximumAsync(
            connectionString);
        await AssertMountGearDrillEligibilityAsync(connectionString);
        await AssertMountGearDrillInsufficientFundsAsync(connectionString);
        await AssertMountGearDrillRejectsForeignSocketFamilyAsync(
            connectionString);
    }

    private static async Task
        AssertMountGearDrillSuccessReplayAndMaximumAsync(
            string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "mgdril",
            target: SimpleItem(14504),
            gold: 3000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        var firstOperationId = Guid.NewGuid();
        var firstReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                firstOperationId,
                HolyStoneCommandOperation.MountGearDrill),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Drilled,
            "first mount-gear drill");
        var afterFirst = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            checked((short)fixture.TargetLocation),
            fixture.TargetSlot))!.Value;
        Check.True(
            firstReceipt.NativeResultSubId ==
                HolyStoneNativeResults.DrilledSubId &&
            firstReceipt.TargetItemInstanceId == fixture.TargetItemId &&
            firstReceipt.StoneItemInstanceId is null &&
            firstReceipt.GoldSpent ==
                HolyStoneExecutionReceipt.FirstDrillGoldCost &&
            firstReceipt.GoldBefore == 3000 &&
            firstReceipt.GoldAfter == 2770 &&
            firstReceipt.WalletRevision == 1 &&
            firstReceipt.InventoryRevision == 1 &&
            afterFirst.Id == fixture.TargetItemId &&
            afterFirst.Item.Id == 14504 &&
            afterFirst.Item.SocketCount == 1,
            "first mount-gear Drill is an atomic 230-Gold mutation");

        var duplicateReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                firstOperationId,
                HolyStoneCommandOperation.MountGearDrill),
            HolyStoneExecutionDisposition.Duplicate,
            HolyStoneCommandResultStatus.Drilled,
            "duplicate mount-gear drill");
        Check.True(
            duplicateReceipt == firstReceipt,
            "mount-gear Drill replay returns the original receipt");

        var secondReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.MountGearDrill,
                expectedTarget: afterFirst.Item.ToCompactString()),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Drilled,
            "second mount-gear drill");
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
            secondReceipt.WalletRevision == 2 &&
            secondReceipt.InventoryRevision == 2 &&
            afterSecond.Id == fixture.TargetItemId &&
            afterSecond.Item.SocketCount == 2,
            "second mount-gear Drill is an atomic 2300-Gold mutation");

        var maximumReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.MountGearDrill,
                expectedTarget: afterSecond.Item.ToCompactString()),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.MaximumSockets,
            "mount-gear drill maximum");
        Check.True(
            maximumReceipt.GoldSpent == 0 &&
            maximumReceipt.GoldBefore == 470 &&
            maximumReceipt.GoldAfter == 470 &&
            maximumReceipt.WalletRevision == 2,
            "mount-gear socket maximum preserves the wallet");

        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.MountGearDrill);
        Check.True(
            state.InventoryRevision == 2 &&
            state.WalletRevision == 2 &&
            state.Gold == 470 &&
            state.AuditCount == 3 &&
            state.InboxCount == 3 &&
            state.LedgerCount == 2 &&
            state.OutboxCount == 2 &&
            state.DuplicateCount == 1 &&
            state.ConflictCount == 0 &&
            state.CurrencyLedgerCount == 2 &&
            state.GoldLedgerDelta == -2530 &&
            state.WalletReconciled &&
            state.InventoryReconciled,
            "mount-gear Drill durable evidence and replay reconcile");
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
                "mount_gear_drill") &&
            ledgers[1] == new HolyGoldLedgerEntry(
                2,
                -2300,
                2770,
                470,
                "mount_gear_drill"),
            "mount-gear Drill appends exact immutable Gold evidence");
    }

    private static async Task AssertMountGearDrillEligibilityAsync(
        string connectionString)
    {
        foreach (var (itemId, description) in new (uint, string)[]
                 {
                     (9030, "non-equipment"),
                     (6000, "mount"),
                     (1035, "character weapon")
                 })
        {
            var fixture = await CreateFixtureAsync(
                connectionString,
                $"mgno{itemId}",
                target: SimpleItem(itemId),
                gold: 3000);
            await using var dataSource =
                NpgsqlDataSource.Create(connectionString);
            var receipt = RequireReceipt(
                await ExecuteAsync(
                    CreateExecutor(dataSource),
                    fixture,
                    Guid.NewGuid(),
                    HolyStoneCommandOperation.MountGearDrill),
                HolyStoneExecutionDisposition.TerminalRejected,
                HolyStoneCommandResultStatus.TargetNotEquipment,
                $"mount-gear Drill rejects {description}");
            var state = await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.MountGearDrill);
            Check.True(
                receipt.GoldSpent == 0 &&
                receipt.NativeResultSubId ==
                    HolyStoneNativeResults.TargetNotMountGearSubId &&
                state.InventoryRevision == 0 &&
                state.WalletRevision == 0 &&
                state.Gold == 3000 &&
                state.LedgerCount == 0 &&
                state.CurrencyLedgerCount == 0 &&
                state.OutboxCount == 0,
                $"rejected {description} changes no item or currency");
        }
    }

    private static async Task AssertMountGearDrillInsufficientFundsAsync(
        string connectionString)
    {
        foreach (var (sockets, gold) in new (short, int)[]
                 {
                     (0, 229),
                     (1, 2299)
                 })
        {
            var target = SimpleItem(14504) with
            {
                SocketCount = sockets
            };
            var fixture = await CreateFixtureAsync(
                connectionString,
                $"mgpoor{sockets}",
                target: target,
                gold: gold);
            await using var dataSource =
                NpgsqlDataSource.Create(connectionString);
            var receipt = RequireReceipt(
                await ExecuteAsync(
                    CreateExecutor(dataSource),
                    fixture,
                    Guid.NewGuid(),
                    HolyStoneCommandOperation.MountGearDrill),
                HolyStoneExecutionDisposition.TerminalRejected,
                HolyStoneCommandResultStatus.InsufficientFunds,
                $"mount-gear socket {sockets} insufficient Gold");
            Check.True(
                receipt.NativeResultSubId ==
                    HolyStoneNativeResults.InsufficientFundsSubId &&
                receipt.GoldSpent == 0 &&
                receipt.GoldBefore == gold &&
                receipt.GoldAfter == gold &&
                receipt.WalletRevision == 0,
                $"mount-gear socket {sockets} does not debit Gold");
            Check.Equal(
                target,
                (await ReadItemAsync(
                    connectionString,
                    fixture.CharacterId,
                    checked((short)fixture.TargetLocation),
                    fixture.TargetSlot))!.Value.Item,
                $"mount-gear socket {sockets} preserves target");
            AssertTerminalEvidence(
                await ReadStateAsync(
                    connectionString,
                    fixture,
                    HolyStoneCommandOperation.MountGearDrill),
                $"mount-gear socket {sockets} insufficient Gold",
                expectedGold: gold);
        }
    }

    private static async Task
        AssertMountGearDrillRejectsForeignSocketFamilyAsync(
            string connectionString)
    {
        var corruptTarget = SimpleItem(14504) with
        {
            SocketCount = 1,
            Socket1EffectId = 1,
            Socket1Level = 1,
            Socket1Value = 1
        };
        var fixture = await CreateFixtureAsync(
            connectionString,
            "mgfamily",
            target: corruptTarget,
            gold: 3000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        try
        {
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.MountGearDrill);
        }
        catch (InvalidDataException exception)
        {
            Check.True(
                exception.Message.Contains(
                    "corrupt Holy Stone state",
                    StringComparison.Ordinal),
                "mount-gear Drill explains a foreign socket family");
            await AssertMountGearDrillCorruptionRollbackAsync(
                connectionString,
                fixture,
                corruptTarget);
            return;
        }

        throw new InvalidOperationException(
            "Mount-gear Drill accepted a character-spirit socket family.");
    }

    private static async Task AssertMountGearDrillCorruptionRollbackAsync(
        string connectionString,
        HolyFixture fixture,
        CompactItemEntry corruptTarget)
    {
        Check.Equal(
            corruptTarget,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                checked((short)fixture.TargetLocation),
                fixture.TargetSlot))!.Value.Item,
            "foreign socket-family rejection preserves mount gear");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.MountGearDrill);
        Check.True(
            state.InventoryRevision == 0 &&
            state.WalletRevision == 0 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0,
            "foreign socket-family rejection rolls back all evidence");
    }
}
