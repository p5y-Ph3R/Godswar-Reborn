using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class GearMentorStateChecks
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

        foreach (var invalidItemId in new uint[] { 4200, 4213, 4230, 4233, 4234, 4235 })
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

    private static void CheckDecomposition()
    {
        Check.Equal(248, ClassSuitItemCatalog.ShippedItemCount, "generated Class Suit count");
        Check.Equal(ClassSuitItemCatalog.ShippedItemCount, ClassSuitItemCatalog.AllItemIds.Count, "Class Suit catalog list count");
        Check.Equal(
            "3A8202C4021C0486DEB30F0D7A9BECA72F632E01227DDF2F1B0F6BFC9434B47D",
            ClassSuitItemCatalog.CanonicalItemIdSha256,
            "Class Suit canonical item-ID hash");
        Check.True(
            ClassSuitItemCatalog.AllItemIds.SequenceEqual(ClassSuitItemCatalog.AllItemIds.OrderBy(static itemId => itemId)),
            "Class Suit IDs remain deterministically sorted");
        Check.True(
            ClassSuitItemCatalog.AllItemIds.All(ClassSuitItemCatalog.IsClassSuit),
            "every generated Class Suit ID resolves");
        Check.True(
            !ClassSuitItemCatalog.IsClassSuit(1004) && !ClassSuitItemCatalog.IsClassSuit(1030),
            "ordinary and elite gear are not misclassified as Class Suits");

        var (eligibleBag, eligibleRequest) = StageDecomposition(
            (4, Gear(1004, quality: 2, grade: 1, attribute1: 0)));
        AssertRejected(
            GearMentorPlanner.Create(eligibleBag, 29, eligibleRequest, static _ => 0),
            eligibleBag,
            GearMentorStatus.PlayerLevelTooLow,
            "characters below Level 30 cannot decompose");

        var (invalidBag, invalidRequest) = StageDecomposition(
            (4, Material(9900, stack: 1)));
        AssertRejected(
            GearMentorPlanner.Create(invalidBag, 30, invalidRequest, static _ => 0),
            invalidBag,
            GearMentorStatus.InvalidEquipment,
            "materials cannot be decomposed as gear");

        var (stackedBag, stackedRequest) = StageDecomposition(
            (4, Gear(1004, quality: 2, grade: 1, stack: 2, attribute1: 0)));
        AssertRejected(
            GearMentorPlanner.Create(stackedBag, 30, stackedRequest, static _ => 0),
            stackedBag,
            GearMentorStatus.InvalidEquipment,
            "stacked equipment records cannot be decomposed");

        var (lowGearBag, lowGearRequest) = StageDecomposition(
            (4, Gear(1003, quality: 2, grade: 1, attribute1: 0)));
        AssertRejected(
            GearMentorPlanner.Create(lowGearBag, 30, lowGearRequest, static _ => 0),
            lowGearBag,
            GearMentorStatus.EquipmentLevelTooLow,
            "Level 40 gear cannot be decomposed");

        var (plainBag, plainRequest) = StageDecomposition(
            (4, Gear(1004, quality: 1, grade: 1, attribute1: 0)));
        AssertRejected(
            GearMentorPlanner.Create(plainBag, 30, plainRequest, static _ => 0),
            plainBag,
            GearMentorStatus.InsufficientEquipmentQuality,
            "common Grade 1 gear cannot be decomposed");

        var (classSuitBag, classSuitRequest) = StageDecomposition(
            (4, Gear(1032, quality: 2, grade: 1, attribute1: 0)));
        AssertRejected(
            GearMentorPlanner.Create(classSuitBag, 30, classSuitRequest, static _ => 0),
            classSuitBag,
            GearMentorStatus.ClassSuit,
            "Class Suit I cannot be decomposed");

        foreach (var qualifyingGear in new[]
                 {
                     Gear(1004, quality: 2, grade: 1, attribute1: 0),
                     Gear(1004, quality: 1, grade: 2, attribute1: 0)
                 })
        {
            var (kitBag, request) = StageDecomposition((4, qualifyingGear));
            var result = GearMentorPlanner.Create(kitBag, 30, request, static _ => 0);
            Check.True(result.Committed, "Enhanced quality or Grade 2 independently qualifies");
        }

        var expectedGears = new[]
        {
            Gear(1004, quality: 2, grade: 1, bound: 0, attribute1: 0),
            Gear(1005, quality: 2, grade: 1, bound: 1, attribute1: 20),
            Gear(1006, quality: 2, grade: 1, bound: 0, attribute1: 40)
        };
        var expectedDustIds = new uint[] { 9900, 9902, 9910 };
        for (var count = 1; count <= 3; count++)
        {
            var staged = Enumerable.Range(0, count)
                .Select(index => (Slot: 10 + index, Item: expectedGears[index]))
                .ToArray();
            var (kitBag, request) = StageDecomposition(staged);
            var result = GearMentorPlanner.Create(
                kitBag,
                30,
                request,
                candidateCount => candidateCount - 1);

            Check.True(result.Committed, $"decomposition accepts exactly {count} selected gear item(s)");
            Check.Equal(count, result.Outputs.Count, $"{count}-gear decomposition output record count");
            for (var index = 0; index < count; index++)
            {
                Check.Equal(expectedDustIds[index], result.Outputs[index].ItemId, $"gear {index} uses its matched Dust family");
                Check.Equal(expectedGears[index].Bound, result.Outputs[index].Bound, $"gear {index} Dust preserves binding");
                Check.True(
                    !Enumerable.Range(0, SlotCount)
                        .Select(slot => KitBagSlots.GetItem(result.UpdatedKitBag, slot))
                        .Any(item => item.Id == expectedGears[index].Id),
                    $"decomposed gear {expectedGears[index].Id} is consumed");
            }
        }

        var fourSelections = new[]
        {
            (Slot: 0, Item: Gear(1004, attribute1: 0)),
            (Slot: 1, Item: Gear(1005, attribute1: 0)),
            (Slot: 2, Item: Gear(1006, attribute1: 0)),
            (Slot: 3, Item: Gear(1007, attribute1: 0))
        };
        var (fourBag, fourRequest) = StageDecomposition(fourSelections);
        AssertRejected(
            GearMentorPlanner.Create(fourBag, 30, fourRequest, static _ => 0),
            fourBag,
            GearMentorStatus.SelectionMissing,
            "decomposition rejects more than three selections");

        var noSelection = new GearMentorRequest(GearMentorOperation.Decompose, []);
        AssertRejected(
            GearMentorPlanner.Create(GameDefaults.EmptyKitBag, 30, noSelection, static _ => 0),
            GameDefaults.EmptyKitBag,
            GearMentorStatus.SelectionMissing,
            "decomposition rejects an empty selection");

        var duplicateBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            4,
            Gear(1004, attribute1: 0).ToCompactString());
        var duplicateSelection = GearMentorSlotSelection.Capture(duplicateBag, 4);
        var duplicateRequest = new GearMentorRequest(
            GearMentorOperation.Decompose,
            [duplicateSelection, duplicateSelection]);
        AssertRejected(
            GearMentorPlanner.Create(duplicateBag, 30, duplicateRequest, static _ => 0),
            duplicateBag,
            GearMentorStatus.DuplicateKitBagSlot,
            "decomposition rejects duplicate bag slots");

        var (matchedBag, matchedRequest) = StageDecomposition(
            (4, Gear(1004, quality: 2, grade: 2, attribute1: 0, attribute2: 50)));
        var matchedResult = GearMentorPlanner.Create(
            matchedBag,
            30,
            matchedRequest,
            candidateCount =>
            {
                Check.Equal(2, candidateCount, "decomposition limits random candidates to appended attributes");
                return 1;
            });
        Check.Equal(9911u, matchedResult.Outputs.Single().ItemId, "second matched attribute yields Psychic Dust");

        var (fallbackBag, fallbackRequest) = StageDecomposition(
            (4, Gear(1004, quality: 2, grade: 1)));
        var fallbackResult = GearMentorPlanner.Create(
            fallbackBag,
            30,
            fallbackRequest,
            candidateCount =>
            {
                Check.Equal(21, candidateCount, "attribute-free gear uses the complete native Dust table");
                return candidateCount - 1;
            });
        Check.Equal(9921u, fallbackResult.Outputs.Single().ItemId, "attribute-free fallback can select Penetration Dust");

        var progression = new (short Quality, short Grade)[]
        {
            (2, 1),
            (2, 2),
            (3, 2),
            (3, 5),
            (13, 25),
            (99, 99)
        };
        var quantities = new List<int>();
        foreach (var (quality, grade) in progression)
        {
            var (kitBag, request) = StageDecomposition(
                (4, Gear(1004, quality, grade, attribute1: 0)));
            var result = GearMentorPlanner.Create(kitBag, 30, request, static _ => 0);
            Check.True(result.Committed, $"quality {quality}/grade {grade} decomposition commits");
            quantities.Add(result.Outputs.Single().Quantity);
        }
        Check.True(
            quantities.Zip(quantities.Skip(1), static (lower, higher) => lower <= higher).All(static monotonic => monotonic),
            "decomposition Dust quantity is monotonic across quality and grade");
        Check.Equal(99, quantities[^1], "decomposition Dust output remains capped to one native stack");
    }

    private static void CheckGenericClearSnapshots()
    {
        var correlationNow = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var oneSlotBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            7,
            Material(9900, stack: 99).ToCompactString());
        var oneSlotContext = Context(() => correlationNow, correlationNow);
        Check.True(
            oneSlotContext.Apply(Selection(7, selected: true), oneSlotBag).Status ==
                GearEnhancerSelectionStageStatus.Staged,
            "one-slot native selection stages");
        Check.True(
            oneSlotContext.Apply(Selection(7, selected: false), oneSlotBag).Status ==
                GearEnhancerSelectionStageStatus.Removed,
            "one-slot native control emits its clear event");
        Check.True(
            oneSlotContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MalformedCommit,
                minimumCount: 1,
                maximumCount: 1,
                out var oneSlotSnapshot) &&
            oneSlotSnapshot.Select(static selection => selection.KitBagSlot).SequenceEqual(new[] { 7 }),
            "one-slot clear preserves the authoritative final-action snapshot");
        Check.True(
            oneSlotSnapshot.Single().ExpectedItem == KitBagSlots.GetItem(oneSlotBag, 7),
            "one-slot native staging preserves the exact selected Dust stack");
        var replacedDustBag = KitBagSlots.SetSlot(
            oneSlotBag,
            7,
            Material(9900, stack: 98).ToCompactString());
        var staleDustRequest = new GearMentorRequest(
            GearMentorOperation.MakeAttributeStone,
            [new GearMentorSlotSelection(
                oneSlotSnapshot.Single().KitBagSlot,
                oneSlotSnapshot.Single().ExpectedItem)]);
        AssertRejected(
            GearMentorPlanner.Create(replacedDustBag, 200, staleDustRequest),
            replacedDustBag,
            GearMentorStatus.StaleSelection,
            "a replacement Dust stack in a staged native slot is rejected");
        correlationNow += GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
        Check.True(
            !oneSlotContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MalformedCommit,
                minimumCount: 1,
                maximumCount: 1,
                out _),
            "an ordinary one-slot deselection cannot be revived after the short correlation window");

        var threeSlots = new[] { 2, 29, 55 };
        var threeSlotBag = GameDefaults.EmptyKitBag;
        foreach (var slot in threeSlots)
        {
            threeSlotBag = KitBagSlots.SetSlot(
                threeSlotBag,
                slot,
                Gear(1004 + checked((uint)Array.IndexOf(threeSlots, slot)), attribute1: 0).ToCompactString());
        }
        var threeSlotContext = Context();
        foreach (var slot in threeSlots)
        {
            Check.True(
                threeSlotContext.Apply(Selection(slot, selected: true), threeSlotBag).Status ==
                    GearEnhancerSelectionStageStatus.Staged,
                $"three-slot native selection stages bag slot {slot}");
        }
        foreach (var slot in threeSlots)
        {
            Check.True(
                threeSlotContext.Apply(Selection(slot, selected: false), threeSlotBag).Status ==
                    GearEnhancerSelectionStageStatus.Removed,
                $"three-slot native control clears bag slot {slot}");
        }
        Check.True(
            threeSlotContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out var threeSlotSnapshot) &&
            threeSlotSnapshot.Select(static selection => selection.KitBagSlot).SequenceEqual(threeSlots),
            "three-slot clear burst preserves ordered decomposition selections");
        Check.True(
            threeSlotSnapshot.All(selection =>
                selection.ExpectedItem == KitBagSlots.GetItem(threeSlotBag, selection.KitBagSlot)),
            "decomposition clear snapshots preserve every exact selected gear item");

        var partialContext = Context();
        foreach (var slot in threeSlots)
        {
            partialContext.Apply(Selection(slot, selected: true), threeSlotBag);
        }
        partialContext.Apply(Selection(threeSlots[0], selected: false), threeSlotBag);
        Check.True(
            !partialContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "an incomplete clear burst cannot commit only its residual decomposition selections");

        var wrongOrderContext = Context();
        foreach (var slot in threeSlots)
        {
            wrongOrderContext.Apply(Selection(slot, selected: true), threeSlotBag);
        }
        foreach (var slot in new[] { threeSlots[1], threeSlots[0], threeSlots[2] })
        {
            wrongOrderContext.Apply(Selection(slot, selected: false), threeSlotBag);
        }
        Check.True(
            !wrongOrderContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "out-of-order native clears cannot rebuild or replay a shorter decomposition snapshot");

        var expiredPartialNow = correlationNow;
        var expiredPartialContext = Context(() => expiredPartialNow, expiredPartialNow);
        foreach (var slot in threeSlots)
        {
            expiredPartialContext.Apply(Selection(slot, selected: true), threeSlotBag);
        }
        expiredPartialContext.Apply(Selection(threeSlots[0], selected: false), threeSlotBag);
        expiredPartialNow += GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
        Check.True(
            !expiredPartialContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "an expired partial clear cannot fall back to residual decomposition selections");

        var emptySelection = Selection(80, selected: true);
        Check.True(
            expiredPartialContext.Apply(emptySelection, threeSlotBag).Status ==
                GearEnhancerSelectionStageStatus.SlotEmpty &&
            !expiredPartialContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "a selected=true packet for an empty slot cannot reset an invalidated clear");
        Check.True(
            expiredPartialContext.Apply(
                Selection(threeSlots[1], selected: true),
                threeSlotBag).Status == GearEnhancerSelectionStageStatus.AlreadyStaged &&
            !expiredPartialContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "a duplicate selected=true packet cannot reset an invalidated clear");
        expiredPartialContext.Apply(Selection(threeSlots[1], selected: false), threeSlotBag);
        expiredPartialContext.Apply(Selection(threeSlots[2], selected: false), threeSlotBag);
        Check.True(
            !expiredPartialContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "an expired partial clear cannot rebuild a shorter decomposition snapshot from its suffix");
    }

    private static void CheckResultSubIdMapping()
    {
        Check.Equal(
            GearEnhancerProtocol.SelectedItemMissingResultSubId,
            GearEnhancerProtocol.ResolveGearMentorResultSubId(null),
            "missing Gear Mentor result uses selected-item-missing");

        var mappings = new[]
        {
            Map(GearMentorOperation.Decompose, GearMentorStatus.Succeeded, 1005),
            Map(GearMentorOperation.Decompose, GearMentorStatus.SelectionMissing, 1024),
            Map(GearMentorOperation.Decompose, GearMentorStatus.RequestMissing, 1024),
            Map(GearMentorOperation.Decompose, GearMentorStatus.PlayerLevelTooLow, 1015),
            Map(GearMentorOperation.Decompose, GearMentorStatus.InvalidEquipment, 1003),
            Map(GearMentorOperation.Decompose, GearMentorStatus.EquipmentLevelTooLow, 1014),
            Map(GearMentorOperation.Decompose, GearMentorStatus.InsufficientEquipmentQuality, 1004),
            Map(GearMentorOperation.Decompose, GearMentorStatus.ClassSuit, 1032),
            Map(GearMentorOperation.Decompose, GearMentorStatus.InsufficientCapacity, 1020),
            Map(GearMentorOperation.Decompose, GearMentorStatus.StaleSelection, 1002),
            Map(GearMentorOperation.Decompose, GearMentorStatus.InvalidKitBagSlot, 1002),
            Map(GearMentorOperation.Decompose, GearMentorStatus.DuplicateKitBagSlot, 1019),

            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.Succeeded, 1017),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.SelectionMissing, 1025),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.RequestMissing, 1025),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.InvalidDust, 1022),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.InsufficientDust, 1016),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.InsufficientCapacity, 1020),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.StaleSelection, 1002),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.InvalidKitBagSlot, 1002),

            Map(GearMentorOperation.TransformCrystal, GearMentorStatus.Succeeded, 1823),
            Map(GearMentorOperation.TransformCrystal, GearMentorStatus.InsufficientCapacity, 1020),
            Map(GearMentorOperation.TransformCrystal, GearMentorStatus.InvalidCrystal, 1822),
            Map(GearMentorOperation.TransformCrystal, GearMentorStatus.StaleSelection, 1822),

            Map(GearMentorOperation.CombineGemPieces, GearMentorStatus.Succeeded, 304),
            Map(GearMentorOperation.CombineGemPieces, GearMentorStatus.InsufficientGemPieces, 302),
            Map(GearMentorOperation.CombineGemPieces, GearMentorStatus.InsufficientCapacity, 303),
            Map(GearMentorOperation.CombineGemPieces, GearMentorStatus.InvalidGemPieces, 301),
            Map(GearMentorOperation.CombineGemPieces, GearMentorStatus.StaleSelection, 301)
        };

        foreach (var mapping in mappings)
        {
            var result = Result(mapping.Operation, mapping.Status);
            Check.Equal(
                mapping.ExpectedSubId,
                GearEnhancerProtocol.ResolveGearMentorResultSubId(result),
                $"{mapping.Operation}/{mapping.Status} result sub-ID");
        }

        Check.True(
            new[] { 1, 4, 8, 201 }.All(GearEnhancerProtocol.IsGearMentorTransactionSubId),
            "all implemented Gear Mentor action sub-IDs are recognized");
        Check.True(
            new[] { 2, 3, 5, 6, 7, 9 }.All(static subId => !GearEnhancerProtocol.IsGearMentorTransactionSubId(subId)),
            "enhancement, disabled, and combine-navigation sub-IDs are not generic transactions");
        Check.True(
            new[] { 5, 7 }.All(GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId),
            "Instructions and Wash Dust remain the only unavailable menu operations");
        Check.True(
            new[] { 1, 2, 3, 4, 6, 8, 9 }.All(static subId => !GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId(subId)),
            "implemented and navigation menu operations do not return 999");
    }

    private static (string KitBag, GearMentorRequest Request) StageSingle(
        GearMentorOperation operation,
        CompactItemEntry item)
    {
        var kitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            SingleSelectionSlot,
            item.ToCompactString());
        return (
            kitBag,
            new GearMentorRequest(
                operation,
                [GearMentorSlotSelection.Capture(kitBag, SingleSelectionSlot)]));
    }

    private static (string KitBag, GearMentorRequest Request) StageDecomposition(
        params (int Slot, CompactItemEntry Item)[] items)
    {
        var kitBag = GameDefaults.EmptyKitBag;
        foreach (var (slot, item) in items)
        {
            kitBag = KitBagSlots.SetSlot(kitBag, slot, item.ToCompactString());
        }

        return (
            kitBag,
            new GearMentorRequest(
                GearMentorOperation.Decompose,
                items.Select(item => GearMentorSlotSelection.Capture(kitBag, item.Slot)).ToArray()));
    }

    private static CompactItemEntry Material(
        uint itemId,
        short stack = 1,
        short bound = 0)
    {
        return CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 1,
            Grade = 1,
            Bound = bound,
            Stack = stack
        };
    }

    private static CompactItemEntry Gear(
        uint itemId,
        short quality = 2,
        short grade = 1,
        short bound = 0,
        short stack = 1,
        int? attribute1 = null,
        int? attribute2 = null)
    {
        return CompactItemEntry.Empty with
        {
            Id = itemId,
            Attribute1 = attribute1,
            Attribute2 = attribute2,
            AttributeLevel1 = attribute1.HasValue ? (short)1 : null,
            AttributeLevel2 = attribute2.HasValue ? (short)1 : null,
            Quality = quality,
            Grade = grade,
            Bound = bound,
            Stack = stack
        };
    }

    private static string FillBag(CompactItemEntry filler)
    {
        var kitBag = GameDefaults.EmptyKitBag;
        for (var slot = 0; slot < SlotCount; slot++)
        {
            kitBag = KitBagSlots.SetSlot(kitBag, slot, filler.ToCompactString());
        }

        return kitBag;
    }

    private static int QuantityInBag(string kitBag, uint itemId, short bound)
    {
        return Enumerable.Range(0, SlotCount)
            .Select(slot => KitBagSlots.GetItem(kitBag, slot))
            .Where(item => item.Id == itemId && item.Bound == bound)
            .Sum(static item => item.Stack);
    }

    private static GearEnhancerSelectionContext Context(
        Func<DateTimeOffset>? utcNow = null,
        DateTimeOffset? now = null)
    {
        var createdAt = now ?? DateTimeOffset.UtcNow;
        return new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            GearEnhancerProtocol.SpartaEnhancerNpcId,
            GearEnhancerProtocol.DialogIndex,
            operation: null,
            createdAt.AddMinutes(1),
            utcNow);
    }

    private static GearEnhancerItemSelectionPacket Selection(int kitBagSlot, bool selected)
    {
        return new GearEnhancerItemSelectionPacket(
            kitBagSlot / GearEnhancerItemSelectionPacket.SlotsPerPage,
            kitBagSlot % GearEnhancerItemSelectionPacket.SlotsPerPage,
            selected);
    }

    private static void AssertRejected(
        GearMentorResult result,
        string expectedKitBag,
        GearMentorStatus expectedStatus,
        string description)
    {
        Check.True(!result.Committed, description);
        Check.True(result.Status == expectedStatus, $"{description}: status {expectedStatus}");
        Check.Equal(expectedKitBag, result.UpdatedKitBag, $"{description}: bag unchanged");
        Check.Equal(0, result.Mutations.Count, $"{description}: no mutations");
        Check.Equal(0, result.Outputs.Count, $"{description}: no outputs");
    }

    private static ResultSubIdExpectation Map(
        GearMentorOperation operation,
        GearMentorStatus status,
        int expectedSubId)
    {
        return new ResultSubIdExpectation(operation, status, expectedSubId);
    }

    private static GearMentorResult Result(
        GearMentorOperation operation,
        GearMentorStatus status)
    {
        return new GearMentorResult(
            status,
            operation,
            GameDefaults.EmptyKitBag,
            GameDefaults.EmptyKitBag,
            [],
            []);
    }

    private sealed record DustExpectation(
        uint ItemId,
        string NameKey,
        string DisplayName,
        uint StoneItemId,
        string Icon);

    private sealed record PieceExpectation(
        uint ItemId,
        string NameKey,
        string DisplayName,
        string Material,
        string Icon);

    private sealed record TransformExpectation(
        uint SourceItemId,
        uint ResultItemId,
        int Quantity);

    private sealed record PieceRecipeExpectation(
        uint PieceItemId,
        uint GemItemId);

    private sealed record ResultSubIdExpectation(
        GearMentorOperation Operation,
        GearMentorStatus Status,
        int ExpectedSubId);
}
