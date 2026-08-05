using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static void AssertDrillEligibilityPolicy()
    {
        AssertBasicEligibility(
            1008,
            sockets: 0,
            HolyStoneDrillEligibilityFailure.ItemLevel,
            "level 90 cannot open socket one");
        AssertBasicEligibility(
            1009,
            sockets: 0,
            HolyStoneDrillEligibilityFailure.None,
            "level 100 can open socket one");
        AssertBasicEligibility(
            1010,
            sockets: 1,
            HolyStoneDrillEligibilityFailure.ItemLevel,
            "level 110 cannot open socket two");
        AssertBasicEligibility(
            1013,
            sockets: 1,
            HolyStoneDrillEligibilityFailure.None,
            "level 120 can open socket two");
        AssertBasicEligibility(
            1035,
            sockets: 0,
            HolyStoneDrillEligibilityFailure.None,
            "higher level remains eligible for socket one");
        AssertBasicEligibility(
            1035,
            sockets: 1,
            HolyStoneDrillEligibilityFailure.None,
            "higher level remains eligible for socket two");

        CheckAdvancedEligibility(
            AdvancedGear(sockets: 1),
            SimpleItem(4272),
            HolyStoneDrillEligibilityFailure.SocketPrerequisite,
            "advanced drilling requires the first two sockets");
        CheckAdvancedEligibility(
            AdvancedGear(sockets: 2),
            SimpleItem(4271),
            HolyStoneDrillEligibilityFailure.SocketSpell,
            "socket three requires Socket Spell III");
        CheckAdvancedEligibility(
            AdvancedGear(sockets: 2),
            SimpleItem(4272),
            HolyStoneDrillEligibilityFailure.None,
            "level 140 gear accepts Socket Spell III");
        CheckAdvancedEligibility(
            AdvancedGear(sockets: 2, id: 1035),
            SimpleItem(4272),
            HolyStoneDrillEligibilityFailure.None,
            "gear above level 140 remains eligible for socket three");
        CheckAdvancedEligibility(
            AdvancedGear(
                sockets: 3,
                quality: 15,
                grade: 20,
                holySuitCode: 510),
            SimpleItem(4273),
            HolyStoneDrillEligibilityFailure.FourthSocketEquipment,
            "Mithril gear cannot open socket four");
        CheckAdvancedEligibility(
            AdvancedGear(
                sockets: 3,
                quality: 15,
                grade: 20,
                holySuitCode: 600),
            SimpleItem(4273),
            HolyStoneDrillEligibilityFailure.FourthSocketEquipment,
            "Orichalcum level zero cannot open socket four");
        CheckAdvancedEligibility(
            AdvancedGear(
                sockets: 3,
                quality: 15,
                grade: 20,
                holySuitCode: 601),
            SimpleItem(4273),
            HolyStoneDrillEligibilityFailure.None,
            "Orichalcum level one opens socket four");
        CheckAdvancedEligibility(
            AdvancedGear(
                sockets: 3,
                quality: 15,
                grade: 20,
                holySuitCode: 700),
            SimpleItem(4273),
            HolyStoneDrillEligibilityFailure.None,
            "ware above Orichalcum opens socket four");
        CheckAdvancedEligibility(
            AdvancedGear(
                sockets: 3,
                quality: 14,
                grade: 20,
                holySuitCode: 601),
            SimpleItem(4273),
            HolyStoneDrillEligibilityFailure.FourthSocketEquipment,
            "quality below Arcane cannot open socket four");
        CheckAdvancedEligibility(
            AdvancedGear(
                sockets: 3,
                quality: 15,
                grade: 19,
                holySuitCode: 601),
            SimpleItem(4273),
            HolyStoneDrillEligibilityFailure.FourthSocketEquipment,
            "grade below 20 cannot open socket four");
        CheckAdvancedEligibility(
            AdvancedGear(
                sockets: 4,
                quality: 15,
                grade: 20,
                holySuitCode: 601),
            SimpleItem(4273),
            HolyStoneDrillEligibilityFailure.MaximumSockets,
            "four sockets is the advanced maximum");
    }

    private static void AssertBasicEligibility(
        uint itemId,
        short sockets,
        HolyStoneDrillEligibilityFailure expected,
        string description)
    {
        var actual = HolyStoneDrillEligibilityPolicy.ValidateBasic(
            RequireTemplate(itemId),
            AdvancedGear(sockets, id: itemId));
        Check.Equal((int)expected, (int)actual, description);
    }

    private static void CheckAdvancedEligibility(
        CompactItemEntry target,
        CompactItemEntry spell,
        HolyStoneDrillEligibilityFailure expected,
        string description)
    {
        var actual = HolyStoneDrillEligibilityPolicy.ValidateAdvanced(
            RequireTemplate(target.Id),
            target,
            spell);
        Check.Equal((int)expected, (int)actual, description);
    }

    private static ItemTemplateDefinition RequireTemplate(uint itemId)
    {
        Check.True(
            TestItemContent.Catalog.TryGet(itemId, out var template),
            $"item template {itemId} exists");
        return template!;
    }

    private static async Task AssertAdvancedDrillAsync(
        string connectionString)
    {
        await AssertThirdSocketDrillAsync(connectionString);
        await AssertFourthSocketDrillAsync(connectionString);
        await AssertAdvancedDrillRejectionsAsync(connectionString);
        await AssertAdvancedDrillReplayLockingAsync(connectionString);
        await AssertBasicDrillLevelGatesAsync(connectionString);
    }

    private static async Task AssertThirdSocketDrillAsync(
        string connectionString)
    {
        var target = AdvancedGear(sockets: 2);
        var spell = SimpleItem(4272, stack: 2);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "adv3",
            target: target,
            stone: spell,
            gold: 3000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.AdvancedDrill),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Drilled,
            "third socket advanced drill");
        Check.True(
            receipt.NativeResultSubId == 1500 &&
            receipt.SocketIndex == 2 &&
            receipt.GoldSpent == 0 &&
            receipt.GoldBefore == 3000 &&
            receipt.GoldAfter == 3000 &&
            receipt.StoneItemInstanceId == fixture.StoneItemId,
            "third socket receipt binds free drilling and its spell");

        var targetAfter = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            checked((short)fixture.TargetLocation),
            fixture.TargetSlot))!.Value;
        var spellAfter = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.StoneSlot))!.Value;
        Check.True(
            targetAfter.Id == fixture.TargetItemId &&
            targetAfter.Item.SocketCount == 3 &&
            spellAfter.Id == fixture.StoneItemId &&
            spellAfter.Item.Id == 4272 &&
            spellAfter.Item.Stack == 1,
            "third socket consumes exactly one Socket Spell III");
        AssertCommittedEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.AdvancedDrill),
            expectedLedger: 2,
            "third socket advanced drill");
    }

    private static async Task AssertFourthSocketDrillAsync(
        string connectionString)
    {
        var target = AdvancedGear(
            sockets: 3,
            quality: 15,
            grade: 20,
            holySuitCode: 601);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "adv4",
            target: target,
            stone: SimpleItem(4273),
            gold: 3000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.AdvancedDrill),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Drilled,
            "fourth socket advanced drill");
        Check.True(
            receipt.NativeResultSubId == 1500 &&
            receipt.SocketIndex == 3 &&
            receipt.GoldSpent == 0 &&
            receipt.AuthoritativeStoneAfterCompactItemState == "[]",
            "fourth socket receipt records the exhausted Socket Spell IV");
        var targetAfter = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            checked((short)fixture.TargetLocation),
            fixture.TargetSlot))!.Value;
        var spellAfter = await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.StoneSlot);
        Check.True(
            targetAfter.Item.SocketCount == 4 &&
            spellAfter is null,
            "fourth socket atomically deletes the consumed Socket Spell IV");
        AssertCommittedEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.AdvancedDrill),
            expectedLedger: 2,
            "fourth socket advanced drill");
    }

    private static async Task AssertAdvancedDrillRejectionsAsync(
        string connectionString)
    {
        await AssertAdvancedRejectionAsync(
            connectionString,
            "badsp",
            AdvancedGear(sockets: 2),
            SimpleItem(4273),
            HolyStoneCommandResultStatus.StoneNotHolyStone,
            nativeResultSubId: 2800,
            "wrong Socket Spell");
        await AssertAdvancedRejectionAsync(
            connectionString,
            "prereq",
            AdvancedGear(
                sockets: 3,
                quality: 14,
                grade: 20,
                holySuitCode: 601),
            SimpleItem(4273),
            HolyStoneCommandResultStatus.DrillPrerequisite,
            nativeResultSubId: 3000,
            "fourth socket prerequisite");
        await AssertAdvancedRejectionAsync(
            connectionString,
            "max4",
            AdvancedGear(
                sockets: 4,
                quality: 15,
                grade: 20,
                holySuitCode: 601),
            SimpleItem(4273),
            HolyStoneCommandResultStatus.MaximumSockets,
            nativeResultSubId: 2900,
            "advanced maximum sockets");
    }

    private static async Task AssertAdvancedRejectionAsync(
        string connectionString,
        string scenario,
        CompactItemEntry target,
        CompactItemEntry spell,
        HolyStoneCommandResultStatus status,
        int nativeResultSubId,
        string description)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            scenario,
            target: target,
            stone: spell,
            gold: 3000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.AdvancedDrill),
            HolyStoneExecutionDisposition.TerminalRejected,
            status,
            description);
        Check.True(
            receipt.NativeResultSubId == nativeResultSubId &&
            receipt.GoldSpent == 0 &&
            receipt.AuthoritativeTargetBeforeCompactItemState ==
                receipt.AuthoritativeTargetAfterCompactItemState &&
            receipt.AuthoritativeStoneBeforeCompactItemState ==
                receipt.AuthoritativeStoneAfterCompactItemState,
            $"{description} preserves target, spell, and Gold");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.AdvancedDrill),
            description,
            expectedGold: 3000);
    }

    private static async Task AssertAdvancedDrillReplayLockingAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "advrpl",
            target: AdvancedGear(sockets: 2),
            stone: SimpleItem(4272),
            gold: 3000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var operationId = Guid.NewGuid();
        var results = await Task.WhenAll(
            ExecuteAsync(
                executor,
                fixture,
                operationId,
                HolyStoneCommandOperation.AdvancedDrill),
            ExecuteAsync(
                executor,
                fixture,
                operationId,
                HolyStoneCommandOperation.AdvancedDrill));
        Check.True(
            results.Count(result =>
                result.Disposition ==
                    HolyStoneExecutionDisposition.Committed) == 1 &&
            results.Count(result =>
                result.Disposition ==
                    HolyStoneExecutionDisposition.Duplicate) == 1,
            "concurrent advanced replay commits exactly once");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.AdvancedDrill);
        Check.True(
            state.InventoryRevision == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 2 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1 &&
            state.WalletRevision == 0 &&
            state.Gold == 3000,
            "advanced replay retains one atomic target/material mutation");
        var targetAfter = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            checked((short)fixture.TargetLocation),
            fixture.TargetSlot))!.Value;
        var spellAfter = await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.StoneSlot);
        Check.True(
            targetAfter.Item.SocketCount == 3 && spellAfter is null,
            "advanced replay cannot duplicate a socket or its spell");
    }

    private static async Task AssertBasicDrillLevelGatesAsync(
        string connectionString)
    {
        await AssertBasicLevelRejectionAsync(
            connectionString,
            "lvl90",
            AdvancedGear(sockets: 0, id: 1008),
            "socket one level gate");
        await AssertBasicLevelRejectionAsync(
            connectionString,
            "lvl110",
            AdvancedGear(sockets: 1, id: 1010),
            "socket two level gate");
    }

    private static async Task AssertBasicLevelRejectionAsync(
        string connectionString,
        string scenario,
        CompactItemEntry target,
        string description)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            scenario,
            target: target,
            gold: 3000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Drill),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.DrillPrerequisite,
            description);
        Check.True(
            receipt.NativeResultSubId == 3000 &&
            receipt.GoldSpent == 0,
            $"{description} reports the authoritative prerequisite result");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Drill),
            description,
            expectedGold: 3000);
    }

    private static CompactItemEntry AdvancedGear(
        short sockets,
        short quality = 1,
        short grade = 1,
        int holySuitCode = 0,
        uint id = 2333) =>
        CompactItemEntry.Empty with
        {
            Id = id,
            Quality = quality,
            Grade = grade,
            Bound = 1,
            Stack = 1,
            HolySuitCode = holySuitCode,
            SocketCount = sockets
        };
}
