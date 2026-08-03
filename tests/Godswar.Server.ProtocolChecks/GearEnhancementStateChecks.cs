using System.Buffers.Binary;
using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// Pure state checks keep the material and mutation rules independent from the
// network envelope and persistence-provider integrations.
internal static partial class GearEnhancementStateChecks
{
    public static Task RunAsync()
    {
        CheckMaterialCatalog();
        CheckAdd();
        CheckFifthAttributeReservation();
        CheckEnhance();
        CheckDeleteAndCompaction();
        CheckLegendaryChainAnchor();
        CheckErebusAttributePools();
        CheckRejectionsDoNotMutate();
        CheckNativeItemSelection();
        CheckCommitContextGuards();
        CheckPreciseNativeResultMapping();
        return Task.CompletedTask;
    }

    private static void CheckMaterialCatalog()
    {
        Check.Equal(72, GearEnhancementMaterialCatalog.All.Count, "gear-enhancement material count");
        Check.Equal(66, GearEnhancementMaterialCatalog.AttributeStones.Count, "Attribute Stone count");
        Check.Equal(
            GearEnhancementMaterialCatalog.All.Count,
            GearEnhancementMaterialCatalog.All.Select(static material => material.ItemId).Distinct().Count(),
            "gear-enhancement material IDs are unique");
        Check.True(
            !GearEnhancementMaterialCatalog.TryGet(9939, out _),
            "the ItemBaseAttribute gap at item 9939 remains unsupported");

        Check.True(
            GearEnhancementMaterialCatalog.TryGet(16300, out var firePower) &&
            firePower.DisplayName == "Fire Power Stone" &&
            firePower.Texture == "./Localization/en_us/UI/Texture/Icon2.gwo" &&
            firePower.Icon == "504,576" &&
            firePower.AllowedAttributeIds.SequenceEqual([480]),
            "Fire Power Stone has the locked item, icon, and attribute mapping");
        Check.True(
            GearEnhancementMaterialCatalog.TryGet(16320, out var darkPenetration) &&
            darkPenetration.DisplayName == "Dark Penetration Stone" &&
            darkPenetration.Icon == "720,576" &&
            darkPenetration.AllowedAttributeIds.SequenceEqual([500]),
            "Dark Penetration Stone closes the locked elemental range");

        Check.True(GearEnhancementMaterialCatalog.TryGet(9930, out var strength), "Strength Stone resolves");
        Check.Equal("Material1", strength.NameKey, "Strength Stone name key");
        Check.Equal("Strength Stone", strength.DisplayName, "Strength Stone display name");
        Check.Equal("./Localization/en_us/UI/Texture/Icon2.gwo", strength.Texture, "Strength Stone texture");
        Check.Equal("504,468", strength.Icon, "Strength Stone icon");
        Check.True(
            strength.AllowedAttributeIds.SequenceEqual(new[] { 0, 1, 2, 3, 4 }),
            "Strength Stone maps to the physical-attack template chain");
        Check.True(strength.CanEnhance, "Strength Stone is Quartz-enhanceable");

        Check.True(GearEnhancementMaterialCatalog.TryGet(9959, out var penetration), "Spirit of Penetration resolves");
        Check.True(
            penetration.AllowedAttributeIds.SequenceEqual(new[] { 250 }),
            "Spirit of Penetration maps to IgnoreMagPer");

        Check.True(GearEnhancementMaterialCatalog.TryGet(9970, out var vitality), "Stone of Vitality resolves");
        Check.True(
            vitality.AllowedAttributeIds.SequenceEqual(Enumerable.Range(300, 8)),
            "Stone of Vitality maps to the MaxHPG chain");

        Check.True(GearEnhancementMaterialCatalog.TryGet(9985, out var impact), "Stone of Impact resolves");
        Check.Equal("Material45", impact.NameKey, "Stone of Impact name key");
        Check.Equal("216,108", impact.Icon, "Stone of Impact icon");
        Check.True(
            impact.AllowedAttributeIds.SequenceEqual(Enumerable.Range(450, 8)),
            "Stone of Impact maps to the CriIncVal chain");
        Check.True(!impact.CanEnhance, "legendary stones are add/delete-only");

        for (var level = 1; level <= 4; level++)
        {
            Check.True(
                GearEnhancementMaterialCatalog.TryGet(checked((uint)(9959 + level)), out var quartz),
                $"Quartz Plate {level} resolves");
            Check.Equal((short)level, quartz.SourceAttributeLevel ?? 0, $"Quartz Plate {level} source level");
            Check.Equal((short)(level + 1), quartz.TargetAttributeLevel ?? 0, $"Quartz Plate {level} target level");
        }

        Check.True(
            GearEnhancementMaterialCatalog.TryGet(GearEnhancementMaterialCatalog.FlameSparkItemId, out var flame) &&
            flame.Kind == GearEnhancementMaterialKind.FlameSpark,
            "Flame Spark catalyst resolves");
        Check.True(
            GearEnhancementMaterialCatalog.TryGet(GearEnhancementMaterialCatalog.WaterGrainItemId, out var water) &&
            water.Kind == GearEnhancementMaterialKind.WaterGrain,
            "Water Grain catalyst resolves");

        var template = strength.ToItemTemplateSeed();
        Check.Equal(9930, template.Id, "Strength Stone item-template ID");
        Check.Equal("consume item", template.Kind, "Strength Stone item-template kind");
        using var stats = JsonDocument.Parse(template.StatsJson);
        Check.Equal("99", stats.RootElement.GetProperty("Overlap").GetString() ?? string.Empty, "Strength Stone stack cap");
        Check.Equal("50,150", stats.RootElement.GetProperty("Distribution").GetString() ?? string.Empty, "Strength Stone distribution");
    }

    private static void CheckAdd()
    {
        var gear = Item(1000);
        var stone = Item(9930, stack: 2);
        var flame = Item(9990, bound: 1);
        var (kitBag, request) = Stage(GearEnhancementOperation.Add, gear, stone, flame);
        var result = GearEnhancementPlanner.Create(TestItemContent.Catalog, kitBag, request);

        Check.True(result.Committed, $"valid Add commits ({result.RejectionReason})");
        Check.Equal(0, result.EquipmentAfter.Attribute1 ?? -1, "Add writes the physical-attack chain base");
        Check.Equal((short)1, result.EquipmentAfter.AttributeLevel1 ?? 0, "Add writes synchronized level 1");
        Check.Equal((short)1, result.EquipmentAfter.Bound, "bound Flame Spark binds the gear");
        Check.Equal((short)1, KitBagSlots.GetItem(result.UpdatedKitBag, StoneSlot).Stack, "Add consumes one stone");
        Check.True(KitBagSlots.GetItem(result.UpdatedKitBag, CatalystSlot).IsEmpty, "Add consumes one Flame Spark");

        var duplicateGear = gear with { Attribute1 = 0, AttributeLevel1 = 1 };
        var (duplicateBag, duplicateRequest) = Stage(
            GearEnhancementOperation.Add,
            duplicateGear,
            Item(9930),
            Item(9990));
        var duplicate = GearEnhancementPlanner.Create(TestItemContent.Catalog, duplicateBag, duplicateRequest);
        CheckRejectedUnchanged(
            duplicate,
            duplicateBag,
            GearEnhancementStatus.AttributeAlreadyPresent,
            "Add rejects another member of an existing attribute family");

        var fullGear = gear with
        {
            Attribute1 = 0,
            Attribute2 = 40,
            Attribute3 = 60,
            Attribute4 = 80,
            Attribute5 = 110,
            AttributeLevel1 = 1,
            AttributeLevel2 = 1,
            AttributeLevel3 = 1,
            AttributeLevel4 = 1,
            AttributeLevel5 = 1
        };
        var (fullBag, fullRequest) = Stage(
            GearEnhancementOperation.Add,
            fullGear,
            Item(9935),
            Item(9990));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(TestItemContent.Catalog, fullBag, fullRequest),
            fullBag,
            GearEnhancementStatus.AttributeSlotsFull,
            "Add enforces the ordinary four-attribute cap");
    }

    private static void CheckEnhance()
    {
        var legacyGear = Item(1000) with { Attribute1 = 0 };
        var (legacyBag, legacyRequest) = Stage(
            GearEnhancementOperation.Enhance,
            legacyGear,
            Item(9930),
            Item(9960));
        var legacyResult = GearEnhancementPlanner.Create(TestItemContent.Catalog, legacyBag, legacyRequest);
        Check.True(
            legacyResult.Committed,
            $"legacy gear infers level from its attribute template ({legacyResult.RejectionReason})");
        Check.Equal(1, legacyResult.EquipmentAfter.Attribute1 ?? -1, "legacy enhancement advances its template ID");
        Check.Equal((short)2, legacyResult.EquipmentAfter.AttributeLevel1 ?? 0, "legacy enhancement begins storing the synchronized level");

        var gear = Item(1000) with { Attribute1 = 0, AttributeLevel1 = 1 };
        var (kitBag, request) = Stage(
            GearEnhancementOperation.Enhance,
            gear,
            Item(9930),
            Item(9960, stack: 2, bound: 1));
        var result = GearEnhancementPlanner.Create(TestItemContent.Catalog, kitBag, request);
        Check.True(result.Committed, $"valid Enhance commits ({result.RejectionReason})");
        Check.Equal(1, result.EquipmentAfter.Attribute1 ?? -1, "Enhance advances attribute template 0 to 1");
        Check.Equal((short)2, result.EquipmentAfter.AttributeLevel1 ?? 0, "Enhance synchronizes level 1 to 2");
        Check.Equal((short)1, result.EquipmentAfter.Bound, "bound Quartz Plate binds enhanced gear");
        Check.Equal((short)1, KitBagSlots.GetItem(result.UpdatedKitBag, CatalystSlot).Stack, "Enhance consumes exactly one Quartz Plate");

        for (short currentLevel = 1; currentLevel <= 4; currentLevel++)
        {
            var current = Item(1000) with
            {
                Attribute1 = currentLevel - 1,
                AttributeLevel1 = currentLevel
            };
            var (stepBag, stepRequest) = Stage(
                GearEnhancementOperation.Enhance,
                current,
                Item(9930),
                Item(checked((uint)(9959 + currentLevel))));
            var step = GearEnhancementPlanner.Create(TestItemContent.Catalog, stepBag, stepRequest);
            Check.True(step.Committed, $"Strength chain level {currentLevel} enhancement commits");
            Check.Equal((int)currentLevel, step.EquipmentAfter.Attribute1 ?? -1, $"Strength chain template advances at level {currentLevel}");
            Check.Equal((short)(currentLevel + 1), step.EquipmentAfter.AttributeLevel1 ?? 0, $"Strength chain level advances from {currentLevel}");
        }

        var (wrongBag, wrongRequest) = Stage(
            GearEnhancementOperation.Enhance,
            gear,
            Item(9930),
            Item(9961));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(TestItemContent.Catalog, wrongBag, wrongRequest),
            wrongBag,
            GearEnhancementStatus.QuartzLevelMismatch,
            "Enhance rejects the wrong Quartz Plate");

        var desynchronized = gear with { Attribute1 = 1, AttributeLevel1 = 1 };
        var (desynchronizedBag, desynchronizedRequest) = Stage(
            GearEnhancementOperation.Enhance,
            desynchronized,
            Item(9930),
            Item(9960));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(TestItemContent.Catalog, desynchronizedBag, desynchronizedRequest),
            desynchronizedBag,
            GearEnhancementStatus.AttributeLevelMismatch,
            "Enhance rejects desynchronized template and level fields");

        var singleTemplate = Item(1000) with { Attribute1 = 40, AttributeLevel1 = 1 };
        var (singleBag, singleRequest) = Stage(
            GearEnhancementOperation.Enhance,
            singleTemplate,
            Item(9940),
            Item(9960));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(TestItemContent.Catalog, singleBag, singleRequest),
            singleBag,
            GearEnhancementStatus.AttributeNotEnhanceable,
            "single-template attributes cannot be raised with Quartz Plates");
    }

    private static void CheckDeleteAndCompaction()
    {
        var gear = Item(1000) with
        {
            Attribute1 = 40,
            Attribute2 = 1,
            Attribute3 = 130,
            AttributeLevel1 = 1,
            AttributeLevel2 = 2,
            AttributeLevel3 = 3
        };
        var (kitBag, request) = Stage(
            GearEnhancementOperation.Delete,
            gear,
            Item(9930, bound: 1),
            Item(9991));
        var result = GearEnhancementPlanner.Create(TestItemContent.Catalog, kitBag, request);

        Check.True(result.Committed, $"valid Delete commits ({result.RejectionReason})");
        Check.Equal(40, result.EquipmentAfter.Attribute1 ?? -1, "Delete preserves the first unrelated attribute");
        Check.Equal(130, result.EquipmentAfter.Attribute2 ?? -1, "Delete compacts the following attribute");
        Check.True(!result.EquipmentAfter.Attribute3.HasValue, "Delete clears the final compacted attribute slot");
        Check.Equal((short)1, result.EquipmentAfter.AttributeLevel1 ?? 0, "Delete preserves the first unrelated level");
        Check.Equal((short)3, result.EquipmentAfter.AttributeLevel2 ?? 0, "Delete compacts the following level with its attribute");
        Check.True(!result.EquipmentAfter.AttributeLevel3.HasValue, "Delete clears the final compacted level slot");
        Check.Equal((short)1, result.EquipmentAfter.Bound, "bound deletion stone binds the gear");
        Check.True(KitBagSlots.GetItem(result.UpdatedKitBag, StoneSlot).IsEmpty, "Delete consumes one Attribute Stone");
        Check.True(KitBagSlots.GetItem(result.UpdatedKitBag, CatalystSlot).IsEmpty, "Delete consumes one Water Grain");
    }

    private static void CheckLegendaryChainAnchor()
    {
        var gear = Item(8280);
        var (addBag, addRequest) = Stage(
            GearEnhancementOperation.Add,
            gear,
            Item(9970),
            Item(9990));
        var added = GearEnhancementPlanner.Create(TestItemContent.Catalog, addBag, addRequest);
        Check.True(added.Committed, $"stylish legendary Add commits ({added.RejectionReason})");
        Check.Equal(305, added.EquipmentAfter.Attribute1 ?? -1, "Add selects the first chain member allowed by MainAttribute");
        Check.Equal((short)1, added.EquipmentAfter.AttributeLevel1 ?? 0, "legendary Add stores level 1");

        var (enhanceBag, enhanceRequest) = Stage(
            GearEnhancementOperation.Enhance,
            added.EquipmentAfter,
            Item(9970),
            Item(9960));
        var enhanced = GearEnhancementPlanner.Create(TestItemContent.Catalog, enhanceBag, enhanceRequest);
        CheckRejectedUnchanged(
            enhanced,
            enhanceBag,
            GearEnhancementStatus.AttributeNotEnhanceable,
            "legendary stones remain add/delete-only");
    }

    private static void CheckErebusAttributePools()
    {
        for (var offset = 0; offset < 10; offset++)
        {
            var erebus = ItemTemplateSeeds.All.Single(template => template.Id == 16200 + offset);
            var source = ItemTemplateSeeds.All.Single(template => template.Id == 14500 + Math.Min(offset, 8));
            Check.True(
                MainAttributes(erebus).SequenceEqual(MainAttributes(source)),
                $"Erebus tier {offset} copies its level-matched mount-gear attribute pool");
        }

        var levelEightyPool = MainAttributes(
            ItemTemplateSeeds.All.Single(static template => template.Id == 16204));
        int[] warriorOffensive = [343, 363, 403, 423];
        int[] levelOneHundredTwenty = [347, 367, 407, 427];
        Check.True(
            warriorOffensive.All(levelEightyPool.Contains),
            "level-80 Erebus allows the Warrior G25 offensive attribute IDs");
        Check.True(
            levelOneHundredTwenty.All(attribute => !levelEightyPool.Contains(attribute)),
            "level-80 Erebus rejects suffix-7 level-120 attributes even at Q20/G25");

        var (kitBag, request) = Stage(
            GearEnhancementOperation.Add,
            Item(16204),
            Item(9974),
            Item(9990));
        var result = GearEnhancementPlanner.Create(TestItemContent.Catalog, kitBag, request);
        Check.True(result.Committed, $"level-80 Erebus accepts its copied pool ({result.RejectionReason})");
        Check.Equal(341, result.EquipmentAfter.Attribute1 ?? -1, "Erebus Add starts at its level-80 chain anchor");
    }

    private static int[] MainAttributes(ItemTemplateSeed template)
    {
        using var document = JsonDocument.Parse(template.StatsJson);
        return document.RootElement
            .GetProperty("MainAttribute")
            .GetString()!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();
    }

    private static void CheckRejectionsDoNotMutate()
    {
        var gear = Item(1000);
        var (wrongStoneBag, wrongStoneRequest) = Stage(
            GearEnhancementOperation.Add,
            gear,
            Item(9931),
            Item(9990));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(TestItemContent.Catalog, wrongStoneBag, wrongStoneRequest),
            wrongStoneBag,
            GearEnhancementStatus.AttributeNotAllowed,
            "a physical-defense stone is invalid for the starter sword");

        var (wrongCatalystBag, wrongCatalystRequest) = Stage(
            GearEnhancementOperation.Add,
            gear,
            Item(9930),
            Item(9991));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(TestItemContent.Catalog, wrongCatalystBag, wrongCatalystRequest),
            wrongCatalystBag,
            GearEnhancementStatus.InvalidCatalyst,
            "Add rejects Water Grain");

        var (elementalStoneBag, elementalStoneRequest) = Stage(
            GearEnhancementOperation.Add,
            gear,
            Item(16300),
            Item(9990));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(
                TestItemContent.Catalog,
                elementalStoneBag,
                elementalStoneRequest),
            elementalStoneBag,
            GearEnhancementStatus.InvalidAttributeStone,
            "ordinary Gear Enhancement cannot consume an elemental Class Suit stone");

        var (nonEquipmentBag, nonEquipmentRequest) = Stage(
            GearEnhancementOperation.Add,
            Item(4000),
            Item(9930),
            Item(9990));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(TestItemContent.Catalog, nonEquipmentBag, nonEquipmentRequest),
            nonEquipmentBag,
            GearEnhancementStatus.UnsupportedEquipment,
            "non-equipment is rejected");

        var missingAttributeGear = gear with { Attribute1 = 40, AttributeLevel1 = 1 };
        var (missingBag, missingRequest) = Stage(
            GearEnhancementOperation.Delete,
            missingAttributeGear,
            Item(9930),
            Item(9991));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(TestItemContent.Catalog, missingBag, missingRequest),
            missingBag,
            GearEnhancementStatus.AttributeMissing,
            "Delete requires the exact matching attribute family");

        var staleBag = KitBagSlots.SetSlot(
            wrongCatalystBag,
            StoneSlot,
            Item(9930, stack: 2).ToCompactString());
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(TestItemContent.Catalog, staleBag, wrongCatalystRequest),
            staleBag,
            GearEnhancementStatus.StaleSelection,
            "changed material stacks invalidate staged selections");
    }
}
