using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresGearMentorMaterialConversionIntegrationChecks
{
    private static async Task AssertMutationBranchesAsync(
        string connectionString)
    {
        await AssertRemainderCreatesOutputAsync(
            connectionString,
            CommandFamily.GearMentorTransformCrystal,
            sourceItemId: 4234,
            sourceStack: 2,
            outputItemId: 4233,
            outputQuantity: 2);
        await AssertRemainderCreatesOutputAsync(
            connectionString,
            CommandFamily.GearMentorCombineGemPieces,
            sourceItemId: 4216,
            sourceStack: 100,
            outputItemId: 4215,
            outputQuantity: 1);
        await AssertExistingOutputStackAsync(
            connectionString,
            CommandFamily.GearMentorTransformCrystal,
            sourceItemId: 4234,
            sourceStack: 2,
            outputItemId: 4233,
            outputQuantity: 2,
            existingOutputStack: 5);
        await AssertExistingOutputStackAsync(
            connectionString,
            CommandFamily.GearMentorCombineGemPieces,
            sourceItemId: 4216,
            sourceStack: 100,
            outputItemId: 4215,
            outputQuantity: 1,
            existingOutputStack: 5);
    }

    private static async Task AssertRemainderCreatesOutputAsync(
        string connectionString,
        CommandFamily family,
        uint sourceItemId,
        short sourceStack,
        uint outputItemId,
        int outputQuantity)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "remain",
            family,
            sourceItemId,
            sourceStack,
            outputItemId,
            outputQuantity,
            isBound: true);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        _ = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(source),
                fixture,
                Guid.NewGuid()),
            GearMentorMaterialConversionExecutionDisposition.Committed,
            $"{family} remainder conversion");

        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.SourceQuantity ==
                sourceStack -
                (family ==
                    CommandFamily.GearMentorTransformCrystal
                    ? 1
                    : 99) &&
            state.OutputQuantity == outputQuantity &&
            state.TotalBagItemCount == 2 &&
            state.LedgerCount == 2 &&
            state.AddLedgerCount == 1 &&
            state.UpdateLedgerCount == 1 &&
            state.DeleteLedgerCount == 0 &&
            state.IsReconciled,
            $"{family} persists source remainder plus inserted output");
    }

    private static async Task AssertExistingOutputStackAsync(
        string connectionString,
        CommandFamily family,
        uint sourceItemId,
        short sourceStack,
        uint outputItemId,
        int outputQuantity,
        short existingOutputStack)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "stack",
            family,
            sourceItemId,
            sourceStack,
            outputItemId,
            outputQuantity,
            isBound: false,
            existingOutputStack: existingOutputStack);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        _ = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(source),
                fixture,
                Guid.NewGuid()),
            GearMentorMaterialConversionExecutionDisposition.Committed,
            $"{family} existing-stack conversion");

        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.SourceQuantity ==
                sourceStack -
                (family ==
                    CommandFamily.GearMentorTransformCrystal
                    ? 1
                    : 99) &&
            state.OutputQuantity ==
                existingOutputStack + outputQuantity &&
            state.OutputBound == 0 &&
            state.TotalBagItemCount == 2 &&
            state.LedgerCount == 2 &&
            state.AddLedgerCount == 0 &&
            state.UpdateLedgerCount == 2 &&
            state.DeleteLedgerCount == 0 &&
            state.IsReconciled,
            $"{family} fills a compatible bound stack before empty slots");
    }
}
