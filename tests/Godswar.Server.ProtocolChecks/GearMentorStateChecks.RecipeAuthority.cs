using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorStateChecks
{
    private static void CheckPublishedRecipeAuthority()
    {
        var source = TestItemContent.Catalog;
        var recipes = TestItemContent.GearMentorRecipes
            .Select(static recipe => recipe.SourceItemId switch
            {
                4234 => recipe with
                {
                    SourceQuantity = 2,
                    TargetQuantity = 3
                },
                4214 => recipe with
                {
                    SourceQuantity = 7,
                    TargetQuantity = 2
                },
                _ => recipe
            })
            .ToArray();
        var revised = PinnedItemTemplateCatalog.Create(
            "protocol-check-recipe-authority",
            source.All,
            source.Attributes,
            source.EquipmentRanks,
            source.HolySuitEffects,
            source.Materials.ForgingMaterials,
            source.Materials.GearEnhancementMaterials,
            source.Materials.AttributeDusts,
            recipes);

        var (crystalBag, crystalRequest) = StageSingle(
            GearMentorOperation.TransformCrystal,
            Material(4234, stack: 2, bound: 1));
        var crystal = GearMentorPlanner.Create(
            revised,
            crystalBag,
            200,
            crystalRequest);
        Check.True(
            crystal.Committed &&
            crystal.Outputs.Single() == new GearMentorOutput(4233, 3, 1),
            "crystal transform follows the pinned revision quantities");

        var (shortBag, shortRequest) = StageSingle(
            GearMentorOperation.CombineGemPieces,
            Material(4214, stack: 6));
        var shortResult = GearMentorPlanner.Create(
            revised,
            shortBag,
            200,
            shortRequest);
        AssertRejected(
            shortResult,
            shortBag,
            GearMentorStatus.InsufficientGemPieces,
            "gem-piece combination enforces the pinned source quantity");
        Check.True(
            shortResult.RejectionReason?.Contains(
                "7 matching gem pieces",
                StringComparison.Ordinal) == true,
            "gem-piece rejection reports the pinned source quantity");

        var (piecesBag, piecesRequest) = StageSingle(
            GearMentorOperation.CombineGemPieces,
            Material(4214, stack: 7, bound: 1));
        var pieces = GearMentorPlanner.Create(
            revised,
            piecesBag,
            200,
            piecesRequest);
        Check.True(
            pieces.Committed &&
            pieces.Outputs.Single() == new GearMentorOutput(4213, 2, 1),
            "gem-piece combination follows the pinned revision quantities");
    }
}
