using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresClassSuitCommandIntegrationChecks
{
    private static async Task AssertCommitReplayAndConflictAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "commit");
        var operationId = Guid.NewGuid();
        ClassSuitExecutionReceipt committed;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            committed = RequireReceipt(
                await ExecuteAsync(
                    CreateExecutor(source),
                    fixture,
                    operationId),
                ClassSuitExecutionDisposition.Committed,
                "Class Suit first execution");
        }

        Check.True(
            committed.Family == CommandFamily.ClassSuitExchangeTierI &&
            committed.Operation == ClassSuitCommandOperation.ExchangeTierI &&
            committed.Status == ClassSuitCommandResultStatus.Succeeded &&
            committed.NativeResultSubId == 120 &&
            committed.InventoryRevision == 1 &&
            committed.OutboxEventId.HasValue &&
            committed.Mutations.Count == 2,
            "Class Suit commit returns canonical durable evidence");
        Check.True(
            committed.Mutations.Any(static mutation =>
                mutation.KitBagSlot == GearSlot &&
                mutation.BeforeItemId == 1013 &&
                mutation.AfterItemId == 1032) &&
            committed.Mutations.Any(static mutation =>
                mutation.KitBagSlot == InsigniaSlot &&
                mutation.BeforeItemId ==
                    ClassSuitConversionCatalog.PromotionalInsigniaI &&
                mutation.AfterItemId ==
                    ClassSuitConversionCatalog.PromotionalInsigniaI),
            "Class Suit receipt covers gear replacement and insignia consumption");

        var committedState = await ReadStateAsync(
            connectionString,
            fixture);
        AssertCommittedState(
            committedState,
            "Class Suit conversion atomically commits all durable state");

        ClassSuitExecutionReceipt duplicate;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            duplicate = RequireReceipt(
                await ExecuteAsync(
                    CreateExecutor(source),
                    fixture,
                    operationId),
                ClassSuitExecutionDisposition.Duplicate,
                "Class Suit duplicate UUID");
        }
        AssertReceiptsEqual(
            committed,
            duplicate,
            "Class Suit duplicate UUID replays the original receipt");

        ClassSuitExecutionReceipt stableIntentReplay;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            stableIntentReplay = RequireReceipt(
                await ReplayAsync(
                    CreateExecutor(source),
                    fixture,
                    operationId),
                ClassSuitExecutionDisposition.Duplicate,
                "Class Suit stable-intent replay");
        }
        AssertReceiptsEqual(
            committed,
            stableIntentReplay,
            "Class Suit retry survives post-commit item snapshot changes");

        ClassSuitExecutionResult endpointConflict;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            endpointConflict = await ReplayAsync(
                CreateExecutor(source),
                fixture,
                operationId,
                npcId: ClassSuitCommandEnvelope.AthensNpcId);
        }
        Check.True(
            endpointConflict.Disposition ==
                ClassSuitExecutionDisposition.RequestHashConflict &&
            endpointConflict.Receipt is null,
            "Class Suit replay rejects one UUID at a different NPC endpoint");

        ClassSuitExecutionResult slotConflict;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            slotConflict = await ReplayAsync(
                CreateExecutor(source),
                fixture,
                operationId,
                gearSlot: GearSlot + 2);
        }
        Check.True(
            slotConflict.Disposition ==
                ClassSuitExecutionDisposition.RequestHashConflict &&
            slotConflict.Receipt is null,
            "Class Suit replay rejects one UUID with different selected slots");

        ClassSuitExecutionResult requestHashConflict;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            requestHashConflict = await ExecuteAsync(
                CreateExecutor(source),
                fixture,
                operationId,
                npcId: ClassSuitCommandEnvelope.AthensNpcId);
        }
        Check.True(
            requestHashConflict.Disposition ==
                ClassSuitExecutionDisposition.RequestHashConflict &&
            requestHashConflict.Receipt is null,
            "full command hash still rejects a changed retried request");

        var replayedState = await ReadStateAsync(
            connectionString,
            fixture);
        AssertCommittedState(
            replayedState,
            "Class Suit replay and conflict do not duplicate value");
        Check.True(
            replayedState.DuplicateCount == 2 &&
            replayedState.ConflictCount == 3,
            "Class Suit inbox records exact retries and all intent conflicts");
    }

    private static async Task AssertStaleSelectionIsAtomicAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "stale");
        var staleGear = CreateGear() with { Exp = 778 };
        ClassSuitExecutionReceipt receipt;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            receipt = RequireReceipt(
                await ExecuteAsync(
                    CreateExecutor(source),
                    fixture,
                    Guid.NewGuid(),
                    expectedGearState: staleGear.ToCompactString()),
                ClassSuitExecutionDisposition.TerminalRejected,
                "Class Suit stale selection");
        }

        Check.True(
            receipt.Status == ClassSuitCommandResultStatus.StaleSelection &&
            receipt.Mutations.Count == 0 &&
            !receipt.OutboxEventId.HasValue &&
            receipt.InventoryRevision == 0,
            "Class Suit stale selection is a durable non-mutation");
        AssertRejectedState(
            await ReadStateAsync(connectionString, fixture),
            fixture,
            "Class Suit stale selection cannot partially mutate inventory");
    }

    private static async Task AssertInsufficientInsigniaIsAtomicAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "short",
            insigniaStack: 2);
        ClassSuitExecutionReceipt receipt;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            receipt = RequireReceipt(
                await ExecuteAsync(
                    CreateExecutor(source),
                    fixture,
                    Guid.NewGuid()),
                ClassSuitExecutionDisposition.TerminalRejected,
                "Class Suit insufficient insignia");
        }

        Check.True(
            receipt.Status ==
                ClassSuitCommandResultStatus.InsufficientMaterial &&
            receipt.Mutations.Count == 0 &&
            !receipt.OutboxEventId.HasValue &&
            receipt.InventoryRevision == 0,
            "Class Suit insufficient insignia is a durable non-mutation");
        AssertRejectedState(
            await ReadStateAsync(connectionString, fixture),
            fixture,
            "Class Suit insufficient insignia consumes nothing");
    }

    private static void AssertCommittedState(
        ClassSuitDurableState state,
        string description)
    {
        var expectedGear = CreateGear() with
        {
            Id = 1032,
            Bound = 1
        };
        var expectedInsignia = CreateInsignia(stack: 2);
        Check.True(
            state.InventoryRevision == 1 &&
            state.Gear == expectedGear &&
            state.Insignia == expectedInsignia &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 2 &&
            state.OutboxCount == 1 &&
            state.CommittedInboxCount == 1 &&
            state.RejectedInboxCount == 0,
            description);
    }

    private static void AssertRejectedState(
        ClassSuitDurableState state,
        ClassSuitFixture fixture,
        string description)
    {
        Check.True(
            state.InventoryRevision == 0 &&
            state.Gear == CreateGear() &&
            state.Insignia ==
                CreateInsignia(fixture.InitialInsigniaStack) &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.ConflictCount == 0 &&
            state.CommittedInboxCount == 0 &&
            state.RejectedInboxCount == 1,
            description);
    }

    private static void AssertReceiptsEqual(
        ClassSuitExecutionReceipt expected,
        ClassSuitExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected.Family == actual.Family &&
            expected.CharacterId == actual.CharacterId &&
            expected.Operation == actual.Operation &&
            expected.NpcId == actual.NpcId &&
            expected.DialogIndex == actual.DialogIndex &&
            expected.ReplayIntent == actual.ReplayIntent &&
            expected.Status == actual.Status &&
            expected.NativeResultSubId == actual.NativeResultSubId &&
            expected.InventoryRevision == actual.InventoryRevision &&
            expected.AuditReference == actual.AuditReference &&
            expected.OutboxEventId == actual.OutboxEventId &&
            expected.Mutations.SequenceEqual(actual.Mutations),
            description);
    }
}
