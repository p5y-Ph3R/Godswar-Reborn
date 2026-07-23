using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorStateChecks
{
    private const int SlotCount = 96;
    private const int SingleSelectionSlot = 12;

    private static readonly DustExpectation[] ExpectedDusts =
    [
        new(9900, "Rmaterial1", "Strength Dust", 9930, "504,432"),
        new(9901, "Rmaterial2", "Shield Dust", 9931, "540,432"),
        new(9902, "Rmaterial3", "Magic Dust", 9932, "576,432"),
        new(9903, "Rmaterial4", "Spell Dust", 9933, "612,432"),
        new(9904, "Rmaterial5", "Absorption Dust", 9934, "648,432"),
        new(9905, "Rmaterial6", "Health Dust", 9935, "684,432"),
        new(9906, "Rmaterial7", "Mana Dust", 9936, "720,432"),
        new(9907, "Rmaterial8", "Blood Dust", 9937, "756,432"),
        new(9908, "Rmaterial9", "Vigor Dust", 9938, "792,432"),
        new(9910, "Rmaterial10", "Accuracy Dust", 9940, "504,504"),
        new(9911, "Rmaterial11", "Psychic Dust", 9941, "540,504"),
        new(9912, "Rmaterial12", "Fury Dust", 9942, "576,504"),
        new(9913, "Rmaterial13", "Tenacity Dust", 9943, "612,504"),
        new(9914, "Rmaterial14", "Impact Dust", 9944, "648,504"),
        new(9915, "Rmaterial15", "Fervor Dust", 9945, "684,504"),
        new(9916, "Rmaterial16", "Punishment Dust", 9946, "720,504"),
        new(9917, "Rmaterial17", "Purge Dust", 9947, "756,504"),
        new(9918, "Rmaterial18", "Guard Dust", 9948, "792,504"),
        new(9919, "Rmaterial19", "Restoration Dust", 9949, "828,504"),
        new(9920, "Rmaterial20", "Dust of Destruction", 9958, "864,504"),
        new(9921, "Rmaterial21", "Dust of Penetration", 9959, "900,504")
    ];

    public static Task RunAsync()
    {
        CheckDustCatalogAndAliases();
        CheckLevelFivePieceDefinitions();
        CheckAttributeStoneRecipes();
        CheckCrystalTransforms();
        CheckGemPieceRecipes();
        CheckDecomposition();
        CheckGenericClearSnapshots();
        CheckResultSubIdMapping();
        return Task.CompletedTask;
    }

    private static void CheckDustCatalogAndAliases()
    {
        Check.Equal(21, GearMentorMaterialCatalog.AttributeDusts.Count, "native Attribute Dust count");
        Check.Equal(
            GearMentorMaterialCatalog.AttributeDusts.Count,
            GearMentorMaterialCatalog.AttributeDusts.Select(static dust => dust.ItemId).Distinct().Count(),
            "Attribute Dust item IDs are unique");
        Check.Equal(
            GearMentorMaterialCatalog.AttributeDusts.Count,
            GearMentorMaterialCatalog.AttributeDusts.Select(static dust => dust.AttributeStoneItemId).Distinct().Count(),
            "Attribute Dust recipes target unique stones");
        Check.Equal(99, GearMentorMaterialCatalog.StoneRecipeDustQuantity, "native Dust-to-Stone recipe quantity");

        for (var index = 0; index < ExpectedDusts.Length; index++)
        {
            var expected = ExpectedDusts[index];
            var ordered = GearMentorMaterialCatalog.AttributeDusts[index];
            Check.Equal(expected.ItemId, ordered.ItemId, $"Dust row {index} item ID");
            Check.Equal(expected.NameKey, ordered.NameKey, $"Dust {expected.ItemId} name key");
            Check.Equal(expected.DisplayName, ordered.DisplayName, $"Dust {expected.ItemId} display name");
            Check.Equal(expected.StoneItemId, ordered.AttributeStoneItemId, $"Dust {expected.ItemId} stone recipe");
            Check.Equal(expected.Icon, ordered.Icon, $"Dust {expected.ItemId} icon cell");
            Check.Equal("./Localization/en_us/UI/Texture/Icon2.gwo", ordered.Texture, $"Dust {expected.ItemId} texture");
            Check.Equal((short)99, ordered.StackCap, $"Dust {expected.ItemId} stack cap");

            Check.True(
                GearMentorMaterialCatalog.TryGetDust(expected.ItemId, out var byItemId) &&
                byItemId == ordered,
                $"Dust {expected.ItemId} resolves by item ID");
            Check.True(
                GearMentorMaterialCatalog.TryGetDustForStone(expected.StoneItemId, out var byStone) &&
                byStone.ItemId == expected.ItemId,
                $"Dust {expected.ItemId} resolves from stone {expected.StoneItemId}");
            Check.True(
                GearEnhancementMaterialCatalog.TryGetAttributeStone(expected.StoneItemId, out var stone),
                $"Dust {expected.ItemId} references a real Attribute Stone");
            foreach (var attributeId in stone.AllowedAttributeIds)
            {
                Check.True(
                    GearMentorMaterialCatalog.TryGetDustForAttribute(attributeId, out var byAttribute) &&
                    byAttribute.ItemId == expected.ItemId,
                    $"attribute {attributeId} resolves to Dust {expected.ItemId}");
            }

            foreach (var alias in GearMentorMaterialCatalog.GetAliases(ordered)
                         .Append(expected.DisplayName.Replace(" ", string.Empty, StringComparison.Ordinal))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Check.True(
                    DeveloperGrantMaterialCatalog.TryResolve(alias, out var byAlias) &&
                    byAlias.ItemId == expected.ItemId,
                    $"Dust alias '{alias}' resolves to {expected.ItemId}");
            }

            Check.True(
                DeveloperGrantMaterialCatalog.TryResolve(expected.ItemId, out var grant) &&
                grant.StackCap == 99 &&
                grant.GrantedBound == 0,
                $"Dust {expected.ItemId} is grant-allowlisted with native policy");
            Check.True(
                DeveloperItemCommand.TryParse(
                    $"/item add {expected.NameKey} 99",
                    out var command,
                    out _) &&
                command is
                {
                    Operation: DeveloperItemOperation.Add,
                    Material.ItemId: var commandItemId,
                    Quantity: 99
                } &&
                commandItemId == expected.ItemId,
                $"Dust command alias {expected.NameKey} grants {expected.ItemId}");

            var template = ordered.ToItemTemplateSeed();
            Check.Equal(checked((int)expected.ItemId), template.Id, $"Dust {expected.ItemId} template ID");
            Check.Equal("consume item", template.Kind, $"Dust {expected.ItemId} template kind");
            using var stats = JsonDocument.Parse(template.StatsJson);
            Check.Equal("99", stats.RootElement.GetProperty("Overlap").GetString() ?? string.Empty, $"Dust {expected.ItemId} template stack cap");
            Check.Equal("50,150", stats.RootElement.GetProperty("Distribution").GetString() ?? string.Empty, $"Dust {expected.ItemId} native distribution");
        }

        Check.True(!GearMentorMaterialCatalog.TryGetDust(9909, out _), "the native Dust ID 9909 gap stays empty");
        Check.True(!GearMentorMaterialCatalog.TryGetDustForStone(9950, out _), "special Primal Stone has no invented Dust recipe");
    }

    private static void CheckLevelFivePieceDefinitions()
    {
        var expectedPieces = new[]
        {
            new PieceExpectation(4216, "MaterialBase7", "Level 5 Sapphire Pieces", "sapphire", "144,0"),
            new PieceExpectation(4226, "MaterialAppend7", "Level 5 Emerald Pieces", "emerald", "180,0"),
            new PieceExpectation(4235, "MaterialOdds6", "Level 5 Crystal Pieces", "crystal", "108,0")
        };

        Check.Equal(23, ForgingMaterialCatalog.All.Count, "forging material catalog includes three Level 5 pieces");
        Check.Equal(5, ForgingMaterialCatalog.All.Count(static material => material.IsPiece), "two Level 4 and three Level 5 piece definitions");
        foreach (var expected in expectedPieces)
        {
            Check.True(
                ForgingMaterialCatalog.TryResolve(expected.ItemId, out var piece),
                $"Level 5 piece {expected.ItemId} resolves");
            Check.Equal(expected.NameKey, piece.NameKey, $"Level 5 piece {expected.ItemId} name key");
            Check.Equal(expected.DisplayName, piece.DisplayName, $"Level 5 piece {expected.ItemId} display name");
            Check.Equal(expected.Material, piece.Material, $"Level 5 piece {expected.ItemId} material family");
            Check.Equal(5, piece.Level ?? 0, $"Level 5 piece {expected.ItemId} level");
            Check.True(piece.IsPiece, $"Level 5 piece {expected.ItemId} is not a complete gem");
            Check.Equal((short)99, piece.StackCap, $"Level 5 piece {expected.ItemId} stack cap");
            Check.Equal((short)1, piece.GrantedBound, $"Level 5 piece {expected.ItemId} grant binding");
            Check.Equal("./Localization/en_us/UI/Texture/Icon4.gwo", piece.Texture, $"Level 5 piece {expected.ItemId} texture");
            Check.Equal(expected.Icon, piece.Icon, $"Level 5 piece {expected.ItemId} icon cell");
            Check.True(
                ForgingMaterialCatalog.TryResolve(piece.CanonicalAlias, out var byCanonicalAlias) &&
                byCanonicalAlias.ItemId == expected.ItemId,
                $"Level 5 piece alias {piece.CanonicalAlias} resolves");
            Check.True(
                DeveloperGrantMaterialCatalog.TryResolve(expected.NameKey, out var byNameKey) &&
                byNameKey.ItemId == expected.ItemId,
                $"Level 5 piece key {expected.NameKey} is grant-allowlisted");

            var template = piece.ToItemTemplateSeed();
            using var stats = JsonDocument.Parse(template.StatsJson);
            Check.Equal("1", stats.RootElement.GetProperty("BindType").GetString() ?? string.Empty, $"Level 5 piece {expected.ItemId} BindType");
            Check.Equal(expected.Icon, stats.RootElement.GetProperty("Icon").GetString() ?? string.Empty, $"Level 5 piece {expected.ItemId} template icon");
        }

        Check.True(
            !ForgingMaterialCatalog.TryResolve("crystal4pieces", out _),
            "no unsupported Level 4 Crystal piece recipe is invented");
    }

    private static void CheckAttributeStoneRecipes()
    {
        for (var index = 0; index < ExpectedDusts.Length; index++)
        {
            var expected = ExpectedDusts[index];
            var bound = checked((short)(index % 2));
            var (kitBag, request) = StageSingle(
                GearMentorOperation.MakeAttributeStone,
                Material(expected.ItemId, stack: 99, bound));
            var result = GearMentorPlanner.Create(kitBag, playerLevel: 200, request);

            Check.True(result.Committed, $"99 {expected.DisplayName} make one stone ({result.RejectionReason})");
            Check.Equal(1, result.Outputs.Count, $"Dust {expected.ItemId} emits one output record");
            var output = result.Outputs[0];
            Check.Equal(expected.StoneItemId, output.ItemId, $"Dust {expected.ItemId} output stone");
            Check.Equal(1, output.Quantity, $"Dust {expected.ItemId} output quantity");
            Check.Equal(bound, output.Bound, $"Dust {expected.ItemId} output preserves binding");
            Check.Equal(0u, KitBagSlots.GetItem(result.UpdatedKitBag, SingleSelectionSlot).Id, $"Dust {expected.ItemId} source stack is consumed");
            Check.Equal(1, QuantityInBag(result.UpdatedKitBag, expected.StoneItemId, bound), $"Dust {expected.ItemId} resulting stone quantity and binding");
        }

        var (insufficientBag, insufficientRequest) = StageSingle(
            GearMentorOperation.MakeAttributeStone,
            Material(9900, stack: 98));
        AssertRejected(
            GearMentorPlanner.Create(insufficientBag, 200, insufficientRequest),
            insufficientBag,
            GearMentorStatus.InsufficientDust,
            "98 Dust cannot make a stone");

        var (invalidBag, invalidRequest) = StageSingle(
            GearMentorOperation.MakeAttributeStone,
            Material(4230, stack: 99));
        AssertRejected(
            GearMentorPlanner.Create(invalidBag, 200, invalidRequest),
            invalidBag,
            GearMentorStatus.InvalidDust,
            "a gem cannot enter the Dust recipe");

        var (stagedBag, stagedRequest) = StageSingle(
            GearMentorOperation.MakeAttributeStone,
            Material(9900, stack: 99, bound: 1));
        var changedBag = KitBagSlots.SetSlot(
            stagedBag,
            SingleSelectionSlot,
            Material(9900, stack: 98, bound: 1).ToCompactString());
        AssertRejected(
            GearMentorPlanner.Create(changedBag, 200, stagedRequest),
            changedBag,
            GearMentorStatus.StaleSelection,
            "changed Dust stack invalidates the staged request");

        // Legacy imports can contain an over-cap source stack. Consuming 99
        // leaves the selected slot occupied, so a completely full bag must be
        // rejected atomically rather than losing the resulting stone.
        var capacityBag = FillBag(Material(4234, stack: 99, bound: 1));
        capacityBag = KitBagSlots.SetSlot(
            capacityBag,
            SingleSelectionSlot,
            Material(9900, stack: 100).ToCompactString());
        var capacityRequest = new GearMentorRequest(
            GearMentorOperation.MakeAttributeStone,
            [GearMentorSlotSelection.Capture(capacityBag, SingleSelectionSlot)]);
        AssertRejected(
            GearMentorPlanner.Create(capacityBag, 200, capacityRequest),
            capacityBag,
            GearMentorStatus.InsufficientCapacity,
            "Dust conversion rejects a full bag before committing");
    }

    private static void CheckCrystalTransforms()
    {
        var recipes = new[]
        {
            new TransformExpectation(4234, 4233, 2),
            new TransformExpectation(4233, 4232, 2),
            new TransformExpectation(4232, 4231, 4),
            new TransformExpectation(4231, 4230, 8)
        };

        for (var index = 0; index < recipes.Length; index++)
        {
            var recipe = recipes[index];
            var bound = checked((short)(index % 2));
            var (kitBag, request) = StageSingle(
                GearMentorOperation.TransformCrystal,
                Material(recipe.SourceItemId, stack: 1, bound));
            var result = GearMentorPlanner.Create(kitBag, 200, request);

            Check.True(result.Committed, $"Crystal {recipe.SourceItemId} transform commits ({result.RejectionReason})");
            var output = result.Outputs.Single();
            Check.Equal(recipe.ResultItemId, output.ItemId, $"Crystal {recipe.SourceItemId} transform target");
            Check.Equal(recipe.Quantity, output.Quantity, $"Crystal {recipe.SourceItemId} transform quantity");
            Check.Equal(bound, output.Bound, $"Crystal {recipe.SourceItemId} transform binding");
            Check.Equal(0u, KitBagSlots.GetItem(result.UpdatedKitBag, SingleSelectionSlot).Id, $"Crystal {recipe.SourceItemId} source is consumed");
            Check.Equal(recipe.Quantity, QuantityInBag(result.UpdatedKitBag, recipe.ResultItemId, bound), $"Crystal {recipe.SourceItemId} output quantity and binding");
        }

        var (multiBag, multiRequest) = StageSingle(
            GearMentorOperation.TransformCrystal,
            Material(4232, stack: 2, bound: 1));
        var multiResult = GearMentorPlanner.Create(multiBag, 200, multiRequest);
        Check.True(multiResult.Committed, "Crystal transform consumes one source unit per action");
        Check.Equal((short)1, KitBagSlots.GetItem(multiResult.UpdatedKitBag, SingleSelectionSlot).Stack, "one Level 3 Crystal remains");
        Check.Equal(4, QuantityInBag(multiResult.UpdatedKitBag, 4231, bound: 1), "one action adds four Level 2 Crystals");

        foreach (var invalidItemId in new uint[] { 4200, 4213, 4230, 4235 })
        {
            var (kitBag, request) = StageSingle(
                GearMentorOperation.TransformCrystal,
                Material(invalidItemId, stack: 1));
            AssertRejected(
                GearMentorPlanner.Create(kitBag, 200, request),
                kitBag,
                GearMentorStatus.InvalidCrystal,
                $"item {invalidItemId} is not a supported Crystal transform source");
        }

        var fullBag = FillBag(Material(4234, stack: 99, bound: 1));
        fullBag = KitBagSlots.SetSlot(
            fullBag,
            SingleSelectionSlot,
            Material(4232, stack: 2, bound: 1).ToCompactString());
        var fullRequest = new GearMentorRequest(
            GearMentorOperation.TransformCrystal,
            [GearMentorSlotSelection.Capture(fullBag, SingleSelectionSlot)]);
        AssertRejected(
            GearMentorPlanner.Create(fullBag, 200, fullRequest),
            fullBag,
            GearMentorStatus.InsufficientCapacity,
            "Crystal transform is atomic when its output cannot fit");
    }

    private static void CheckGemPieceRecipes()
    {
        var recipes = new[]
        {
            new PieceRecipeExpectation(4214, 4213),
            new PieceRecipeExpectation(4224, 4223),
            new PieceRecipeExpectation(4216, 4215),
            new PieceRecipeExpectation(4226, 4225),
            new PieceRecipeExpectation(4235, 4234)
        };

        foreach (var recipe in recipes)
        {
            var (kitBag, request) = StageSingle(
                GearMentorOperation.CombineGemPieces,
                Material(recipe.PieceItemId, stack: 99, bound: 1));
            var result = GearMentorPlanner.Create(kitBag, 200, request);

            Check.True(result.Committed, $"99 pieces {recipe.PieceItemId} combine ({result.RejectionReason})");
            var output = result.Outputs.Single();
            Check.Equal(recipe.GemItemId, output.ItemId, $"piece {recipe.PieceItemId} result gem");
            Check.Equal(1, output.Quantity, $"piece {recipe.PieceItemId} result quantity");
            Check.Equal((short)1, output.Bound, $"piece {recipe.PieceItemId} preserves binding");
            Check.Equal(0u, KitBagSlots.GetItem(result.UpdatedKitBag, SingleSelectionSlot).Id, $"piece {recipe.PieceItemId} source stack is consumed");
            Check.Equal(1, QuantityInBag(result.UpdatedKitBag, recipe.GemItemId, bound: 1), $"piece {recipe.PieceItemId} resulting quantity and binding");
        }

        var (insufficientBag, insufficientRequest) = StageSingle(
            GearMentorOperation.CombineGemPieces,
            Material(4216, stack: 98, bound: 1));
        AssertRejected(
            GearMentorPlanner.Create(insufficientBag, 200, insufficientRequest),
            insufficientBag,
            GearMentorStatus.InsufficientGemPieces,
            "98 Level 5 pieces cannot combine");

        foreach (var invalidItemId in new uint[] { 4213, 4215, 4225, 4233, 4234 })
        {
            var (kitBag, request) = StageSingle(
                GearMentorOperation.CombineGemPieces,
                Material(invalidItemId, stack: 99, bound: 1));
            AssertRejected(
                GearMentorPlanner.Create(kitBag, 200, request),
                kitBag,
                GearMentorStatus.InvalidGemPieces,
                $"complete gem {invalidItemId} cannot enter a piece recipe");
        }
    }
}
