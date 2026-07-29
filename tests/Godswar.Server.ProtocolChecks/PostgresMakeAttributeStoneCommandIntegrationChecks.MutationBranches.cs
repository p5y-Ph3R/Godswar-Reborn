using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresMakeAttributeStoneCommandIntegrationChecks
{
    private static async Task AssertRemainderAndInsertedOutputAsync(
        string connectionString)
    {
        const short oversizedDustStack = RecipeDustQuantity + 1;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "remain",
            oversizedDustStack);
        MakeAttributeStoneExecutionResult result;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            result = await CreateExecutor(source).ExecuteAsync(
                CreateEnvelope(fixture, Guid.NewGuid()));
        }

        var receipt = RequireReceipt(
            result,
            MakeAttributeStoneExecutionDisposition.Committed,
            "remainder-and-add Make Attribute Stone transaction");
        Check.True(
            receipt.SourceDustItemId == DustItemId &&
            receipt.OutputStoneItemId == AttributeStoneItemId &&
            receipt.InventoryRevision == 1 &&
            receipt.IsBound == fixture.IsBound,
            "oversized Dust recipe returns the canonical receipt");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.DustQuantity == 1 &&
            state.StoneQuantity == 1 &&
            state.StoneItemCount == 1 &&
            state.StoneBound == 1 &&
            state.StoneSlot == 1 &&
            state.TotalBagItemCount == 2 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 2 &&
            state.RecipeLedgerCount == 0 &&
            state.OutboxCount == 1 &&
            state.CommittedInboxCount == 1 &&
            state.RejectedInboxCount == 0 &&
            state.IsReconciled,
            "oversized Dust updates its remainder and inserts output");
        var shape =
            await ReadMutationShapeAsync(connectionString, fixture);
        Check.True(
            shape.AddCount == 1 &&
            shape.UpdateCount == 1 &&
            shape.DeleteCount == 0 &&
            shape.DustRemainderUpdateCount == 1 &&
            shape.StoneStackUpdateCount == 0,
            "remainder recipe journals one update and one add");
    }

    private static async Task AssertExistingStoneStackAsync(
        string connectionString)
    {
        const short existingStoneStack = 10;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "merge",
            RecipeDustQuantity,
            isBound: false,
            existingStoneStack: existingStoneStack);
        MakeAttributeStoneExecutionResult result;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            result = await CreateExecutor(source).ExecuteAsync(
                CreateEnvelope(fixture, Guid.NewGuid()));
        }

        var receipt = RequireReceipt(
            result,
            MakeAttributeStoneExecutionDisposition.Committed,
            "existing-stack Make Attribute Stone transaction");
        Check.True(
            receipt.IsBound == false &&
            receipt.InventoryRevision == 1 &&
            receipt.OutboxEventId.HasValue,
            "existing-stack recipe preserves unbound identity");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.DustQuantity == 0 &&
            state.StoneQuantity == existingStoneStack + 1 &&
            state.StoneItemCount == 1 &&
            state.StoneBound == 0 &&
            state.StoneSlot == 1 &&
            state.TotalBagItemCount == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 2 &&
            state.RecipeLedgerCount == 0 &&
            state.OutboxCount == 1 &&
            state.CommittedInboxCount == 1 &&
            state.RejectedInboxCount == 0 &&
            state.IsReconciled,
            "exact Dust deletes its source and updates the compatible " +
            "Attribute Stone stack");
        var shape =
            await ReadMutationShapeAsync(connectionString, fixture);
        Check.True(
            shape.AddCount == 0 &&
            shape.UpdateCount == 1 &&
            shape.DeleteCount == 1 &&
            shape.DustRemainderUpdateCount == 0 &&
            shape.StoneStackUpdateCount == 1,
            "compatible-stack recipe journals one delete and one update");
    }
}
