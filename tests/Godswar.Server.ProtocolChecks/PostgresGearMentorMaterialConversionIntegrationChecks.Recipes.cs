using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresGearMentorMaterialConversionIntegrationChecks
{
    private static async Task AssertTransformRecipesAsync(
        string connectionString)
    {
        var recipes = new[]
        {
            new RecipeCase(4234u, 4233u, 2, true),
            new RecipeCase(4233u, 4232u, 2, false),
            new RecipeCase(4232u, 4231u, 4, true),
            new RecipeCase(4231u, 4230u, 8, false)
        };
        foreach (var recipe in recipes)
        {
            await AssertCommittedRecipeAsync(
                connectionString,
                CommandFamily.GearMentorTransformCrystal,
                recipe,
                $"Transform {recipe.SourceItemId}");
        }
    }

    private static async Task AssertCombineRecipesAsync(
        string connectionString)
    {
        var recipes = new[]
        {
            new RecipeCase(4214u, 4213u, 1, true),
            new RecipeCase(4224u, 4223u, 1, false),
            new RecipeCase(4216u, 4215u, 1, true),
            new RecipeCase(4226u, 4225u, 1, false),
            new RecipeCase(4235u, 4234u, 1, true)
        };
        foreach (var recipe in recipes)
        {
            await AssertCommittedRecipeAsync(
                connectionString,
                CommandFamily.GearMentorCombineGemPieces,
                recipe,
                $"Combine {recipe.SourceItemId}");
        }
    }

    private static async Task AssertCommittedRecipeAsync(
        string connectionString,
        CommandFamily family,
        RecipeCase recipe,
        string description)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            family == CommandFamily.GearMentorTransformCrystal
                ? "trecip"
                : "crecip",
            family,
            recipe.SourceItemId,
            family == CommandFamily.GearMentorTransformCrystal
                ? (short)1
                : (short)99,
            recipe.OutputItemId,
            recipe.OutputQuantity,
            recipe.IsBound);

        GearMentorMaterialConversionExecutionResult result;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            result = await ExecuteAsync(
                CreateExecutor(source),
                fixture,
                Guid.NewGuid());
        }

        var receipt = RequireReceipt(
            result,
            GearMentorMaterialConversionExecutionDisposition.Committed,
            description);
        Check.True(
            receipt.Family == family &&
            receipt.Status ==
                GearMentorMaterialConversionResultStatus.Succeeded &&
            receipt.NativeResultSubId ==
                GearMentorMaterialConversionNativeResults.GetResultSubId(
                    family,
                    GearMentorMaterialConversionResultStatus.Succeeded) &&
            receipt.SelectedKitBagSlot == fixture.SelectedSlot &&
            receipt.SourceItemId == recipe.SourceItemId &&
            receipt.OutputItemId == recipe.OutputItemId &&
            receipt.OutputQuantity == recipe.OutputQuantity &&
            receipt.IsBound == recipe.IsBound &&
            receipt.InventoryRevision == 1 &&
            receipt.OutboxEventId.HasValue &&
            receipt.OutboxEventId.Value != Guid.Empty,
            $"{description} returns its canonical receipt");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.SourceQuantity == 0 &&
            state.OutputQuantity == recipe.OutputQuantity &&
            state.OutputBound == (recipe.IsBound ? 1 : 0) &&
            state.TotalBagItemCount == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 0 &&
            state.ConflictCount == 0 &&
            state.CommittedInboxCount == 1 &&
            state.RejectedInboxCount == 0 &&
            state.AddLedgerCount == 0 &&
            state.UpdateLedgerCount == 1 &&
            state.DeleteLedgerCount == 0 &&
            state.IsReconciled,
            $"{description} atomically persists one conversion");
    }

    private sealed record RecipeCase(
        uint SourceItemId,
        uint OutputItemId,
        int OutputQuantity,
        bool IsBound);
}
