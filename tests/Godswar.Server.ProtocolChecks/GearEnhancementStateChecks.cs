using System.Buffers.Binary;
using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// Pure state checks keep the material and mutation rules independent from the
// network envelope and persistence-provider integrations.
internal static class GearEnhancementStateChecks
{
    public static Task RunAsync()
    {
        CheckMaterialCatalog();
        CheckAdd();
        CheckEnhance();
        CheckDeleteAndCompaction();
        CheckLegendaryChainAnchor();
        CheckRejectionsDoNotMutate();
        CheckNativeItemSelection();
        CheckCommitContextGuards();
        CheckPreciseNativeResultMapping();
        return Task.CompletedTask;
    }

    private static void CheckMaterialCatalog()
    {
        Check.Equal(51, GearEnhancementMaterialCatalog.All.Count, "gear-enhancement material count");
        Check.Equal(45, GearEnhancementMaterialCatalog.AttributeStones.Count, "Attribute Stone count");
        Check.Equal(
            GearEnhancementMaterialCatalog.All.Count,
            GearEnhancementMaterialCatalog.All.Select(static material => material.ItemId).Distinct().Count(),
            "gear-enhancement material IDs are unique");
        Check.True(
            !GearEnhancementMaterialCatalog.TryGet(9939, out _),
            "the ItemBaseAttribute gap at item 9939 remains unsupported");

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
        var result = GearEnhancementPlanner.Create(kitBag, request);

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
        var duplicate = GearEnhancementPlanner.Create(duplicateBag, duplicateRequest);
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
            GearEnhancementPlanner.Create(fullBag, fullRequest),
            fullBag,
            GearEnhancementStatus.AttributeSlotsFull,
            "Add enforces the five-attribute cap");
    }

    private static void CheckEnhance()
    {
        var legacyGear = Item(1000) with { Attribute1 = 0 };
        var (legacyBag, legacyRequest) = Stage(
            GearEnhancementOperation.Enhance,
            legacyGear,
            Item(9930),
            Item(9960));
        var legacyResult = GearEnhancementPlanner.Create(legacyBag, legacyRequest);
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
        var result = GearEnhancementPlanner.Create(kitBag, request);
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
            var step = GearEnhancementPlanner.Create(stepBag, stepRequest);
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
            GearEnhancementPlanner.Create(wrongBag, wrongRequest),
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
            GearEnhancementPlanner.Create(desynchronizedBag, desynchronizedRequest),
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
            GearEnhancementPlanner.Create(singleBag, singleRequest),
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
        var result = GearEnhancementPlanner.Create(kitBag, request);

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
        var added = GearEnhancementPlanner.Create(addBag, addRequest);
        Check.True(added.Committed, $"stylish legendary Add commits ({added.RejectionReason})");
        Check.Equal(305, added.EquipmentAfter.Attribute1 ?? -1, "Add selects the first chain member allowed by MainAttribute");
        Check.Equal((short)1, added.EquipmentAfter.AttributeLevel1 ?? 0, "legendary Add stores level 1");

        var (enhanceBag, enhanceRequest) = Stage(
            GearEnhancementOperation.Enhance,
            added.EquipmentAfter,
            Item(9970),
            Item(9960));
        var enhanced = GearEnhancementPlanner.Create(enhanceBag, enhanceRequest);
        CheckRejectedUnchanged(
            enhanced,
            enhanceBag,
            GearEnhancementStatus.AttributeNotEnhanceable,
            "legendary stones remain add/delete-only");
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
            GearEnhancementPlanner.Create(wrongStoneBag, wrongStoneRequest),
            wrongStoneBag,
            GearEnhancementStatus.AttributeNotAllowed,
            "a physical-defense stone is invalid for the starter sword");

        var (wrongCatalystBag, wrongCatalystRequest) = Stage(
            GearEnhancementOperation.Add,
            gear,
            Item(9930),
            Item(9991));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(wrongCatalystBag, wrongCatalystRequest),
            wrongCatalystBag,
            GearEnhancementStatus.InvalidCatalyst,
            "Add rejects Water Grain");

        var (nonEquipmentBag, nonEquipmentRequest) = Stage(
            GearEnhancementOperation.Add,
            Item(4000),
            Item(9930),
            Item(9990));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(nonEquipmentBag, nonEquipmentRequest),
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
            GearEnhancementPlanner.Create(missingBag, missingRequest),
            missingBag,
            GearEnhancementStatus.AttributeMissing,
            "Delete requires the exact matching attribute family");

        var staleBag = KitBagSlots.SetSlot(
            wrongCatalystBag,
            StoneSlot,
            Item(9930, stack: 2).ToCompactString());
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(staleBag, wrongCatalystRequest),
            staleBag,
            GearEnhancementStatus.StaleSelection,
            "changed material stacks invalidate staged selections");
    }

    private static void CheckNativeItemSelection()
    {
        var kitBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 0, (Item(1000) with
        {
            Attribute1 = 0,
            Attribute2 = 40,
            Attribute3 = 60,
            Attribute4 = 80
        }).ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 1, Item(9960, stack: 99).ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 12, Item(9930, stack: 99).ToCompactString());

        var gearPacket = NativeSelectionPacket(pageSlot: 0, selected: true, scratch: 0x579601);
        var quartzPacket = NativeSelectionPacket(pageSlot: 1, selected: true, scratch: 0x577501);
        var stonePacket = NativeSelectionPacket(pageSlot: 12, selected: true, scratch: 0x577501);
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(gearPacket, out var gearSelection),
            "native Gear Mentor selection accepts its unstable scratch tail");
        Check.Equal(0, gearSelection.KitBagSlot, "native Gear Mentor decodes bag slot 0");
        Check.True(gearSelection.Selected, "native Gear Mentor decodes selected flag");

        var now = DateTimeOffset.UtcNow;
        var context = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: GearEnhancementOperation.Enhance,
            expiresAt: now.AddMinutes(2),
            utcNow: () => now);
        Check.True(
            context.Apply(gearSelection, kitBag).Role == GearEnhancerSelectionRole.Gear,
            "first native selection is the gear control");
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(quartzPacket, out var quartzSelection),
            "native Quartz selection parses");
        Check.True(
            context.Apply(quartzSelection, kitBag).Role == GearEnhancerSelectionRole.Catalyst,
            "second native selection is the operation catalyst control");
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(stonePacket, out var stoneSelection),
            "native Attribute Stone selection parses");
        Check.True(
            context.Apply(stoneSelection, kitBag).Role == GearEnhancerSelectionRole.AttributeStone,
            "third native selection is the Attribute Stone control");
        Check.Equal(0, context.GearKitBagSlot, "native context stages gear slot 0");
        Check.Equal(1, context.CatalystKitBagSlot, "native context stages Quartz slot 1");
        Check.Equal(12, context.AttributeStoneKitBagSlot, "native context stages Strength Stone slot 12");
        var scratchArgs = Enumerable.Repeat(
                -1,
                GearEnhancerProtocol.FunctionActionArgumentCount)
            .ToArray();
        scratchArgs[GearEnhancerProtocol.GearArgumentIndex] = 0x0CA589;
        var scratchShape = GearEnhancerProtocol.ReadSelection(
            scratchArgs,
            out _,
            out _,
            out _);
        Check.True(
            scratchShape == GearEnhancerSelectionShape.MalformedCommit,
            "native final-action scratch can look like a malformed inline commit");
        Check.True(
            context.TryResolveNativeCommit(
                scratchShape,
                out var stagedSelections) &&
            stagedSelections.GearKitBagSlot == 0 &&
            stagedSelections.CatalystKitBagSlot == 1 &&
            stagedSelections.AttributeStoneKitBagSlot == 12,
            "native 10193 staging overrides harmless malformed final-action scratch");
        Check.True(
            stagedSelections.Gear.ExpectedItem == KitBagSlots.GetItem(kitBag, 0) &&
            stagedSelections.Catalyst.ExpectedItem == KitBagSlots.GetItem(kitBag, 1) &&
            stagedSelections.AttributeStone.ExpectedItem == KitBagSlots.GetItem(kitBag, 12),
            "native staging retains the exact selected item snapshots");
        var replacedGearBag = KitBagSlots.SetSlot(
            kitBag,
            stagedSelections.GearKitBagSlot,
            Item(1004).ToCompactString());
        var replacedGearRequest = new GearEnhancementRequest(
            GearEnhancementOperation.Enhance,
            new GearEnhancementSlotSelection(
                stagedSelections.Gear.KitBagSlot,
                stagedSelections.Gear.ExpectedItem),
            new GearEnhancementSlotSelection(
                stagedSelections.AttributeStone.KitBagSlot,
                stagedSelections.AttributeStone.ExpectedItem),
            new GearEnhancementSlotSelection(
                stagedSelections.Catalyst.KitBagSlot,
                stagedSelections.Catalyst.ExpectedItem));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(replacedGearBag, replacedGearRequest),
            replacedGearBag,
            GearEnhancementStatus.StaleSelection,
            "a replacement item in a staged native slot is never silently enhanced");
        Check.True(
            context.IsActiveFor(
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Enhance,
                now),
            "native selection context is bound to account, character, NPC, dialog, and operation");
        Check.True(
            !context.IsActiveFor(
                13,
                3,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Enhance,
                now),
            "native selection context cannot cross characters");

        var stockPhysicalContext = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: null,
            expiresAt: now.AddMinutes(2),
            utcNow: () => now);
        Check.True(
            stockPhysicalContext.IsActiveFor(
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Enhance,
                now) &&
            stockPhysicalContext.IsActiveFor(
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Add,
                now),
            "physical initial-menu context binds its operation only from final 10069");
        stockPhysicalContext.Apply(gearSelection, kitBag);
        stockPhysicalContext.Apply(quartzSelection, kitBag);
        stockPhysicalContext.Apply(stoneSelection, kitBag);
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(
                NativeSelectionPacket(pageSlot: 0, selected: false, scratch: 0x22B92D00),
                out var clearGear),
            "observed native gear confirmation-clear packet parses");
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(
                NativeSelectionPacket(pageSlot: 1, selected: false, scratch: 0x22B92D00),
                out var clearQuartz),
            "observed native Quartz confirmation-clear packet parses");
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(
                NativeSelectionPacket(pageSlot: 12, selected: false, scratch: 0x22B92D00),
                out var clearStone),
            "observed native Attribute Stone confirmation-clear packet parses");
        stockPhysicalContext.Apply(clearGear, kitBag);
        stockPhysicalContext.Apply(clearQuartz, kitBag);
        stockPhysicalContext.Apply(clearStone, kitBag);
        Check.True(
            stockPhysicalContext.GearKitBagSlot == -1 &&
            stockPhysicalContext.CatalystKitBagSlot == -1 &&
            stockPhysicalContext.AttributeStoneKitBagSlot == -1,
            "native Start visually clears all three active controls");
        Check.True(
            stockPhysicalContext.TryResolveNativeCommit(
                scratchShape,
                out var clearedSelections) &&
            clearedSelections.GearKitBagSlot == 0 &&
            clearedSelections.CatalystKitBagSlot == 1 &&
            clearedSelections.AttributeStoneKitBagSlot == 12,
            "observed select-then-clear-then-final sequence retains the complete commit triplet");

        var correlationNow = now;
        var expiredPhysicalContext = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: null,
            expiresAt: now.AddMinutes(2),
            utcNow: () => correlationNow);
        expiredPhysicalContext.Apply(gearSelection, kitBag);
        expiredPhysicalContext.Apply(quartzSelection, kitBag);
        expiredPhysicalContext.Apply(stoneSelection, kitBag);
        expiredPhysicalContext.Apply(clearGear, kitBag);
        expiredPhysicalContext.Apply(clearQuartz, kitBag);
        expiredPhysicalContext.Apply(clearStone, kitBag);
        correlationNow += GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
        Check.True(
            !expiredPhysicalContext.TryResolveNativeCommit(scratchShape, out _),
            "a completed native clear triplet cannot be revived after its short correlation window");

        var incompletePhysicalContext = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: null,
            expiresAt: now.AddMinutes(2),
            utcNow: () => now);
        incompletePhysicalContext.Apply(gearSelection, kitBag);
        incompletePhysicalContext.Apply(quartzSelection, kitBag);
        Check.True(
            !incompletePhysicalContext.TryResolveNativeCommit(
                scratchShape,
                out _),
            "an unbound initial-menu context cannot turn an incomplete selection into a commit");

        var request = new GearEnhancementRequest(
            GearEnhancementOperation.Enhance,
            GearEnhancementSlotSelection.Capture(kitBag, context.GearKitBagSlot),
            GearEnhancementSlotSelection.Capture(kitBag, context.AttributeStoneKitBagSlot),
            GearEnhancementSlotSelection.Capture(kitBag, context.CatalystKitBagSlot));
        var exactAttempt = GearEnhancementPlanner.Create(kitBag, request);
        Check.True(
            exactAttempt.Committed,
            $"the observed Short Sword + QP1 + Strength Stone attempt succeeds ({exactAttempt.RejectionReason})");
        Check.Equal(1, exactAttempt.EquipmentAfter.Attribute1 ?? -1, "observed Strength attribute advances to template 1");
        Check.Equal((short)2, exactAttempt.EquipmentAfter.AttributeLevel1 ?? 0, "observed Strength attribute advances to level 2");

        var wrongOrder = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: GearEnhancementOperation.Enhance,
            expiresAt: now.AddMinutes(2),
            utcNow: () => now);
        wrongOrder.Apply(gearSelection, kitBag);
        wrongOrder.Apply(stoneSelection, kitBag);
        wrongOrder.Apply(quartzSelection, kitBag);
        var wrongOrderRequest = new GearEnhancementRequest(
            GearEnhancementOperation.Enhance,
            GearEnhancementSlotSelection.Capture(kitBag, wrongOrder.GearKitBagSlot),
            GearEnhancementSlotSelection.Capture(kitBag, wrongOrder.AttributeStoneKitBagSlot),
            GearEnhancementSlotSelection.Capture(kitBag, wrongOrder.CatalystKitBagSlot));
        var wrongOrderResult = GearEnhancementPlanner.Create(kitBag, wrongOrderRequest);
        CheckRejectedUnchanged(
            wrongOrderResult,
            kitBag,
            GearEnhancementStatus.InvalidAttributeStone,
            "native second/third controls reject a Stone and Quartz placed in the wrong order");
        Check.Equal(
            GearEnhancerProtocol.MissingAttributeStoneResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Enhance,
                wrongOrderResult,
                wrongOrderRequest),
            "wrong native Attribute Stone control receives the specific third-slot error");

        var removalPacket = NativeSelectionPacket(pageSlot: 1, selected: false, scratch: 0x0CA58900);
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(removalPacket, out var removal),
            "native removal packet ignores scratch tail");
        var removalResult = context.Apply(removal, kitBag);
        Check.True(
            removalResult.Status == GearEnhancerSelectionStageStatus.Removed,
            "native removal clears its staged role");
        Check.Equal(-1, context.CatalystKitBagSlot, "native removal clears only the catalyst control");
        Check.True(
            !context.TryResolveNativeCommit(
                scratchShape,
                out _),
            "a normal single-slot removal cannot reuse the previously complete triplet");

        var malformed = NativeSelectionPacket(pageSlot: 1, selected: true, scratch: 0);
        malformed[8] = 2;
        Check.True(
            !GearEnhancerItemSelectionPacket.TryParse(malformed, out _),
            "native selection rejects flags other than selected/removed");
    }

    private static void CheckCommitContextGuards()
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var originContext = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.OriginDialogIndex,
            operation: GearEnhancementOperation.Enhance,
            expiresAt: now.AddMinutes(2));
        Check.True(
            GameClientHandler.GearEnhancerCommitContextMatches(
                originContext,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancementOperation.Enhance,
                now),
            "Origin Enhancer inline commits retain their live operation-bound page context");
        Check.True(
            !GameClientHandler.GearEnhancerCommitContextMatches(
                null,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancementOperation.Enhance,
                now) &&
            !GameClientHandler.GearEnhancerCommitContextMatches(
                originContext,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancementOperation.Add,
                now) &&
            !GameClientHandler.GearEnhancerCommitContextMatches(
                originContext,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancementOperation.Enhance,
                originContext.ExpiresAt),
            "inline enhancement commits reject missing, mismatched, and expired contexts");

        var physicalContext = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: null,
            expiresAt: now.AddMinutes(2));
        Check.True(
            GameClientHandler.GearEnhancerCommitContextMatches(
                physicalContext,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Add,
                now) &&
            GameClientHandler.GearEnhancerCommitContextMatches(
                physicalContext,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Delete,
                now),
            "physical Gear Mentor keeps its client-local Add/Enhance/Delete final-operation binding");
        Check.True(
            !GameClientHandler.GearEnhancerCommitContextMatches(
                physicalContext,
                GearEnhancerProtocol.DecomposeGearSubId,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Enhance,
                now),
            "a Gear Mentor transaction page cannot be reused to commit Add/Enhance/Delete");

        Check.True(
            GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                GearEnhancerProtocol.DecomposeGearSubId,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DecomposeGearSubId,
                now),
            "Decompose commit requires its live matching operation page");
        Check.True(
            !GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                GearEnhancerProtocol.MakeAttributeStoneSubId,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DecomposeGearSubId,
                now) &&
            !GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                GearEnhancerProtocol.DecomposeGearSubId,
                13,
                2,
                GearEnhancerProtocol.AthensEnhancerNpcId,
                GearEnhancerProtocol.DecomposeGearSubId,
                now),
            "Gear Mentor commits reject a mismatched page marker or physical NPC");
        Check.True(
            GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                GearEnhancerProtocol.CombineGemPiecesActionSubId,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.CombineGemPiecesActionSubId,
                now),
            "gem-piece action 201 is accepted only through its matching page marker");
    }

    private static void CheckPreciseNativeResultMapping()
    {
        var equipment = Item(1000);
        var baseResult = new GearEnhancementResult(
            GearEnhancementStatus.StaleSelection,
            GearEnhancementOperation.Enhance,
            GameDefaults.EmptyKitBag,
            GameDefaults.EmptyKitBag,
            equipment,
            equipment,
            [],
            "A staged item changed.");

        Check.Equal(
            GearEnhancerProtocol.SelectedItemMissingResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(GearEnhancementOperation.Enhance, baseResult),
            "stale native selection maps to chosen-item-missing instead of generic 1019");
        Check.Equal(
            GearEnhancerProtocol.MissingGearResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Enhance,
                baseResult with { Status = GearEnhancementStatus.UnsupportedEquipment }),
            "unsupported gear maps to the first-slot gear error");
        Check.Equal(
            GearEnhancerProtocol.QuartzLevelMismatchResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Enhance,
                baseResult with { Status = GearEnhancementStatus.AttributeLevelMismatch }),
            "stored/template attribute level mismatch maps to the native level error");
        Check.Equal(
            GearEnhancerProtocol.AttributeNotEnhanceableResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Enhance,
                baseResult with { Status = GearEnhancementStatus.AttributeAmbiguous }),
            "ambiguous Enhance attribute maps to cannot-enhance instead of generic 1019");
        Check.Equal(
            GearEnhancerProtocol.MissingDeleteAttributeResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Delete,
                baseResult with { Status = GearEnhancementStatus.AttributeAmbiguous }),
            "ambiguous Delete attribute maps to the native matching-attribute error");
    }

    private static byte[] NativeSelectionPacket(int pageSlot, bool selected, int scratch)
    {
        var payload = new byte[GearEnhancerItemSelectionPacket.PayloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), pageSlot);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), scratch);
        payload[8] = selected ? (byte)1 : (byte)0;
        return payload;
    }

    private static void CheckRejectedUnchanged(
        GearEnhancementResult result,
        string expectedKitBag,
        GearEnhancementStatus expectedStatus,
        string description)
    {
        Check.True(!result.Committed && result.Status == expectedStatus, description);
        Check.Equal(expectedKitBag, result.UpdatedKitBag, $"{description}: kit bag is unchanged");
        Check.Equal(0, result.Mutations.Count, $"{description}: no mutations are emitted");
    }

    private const int GearSlot = 10;
    private const int StoneSlot = 11;
    private const int CatalystSlot = 12;

    private static (string KitBag, GearEnhancementRequest Request) Stage(
        GearEnhancementOperation operation,
        CompactItemEntry gear,
        CompactItemEntry stone,
        CompactItemEntry catalyst)
    {
        var kitBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, GearSlot, gear.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, StoneSlot, stone.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, CatalystSlot, catalyst.ToCompactString());
        return (
            kitBag,
            new GearEnhancementRequest(
                operation,
                GearEnhancementSlotSelection.Capture(kitBag, GearSlot),
                GearEnhancementSlotSelection.Capture(kitBag, StoneSlot),
                GearEnhancementSlotSelection.Capture(kitBag, CatalystSlot)));
    }

    private static CompactItemEntry Item(
        uint itemId,
        short stack = 1,
        short bound = 0)
    {
        return CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 1,
            Grade = 1,
            Stack = stack,
            Bound = bound
        };
    }
}
