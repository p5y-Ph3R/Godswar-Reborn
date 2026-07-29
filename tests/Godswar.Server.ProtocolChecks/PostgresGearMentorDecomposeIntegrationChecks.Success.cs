using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearMentorDecomposeIntegrationChecks
{
    private static async Task AssertSuccessReplayAndConflictAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "success",
            [
                new GearSpec(10, 1004, Bound: 0, Attribute1: 0),
                new GearSpec(11, 1005, Bound: 1, Attribute1: 20),
                new GearSpec(12, 1006, Bound: 0, Attribute1: 40)
            ]);
        var operationId = Guid.NewGuid();
        var random = new CountingRandomSource();
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source, random);
        var committed = RequireReceipt(
            await ExecuteAsync(executor, fixture, operationId),
            GearMentorDecomposeGearExecutionDisposition.Committed,
            "three-gear Decompose");
        Check.True(
            committed.Status ==
                GearMentorDecomposeGearResultStatus.Succeeded &&
            committed.InventoryRevision == 1 &&
            committed.OutboxEventId.HasValue &&
            committed.Selections.Select(static value => value.SourceItemId)
                .SequenceEqual(new uint[] { 1004, 1005, 1006 }) &&
            committed.DustOutcomes.Select(static value => value.DustItemId)
                .SequenceEqual(new uint[] { 9900, 9902, 9910 }) &&
            committed.DustOutcomes.Select(static value => value.Quantity)
                .SequenceEqual(new[] { 2, 2, 2 }) &&
            committed.DustOutcomes.Select(static value => value.Bound)
                .SequenceEqual(new short[] { 0, 1, 0 }),
            "three-gear Decompose persists exact per-source Dust");
        Check.Equal(
            3,
            random.CallCount,
            "first execution selects one random Dust per gear");

        var items = await ReadItemsAsync(
            connectionString,
            fixture.CharacterId);
        Check.True(
            items.Select(static item => item.ItemId)
                .SequenceEqual(new uint[] { 9900, 9902, 9910 }) &&
            items.All(static item =>
                item.Quality == 1 &&
                item.Grade == 1 &&
                item.Stack == 2),
            "selected gear is atomically replaced by exact Dust");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 6 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 0 &&
            state.ConflictCount == 0,
            "success writes one revision, inbox, audit, and outbox: " +
            state);

        var duplicate = RequireReceipt(
            await ExecuteAsync(executor, fixture, operationId),
            GearMentorDecomposeGearExecutionDisposition.Duplicate,
            "same-request duplicate");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "same-request duplicate returns the exact stored outcome");
        Check.Equal(
            3,
            random.CallCount,
            "same-request duplicate never rerolls Dust");

        var replay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                operationId),
            GearMentorDecomposeGearExecutionDisposition.Duplicate,
            "explicit Decompose replay");
        AssertReceiptsEqual(
            committed,
            replay,
            "explicit replay returns the exact stored outcome");
        Check.Equal(
            3,
            random.CallCount,
            "explicit replay never invokes randomness");

        var conflict = await ExecuteAsync(
            executor,
            fixture,
            operationId,
            GearMentorDecomposeGearCommandEnvelope
                .AthensGearMentorNpcId);
        Check.Equal(
            (int)GearMentorDecomposeGearExecutionDisposition
                .RequestHashConflict,
            (int)conflict.Disposition,
            "same UUID with a different NPC conflicts");
        Check.Equal(
            3,
            random.CallCount,
            "request conflict never rerolls Dust");
        state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 6 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 2 &&
            state.ConflictCount == 1,
            "duplicate and conflict only update bounded inbox counters");
    }

    private static async Task AssertMutationBranchesAsync(
        string connectionString)
    {
        await AssertPartialStackSplitAsync(connectionString);
        await AssertSameDustCoalescingAsync(connectionString);
        await AssertBindingSeparationAsync(connectionString);
        await AssertFullBagUsesFreedCapacityAsync(connectionString);
    }

    private static async Task AssertPartialStackSplitAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "split",
            [new GearSpec(10, 1004, Bound: 1, Attribute1: 0)],
            otherItems:
            [
                new GearSpec(
                    0,
                    9900,
                    Quality: 1,
                    Grade: 1,
                    Bound: 1,
                    Attribute1: null,
                    Stack: 98)
            ]);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(source, new CountingRandomSource()),
                fixture,
                Guid.NewGuid()),
            GearMentorDecomposeGearExecutionDisposition.Committed,
            "partial-stack Decompose");
        Check.Equal(
            2,
            receipt.DustOutcomes.Single().Quantity,
            "quality/grade determines the exact Dust quantity");
        var items = await ReadItemsAsync(
            connectionString,
            fixture.CharacterId);
        Check.True(
            items.Count == 2 &&
            items[0] == new StoredItem(0, 9900, 1, 1, 1, 99) &&
            items[1] == new StoredItem(1, 9900, 1, 1, 1, 1),
            "Dust first fills a compatible stack then uses first empty slot");
        Check.Equal(
            3L,
            (await ReadStateAsync(connectionString, fixture)).LedgerCount,
            "stack update, output add, and source delete are ledgered");
    }

    private static async Task AssertSameDustCoalescingAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "merge",
            [
                new GearSpec(20, 1004, Attribute1: 0),
                new GearSpec(21, 1005, Attribute1: 0),
                new GearSpec(22, 1006, Attribute1: 0)
            ]);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(source, new CountingRandomSource()),
                fixture,
                Guid.NewGuid()),
            GearMentorDecomposeGearExecutionDisposition.Committed,
            "same-Dust multi-selection");
        Check.True(
            receipt.DustOutcomes.Length == 3 &&
            receipt.DustOutcomes.All(static value =>
                value.DustItemId == 9900 &&
                value.Quantity == 2),
            "receipt preserves every random outcome even when stacks merge");
        var items = await ReadItemsAsync(
            connectionString,
            fixture.CharacterId);
        Check.True(
            items.SequenceEqual(
                [new StoredItem(0, 9900, 1, 1, 1, 6)]),
            "same bound Dust outputs coalesce deterministically");
        Check.Equal(
            4L,
            (await ReadStateAsync(connectionString, fixture)).LedgerCount,
            "one add and three deletions share one inventory revision");
    }

    private static async Task AssertBindingSeparationAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "bound",
            [new GearSpec(10, 1004, Bound: 1, Attribute1: 0)],
            otherItems:
            [
                new GearSpec(
                    0,
                    9900,
                    Quality: 1,
                    Grade: 1,
                    Bound: 0,
                    Attribute1: null,
                    Stack: 20)
            ]);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        _ = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(source, new CountingRandomSource()),
                fixture,
                Guid.NewGuid()),
            GearMentorDecomposeGearExecutionDisposition.Committed,
            "bound Dust separation");
        var items = await ReadItemsAsync(
            connectionString,
            fixture.CharacterId);
        Check.True(
            items.Count == 2 &&
            items[0].ItemId == 9900 &&
            items[0].Bound == 0 &&
            items[0].Stack == 20 &&
            items[1].ItemId == 9900 &&
            items[1].Bound == 1 &&
            items[1].Stack == 2,
            "bound Dust never merges into an unbound stack");
    }

    private static async Task AssertFullBagUsesFreedCapacityAsync(
        string connectionString)
    {
        const short selectedSlot = 4;
        var filler = Enumerable.Range(0, 96)
            .Where(static slot => slot != selectedSlot)
            .Select(static slot =>
                new GearSpec(
                    checked((short)slot),
                    1000,
                    Quality: 1,
                    Grade: 1,
                    Bound: 0,
                    Attribute1: null))
            .ToArray();
        var fixture = await CreateFixtureAsync(
            connectionString,
            "full",
            [
                new GearSpec(
                    selectedSlot,
                    1004,
                    Quality: 13,
                    Grade: 25,
                    Bound: 1,
                    Attribute1: 0)
            ],
            otherItems: filler);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(source, new CountingRandomSource()),
                fixture,
                Guid.NewGuid()),
            GearMentorDecomposeGearExecutionDisposition.Committed,
            "full-bag Decompose");
        Check.True(
            receipt.DustOutcomes.Single().Quantity == 37 &&
            receipt.DustOutcomes.Single().DustItemId == 9900,
            "full bag retains exact high-quality Dust outcome");
        var items = await ReadItemsAsync(
            connectionString,
            fixture.CharacterId);
        Check.True(
            items.Count == 96 &&
            items.Single(static item => item.Slot == selectedSlot) ==
                new StoredItem(selectedSlot, 9900, 1, 1, 1, 37),
            "selected gear slot guarantees output capacity in a full bag");
        Check.Equal(
            1L,
            (await ReadStateAsync(connectionString, fixture)).LedgerCount,
            "full-bag replacement is one authoritative update");
    }
}
