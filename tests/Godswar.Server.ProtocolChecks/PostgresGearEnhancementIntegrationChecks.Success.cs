using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearEnhancementIntegrationChecks
{
    private static async Task AssertOperationSuccessesAsync(
        string connectionString)
    {
        foreach (var operation in Enum.GetValues<
                     GearEnhancementCommandOperation>())
        {
            var useOrigin =
                operation == GearEnhancementCommandOperation.Add;
            ItemSpec? catalystSpec = useOrigin
                ? ItemSpec.Create(
                    5,
                    GearEnhancementMaterialCatalog.FlameSparkItemId,
                    stack: 2,
                    bound: 1)
                : operation == GearEnhancementCommandOperation.Delete
                    ? ItemSpec.Create(
                        5,
                        GearEnhancementMaterialCatalog.WaterGrainItemId)
                    : null;
            ItemSpec? stoneSpec =
                operation == GearEnhancementCommandOperation.Delete
                    ? ItemSpec.Create(6, 9930)
                    : null;
            var fixture = await CreateFixtureAsync(
                connectionString,
                $"ok{operation}",
                operation,
                catalyst: catalystSpec,
                stone: stoneSpec,
                npcId: useOrigin
                    ? GearEnhancementCommandEnvelope
                        .SpartaOriginEnhancerNpcId
                    : null,
                dialogIndex: useOrigin
                    ? GearEnhancementCommandEnvelope
                        .OriginEnhancerDialogIndex
                    : null);
            await using var source =
                NpgsqlDataSource.Create(connectionString);
            var receipt = RequireReceipt(
                await ExecuteAsync(
                    CreateExecutor(source),
                    fixture,
                    Guid.NewGuid()),
                GearEnhancementExecutionDisposition.Committed,
                $"{operation} success");
            Check.True(
                receipt.Status ==
                    GearEnhancementCommandResultStatus.Succeeded &&
                receipt.Operation == operation &&
                receipt.NpcId == fixture.NpcId &&
                receipt.DialogIndex == fixture.DialogIndex &&
                receipt.InventoryRevision == 1 &&
                receipt.OutboxEventId.HasValue &&
                receipt.Mutations.Select(static mutation => mutation.Role)
                    .SequenceEqual(
                        [
                            GearEnhancementCommandItemRole.Gear,
                            GearEnhancementCommandItemRole.Catalyst,
                            GearEnhancementCommandItemRole.AttributeStone
                        ]),
                $"{operation} stores endpoint and exact role evidence");

            var gear = await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                checked((short)fixture.Gear.KitBagSlot));
            Check.True(
                operation switch
                {
                    GearEnhancementCommandOperation.Add =>
                        gear is
                        {
                            Attribute1: 0,
                            AttributeLevel1: 1,
                            Bound: 1,
                            Stack: 1
                        },
                    GearEnhancementCommandOperation.Enhance =>
                        gear is
                        {
                            Attribute1: 1,
                            AttributeLevel1: 2,
                            Stack: 1
                        },
                    GearEnhancementCommandOperation.Delete =>
                        gear is
                        {
                            Attribute1: null,
                            AttributeLevel1: null,
                            Stack: 1
                        },
                    _ => false
                },
                $"{operation} applies the authoritative gear mutation");
            var catalyst = await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                checked((short)fixture.Catalyst.KitBagSlot));
            var stone = await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                checked((short)fixture.Stone.KitBagSlot));
            Check.True(
                operation == GearEnhancementCommandOperation.Delete
                    ? catalyst is null && stone is null
                    : catalyst?.Stack == 1 && stone?.Stack == 1,
                $"{operation} consumes exactly one catalyst and stone");
            var state = await ReadStateAsync(connectionString, fixture);
            Check.True(
                state.InventoryRevision == 1 &&
                state.AuditCount == 1 &&
                state.InboxCount == 1 &&
                state.LedgerCount == 3 &&
                state.OutboxCount == 1 &&
                state.RejectedInboxCount == 0,
                $"{operation} commits one exact durable transaction: " +
                state);
            var ledger = await ReadLedgerAsync(
                connectionString,
                fixture);
            Check.True(
                ledger.Select(static entry => entry.Ordinal)
                    .SequenceEqual(new short[] { 0, 1, 2 }) &&
                ledger.Select(static entry => entry.ItemId)
                    .SequenceEqual(
                        receipt.Mutations.Select(
                            static mutation => mutation.ItemId)) &&
                ledger.All(entry =>
                    string.Equals(
                        entry.ReasonCode,
                        GearEnhancementPersistenceCodec.LedgerReasonCode(
                            receipt.Family),
                        StringComparison.Ordinal)) &&
                (operation == GearEnhancementCommandOperation.Delete
                    ? ledger.Select(static entry => entry.MutationKind)
                        .SequenceEqual(
                            ["update", "delete", "delete"])
                    : ledger.All(static entry =>
                        entry.MutationKind == "update")),
                $"{operation} ledger preserves Gear/Catalyst/Stone order");
        }
    }

    private static async Task AssertReplayConflictAndRejectionAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "replay",
            GearEnhancementCommandOperation.Add);
        var operationId = Guid.NewGuid();
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source);
        var committed = RequireReceipt(
            await ExecuteAsync(executor, fixture, operationId),
            GearEnhancementExecutionDisposition.Committed,
            "first Add");
        var duplicate = RequireReceipt(
            await ExecuteAsync(executor, fixture, operationId),
            GearEnhancementExecutionDisposition.Duplicate,
            "same-request Add retry");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "same-request retry returns exact stored receipt");
        var replay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                PlayerOwnershipTestFences.ForCharacter(
                    fixture.CharacterId),
                fixture.Operation,
                operationId),
            GearEnhancementExecutionDisposition.Duplicate,
            "explicit Add replay");
        AssertReceiptsEqual(
            committed,
            replay,
            "explicit replay returns exact stored receipt");
        var missingOtherFamily = await executor.TryReplayAsync(
            fixture.Subject,
            PlayerOwnershipTestFences.ForCharacter(
                fixture.CharacterId),
            GearEnhancementCommandOperation.Enhance,
            operationId);
        Check.Equal(
            (int)GearEnhancementExecutionDisposition.ReplayNotFound,
            (int)missingOtherFamily.Disposition,
            "operation families do not share replay identity");

        var conflict = await ExecuteAsync(
            executor,
            fixture,
            operationId,
            npcId: GearEnhancementCommandEnvelope.AthensGearMentorNpcId);
        Check.Equal(
            (int)GearEnhancementExecutionDisposition.RequestHashConflict,
            (int)conflict.Disposition,
            "same UUID with a different endpoint conflicts");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.LedgerCount == 3 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 2 &&
            state.ConflictCount == 1,
            "replay/conflict paths never repeat player-value mutation");

        // The PostgreSQL terminal branch is status-agnostic, so one
        // representative proves audit/inbox-only atomicity. The pure planner
        // suite covers rejection production, while the command-contract suite
        // exhaustively verifies every reachable operation/status/native map.
        var rejectedFixture = await CreateFixtureAsync(
            connectionString,
            "reject",
            GearEnhancementCommandOperation.Add,
            catalyst: ItemSpec.Create(5, 9991, stack: 2));
        var rejected = RequireReceipt(
            await ExecuteAsync(
                executor,
                rejectedFixture,
                Guid.NewGuid()),
            GearEnhancementExecutionDisposition.TerminalRejected,
            "wrong Add catalyst");
        Check.True(
            rejected.Status ==
                GearEnhancementCommandResultStatus.InvalidCatalyst &&
            rejected.NativeResultSubId ==
                GearEnhancementNativeResults.MissingFlameSparkSubId &&
            rejected.Mutations.IsEmpty &&
            rejected.OutboxEventId is null,
            "terminal rejection is precise and has no mutation evidence");
        var rejectedState = await ReadStateAsync(
            connectionString,
            rejectedFixture);
        Check.True(
            rejectedState.InventoryRevision == 0 &&
            rejectedState.AuditCount == 1 &&
            rejectedState.InboxCount == 1 &&
            rejectedState.LedgerCount == 0 &&
            rejectedState.OutboxCount == 0 &&
            rejectedState.RejectedInboxCount == 1,
            "terminal rejection persists audit/inbox only");
    }

    private static async Task AssertConcurrentDuplicateAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "race",
            GearEnhancementCommandOperation.Add);
        var operationId = Guid.NewGuid();
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source);
        var results = await Task.WhenAll(
            ExecuteAsync(executor, fixture, operationId),
            ExecuteAsync(executor, fixture, operationId));
        Check.True(
            results.Count(static result =>
                result.Disposition ==
                    GearEnhancementExecutionDisposition.Committed) == 1 &&
            results.Count(static result =>
                result.Disposition ==
                    GearEnhancementExecutionDisposition.Duplicate) == 1,
            "concurrent identical UUID yields one commit and one replay");
        AssertReceiptsEqual(
            results[0].Receipt!,
            results[1].Receipt!,
            "concurrent duplicate returns exact stored receipt");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 3 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1,
            "concurrent duplicate creates one atomic mutation");
    }

    private static async Task AssertConcurrentDistinctAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "race2",
            GearEnhancementCommandOperation.Add);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source);
        var results = await Task.WhenAll(
            ExecuteAsync(executor, fixture, Guid.NewGuid()),
            ExecuteAsync(executor, fixture, Guid.NewGuid()));
        Check.True(
            results.Count(static result =>
                result.Disposition ==
                    GearEnhancementExecutionDisposition.Committed) == 1 &&
            results.Count(static result =>
                result.Disposition ==
                    GearEnhancementExecutionDisposition
                        .TerminalRejected) == 1 &&
            results.Single(static result =>
                result.Disposition ==
                    GearEnhancementExecutionDisposition
                        .TerminalRejected).Receipt?.Status ==
                GearEnhancementCommandResultStatus.StaleSelection,
            "distinct UUID race commits once and durably rejects stale input");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.AuditCount == 2 &&
            state.InboxCount == 2 &&
            state.LedgerCount == 3 &&
            state.OutboxCount == 1 &&
            state.RejectedInboxCount == 1,
            "distinct race loser cannot duplicate materials or revision");
    }
}
