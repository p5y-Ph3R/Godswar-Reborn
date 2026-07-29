using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresMakeAttributeStoneCommandIntegrationChecks
{
    private static async Task AssertInvalidDustRejectionAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "invalid",
            dustStack: 1,
            selectedItemId: FillerItemId);
        var receipt = await ExecuteTerminalAsync(
            connectionString,
            fixture,
            MakeAttributeStoneResultStatus.InvalidDust,
            "invalid-Dust transaction");
        Check.True(
            receipt.SourceDustItemId == FillerItemId &&
            receipt.OutputStoneItemId == 0 &&
            receipt.IsBound == fixture.IsBound,
            "invalid-Dust result preserves known source identity");
        AssertTerminalState(
            await ReadStateAsync(connectionString, fixture),
            expectedDustQuantity: 0,
            expectedBagItemCount: 1,
            "invalid Dust commits only terminal identity");
    }

    private static async Task
        AssertInsufficientCapacityRejectionAsync(
            string connectionString)
    {
        const short oversizedDustStack = RecipeDustQuantity + 1;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "full",
            oversizedDustStack,
            fillRemainingBag: true);
        var receipt = await ExecuteTerminalAsync(
            connectionString,
            fixture,
            MakeAttributeStoneResultStatus.InsufficientCapacity,
            "full-bag Make Attribute Stone transaction");
        Check.True(
            receipt.SourceDustItemId == DustItemId &&
            receipt.OutputStoneItemId == AttributeStoneItemId &&
            receipt.IsBound == fixture.IsBound,
            "capacity result preserves the resolved recipe identity");
        AssertTerminalState(
            await ReadStateAsync(connectionString, fixture),
            oversizedDustStack,
            expectedBagItemCount: 96,
            "full bag consumes no Dust and publishes no output");
    }

    private static async Task
        AssertMissingAndStaleSelectionRejectionsAsync(
            string connectionString)
    {
        var missing = await CreateFixtureAsync(
            connectionString,
            "missing",
            includeSelectedItem: false);
        var missingReceipt = await ExecuteTerminalAsync(
            connectionString,
            missing,
            MakeAttributeStoneResultStatus.StaleSelection,
            "missing-selection transaction");
        Check.True(
            missingReceipt.SourceDustItemId == 0 &&
            missingReceipt.OutputStoneItemId == 0 &&
            missingReceipt.IsBound is null,
            "missing selection exposes no stale item identity");
        AssertTerminalState(
            await ReadStateAsync(connectionString, missing),
            expectedDustQuantity: 0,
            expectedBagItemCount: 0,
            "missing selection commits only terminal identity");

        const short actualDustStack = RecipeDustQuantity - 1;
        var stale = await CreateFixtureAsync(
            connectionString,
            "stale",
            actualDustStack,
            expectedSelectedStack: RecipeDustQuantity);
        var staleReceipt = await ExecuteTerminalAsync(
            connectionString,
            stale,
            MakeAttributeStoneResultStatus.StaleSelection,
            "stale-selection transaction");
        Check.True(
            staleReceipt.SourceDustItemId == 0 &&
            staleReceipt.OutputStoneItemId == 0 &&
            staleReceipt.IsBound is null,
            "stale selection exposes no replaced item identity");
        AssertTerminalState(
            await ReadStateAsync(connectionString, stale),
            actualDustStack,
            expectedBagItemCount: 1,
            "stale selection leaves the changed Dust untouched");
    }

    private static async Task<MakeAttributeStoneExecutionReceipt>
        ExecuteTerminalAsync(
            string connectionString,
            StoneFixture fixture,
            MakeAttributeStoneResultStatus expectedStatus,
            string description)
    {
        MakeAttributeStoneExecutionResult result;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            result = await CreateExecutor(source).ExecuteAsync(
                CreateEnvelope(fixture, Guid.NewGuid()));
        }

        var receipt = RequireReceipt(
            result,
            MakeAttributeStoneExecutionDisposition.TerminalRejected,
            description);
        Check.True(
            receipt.Status == expectedStatus &&
            receipt.NativeResultSubId ==
                MakeAttributeStoneNativeResults.GetResultSubId(
                    expectedStatus) &&
            receipt.InventoryRevision == 0 &&
            receipt.OutboxEventId is null,
            $"{description} returns its canonical durable result");
        return receipt;
    }

    private static void AssertTerminalState(
        StoneDurableState state,
        long expectedDustQuantity,
        long expectedBagItemCount,
        string description)
    {
        Check.True(
            state.InventoryRevision == 0 &&
            state.DustQuantity == expectedDustQuantity &&
            state.StoneQuantity == 0 &&
            state.StoneItemCount == 0 &&
            state.StoneBound == -1 &&
            state.StoneSlot == -1 &&
            state.TotalBagItemCount == expectedBagItemCount &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 0 &&
            state.RecipeLedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.ConflictCount == 0 &&
            state.CommittedInboxCount == 0 &&
            state.RejectedInboxCount == 1 &&
            state.IsReconciled,
            description);
    }
}
