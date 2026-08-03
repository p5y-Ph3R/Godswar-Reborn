using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Focused checks for the pure dialogue-37 conversion planner.
/// </summary>
internal static partial class ClassSuitConversionPlannerChecks
{
    private const int GearSlot = 10;
    private const int MaterialSlot = 11;

    public static Task RunAsync()
    {
        CheckCanonicalBranchTable();
        CheckTierIConversionPreservesEquipment();
        CheckEquippedTierIConversionIsLocationAware();
        CheckTierAndProfessionAuthority();
        CheckTierIIReverseRefundAndAttributeRemoval();
        CheckTierIVReverseAndCapacityAreAtomic();
        return Task.CompletedTask;
    }

    private static void CheckEquippedTierIConversionIsLocationAware()
    {
        var gear = RichEquipment(1013, bound: 0);
        var insignia = Material(
            ClassSuitConversionCatalog.PromotionalInsigniaI,
            stack: 5,
            bound: 1);
        var kitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            MaterialSlot,
            insignia.ToCompactString());
        var result = ClassSuitConversionPlanner.CreateForEquippedGear(
            TestItemContent.Catalog,
            kitBag,
            profession: 0,
            playerLevel: 120,
            gear,
            new ClassSuitEquippedConversionRequest(
                ClassSuitConversionOperation.ExchangeTierI,
                EquipmentSlots.Weapon,
                gear,
                ClassSuitSlotSelection.Capture(
                    kitBag,
                    MaterialSlot)));

        Check.True(
            result.Committed &&
            result.EquipmentBefore == gear &&
            result.EquipmentAfter.Id == 1032 &&
            result.EquipmentAfter.Quality == 20 &&
            result.EquipmentAfter.Grade == 25 &&
            result.EquipmentAfter.Exp == 777 &&
            result.EquipmentAfter.HolySuitCode == 705,
            $"equipped weapon converts without losing state ({result.RejectionReason})");
        Check.Equal(
            (short)2,
            KitBagSlots.GetItem(
                result.UpdatedKitBag,
                MaterialSlot).Stack,
            "equipped conversion consumes only the staged bag insignia");
        Check.True(
            result.Mutations.Count == 1 &&
            result.Mutations[0].KitBagSlot == MaterialSlot,
            "equipped conversion keeps equipment outside bag mutation evidence");

        var wrongSlot = ClassSuitConversionPlanner.CreateForEquippedGear(
            TestItemContent.Catalog,
            kitBag,
            profession: 0,
            playerLevel: 120,
            gear,
            new ClassSuitEquippedConversionRequest(
                ClassSuitConversionOperation.ExchangeTierI,
                EquipmentSlots.Armor,
                gear,
                ClassSuitSlotSelection.Capture(
                    kitBag,
                    MaterialSlot)));
        Check.True(
            !wrongSlot.Committed &&
            wrongSlot.Status ==
                ClassSuitConversionStatus.InvalidEquipment &&
            wrongSlot.UpdatedKitBag == kitBag,
            "equipped Class Suit conversion rejects the wrong physical slot atomically");
    }

    private static void CheckCanonicalBranchTable()
    {
        Check.Equal(
            62,
            ClassSuitConversionCatalog.Branches.Count,
            "Class Suit conversion branch count");
        var allTierIds = ClassSuitConversionCatalog.Branches
            .SelectMany(static branch => new[]
            {
                branch.TierIItemId,
                branch.TierIIItemId,
                branch.TierIIIItemId,
                branch.TierIVItemId
            })
            .Order()
            .ToArray();
        Check.True(
            allTierIds.SequenceEqual(ClassSuitItemCatalog.AllItemIds),
            "62 conversion branches cover the canonical 248 Class Suit items");

        AssertBranch(0, 1013, ClassSuitTier.TierI, 1032, cost: 3);
        AssertBranch(3, 1813, ClassSuitTier.TierI, 1832, cost: 3);
        AssertBranch(0, 2013, ClassSuitTier.TierI, 2031, cost: 1);
        AssertBranch(2, 2013, ClassSuitTier.TierI, 2041, cost: 1);
        AssertBranch(3, 3513, ClassSuitTier.TierI, 2861, cost: 1);
        AssertBranch(2, 3613, ClassSuitTier.TierI, 2951, cost: 1);
        AssertBranch(0, 3232, ClassSuitTier.TierIV, 3236, cost: 2);
        AssertBranch(0, 3235, ClassSuitTier.TierIV, 3237, cost: 2);
        AssertBranch(3, 3262, ClassSuitTier.TierIV, 3266, cost: 2);
        AssertBranch(3, 3265, ClassSuitTier.TierIV, 3267, cost: 2);
        Check.True(
            ClassSuitConversionCatalog.TryResolveReverse(
                profession: 3,
                sourceItemId: 2861,
                out var magicGloveReverse) &&
            magicGloveReverse.CommonItemId == 3513,
            "caster glove reverse uses the magic common family");
        Check.True(
            ClassSuitConversionCatalog.TryResolveReverse(
                profession: 3,
                sourceItemId: 2961,
                out var magicBootReverse) &&
            magicBootReverse.CommonItemId == 3613,
            "caster boots reverse uses the magic common family");

        foreach (var branch in ClassSuitConversionCatalog.Branches)
        {
            foreach (var tier in new[]
                     {
                         ClassSuitTier.TierI,
                         ClassSuitTier.TierII,
                         ClassSuitTier.TierIII,
                         ClassSuitTier.TierIV
                     })
            {
                Check.True(
                    TestItemContent.Catalog.TryGet(
                        branch.ItemIdFor(tier),
                        out var template) &&
                    template.ClassIds.Contains((short)branch.Profession) &&
                    template.MinLevel.HasValue,
                    $"Class Suit {branch.Key} {tier} has pinned class and level authority");
                Check.True(
                    ClassSuitConversionCatalog.TryResolveSuit(
                        branch.ItemIdFor(tier),
                        out var anyProfessionBranch,
                        out var anyProfessionTier) &&
                    anyProfessionBranch == branch &&
                    anyProfessionTier == tier,
                    $"globally unique Class Suit item {branch.ItemIdFor(tier)} resolves without client profession input");
                Check.True(
                    ClassSuitConversionCatalog.TryResolveReverse(
                        branch.Profession,
                        branch.ItemIdFor(tier),
                        out var reverse) &&
                    reverse.Branch == branch &&
                    reverse.SourceTier == tier &&
                    reverse.Refunds.Count == (int)tier &&
                    reverse.Refunds.Select(static refund => refund.ItemId)
                        .SequenceEqual(
                            Enumerable.Range(1, (int)tier)
                                .Select(value =>
                                    ClassSuitConversionCatalog.InsigniaFor(
                                        (ClassSuitTier)value))) &&
                    reverse.Refunds.All(refund =>
                        refund.Quantity == branch.InsigniaCost),
                    $"Class Suit {branch.Key} {tier} has a cumulative reverse recipe");
            }
        }
    }

    private static void CheckTierIConversionPreservesEquipment()
    {
        var gear = RichEquipment(1013, bound: 0);
        var insignia = Material(
            ClassSuitConversionCatalog.PromotionalInsigniaI,
            stack: 5,
            bound: 1);
        var kitBag = Stage(gear, insignia);
        var result = ClassSuitConversionPlanner.Create(
            TestItemContent.Catalog,
            kitBag,
            profession: 0,
            playerLevel: 120,
            ForwardRequest(
                kitBag,
                ClassSuitConversionOperation.ExchangeTierI));

        Check.True(
            result.Committed,
            $"common weapon converts to Class Suit I ({result.RejectionReason})");
        var converted = KitBagSlots.GetItem(result.UpdatedKitBag, GearSlot);
        Check.Equal(1032u, converted.Id, "Class Suit I weapon target ID");
        Check.Equal((short)1, converted.Bound, "bound insignia binds converted gear");
        Check.Equal((short)20, converted.Quality, "quality survives Class Suit conversion");
        Check.Equal((short)25, converted.Grade, "grade survives Class Suit conversion");
        Check.Equal(40, converted.Attribute1 ?? -1, "first appended attribute survives");
        Check.Equal(60, converted.Attribute2 ?? -1, "second ordinary attribute survives forward conversion");
        Check.True(
            converted.ClassAttribute1 is null && converted.ClassAttribute2 is null,
            "conversion to Class Suit I cannot carry class-specific attributes");
        Check.Equal(777, converted.Exp, "stored gear EXP survives conversion");
        Check.Equal(705, converted.HolySuitCode, "Holy Suit code survives conversion");
        Check.Equal((short)2, converted.SocketCount, "socket count survives conversion");
        Check.Equal(501, converted.Socket1EffectId ?? -1, "socket effect survives conversion");
        Check.Equal((short)2, KitBagSlots.GetItem(result.UpdatedKitBag, MaterialSlot).Stack,
            "weapon conversion consumes three Promotional Insignia I");
        Check.Equal(1, result.Materials.Count, "forward conversion has one material plan entry");
        Check.True(
            result.Materials[0].Direction ==
            ClassSuitMaterialDirection.Consumed,
            "forward material plan is consumption");
    }

    private static void CheckTierAndProfessionAuthority()
    {
        var tierThree = RichEquipment(1034, bound: 1) with
        {
            ClassAttribute1 = 200,
            ElementalAttribute1 = 480,
            ElementalAttribute2 = 483
        };
        var tierFourInsignia = Material(
            ClassSuitConversionCatalog.PromotionalInsigniaIV,
            stack: 3,
            bound: 1);
        var kitBag = Stage(tierThree, tierFourInsignia);
        AssertRejected(
            ClassSuitConversionPlanner.Create(
                TestItemContent.Catalog,
                kitBag,
                profession: 0,
                playerLevel: 149,
                ForwardRequest(
                    kitBag,
                    ClassSuitConversionOperation.UpgradeTierIV)),
            kitBag,
            ClassSuitConversionStatus.PlayerLevelTooLow,
            "target template minimum level is authoritative");

        var upgraded = ClassSuitConversionPlanner.Create(
            TestItemContent.Catalog,
            kitBag,
            profession: 0,
            playerLevel: 200,
            ForwardRequest(
                kitBag,
                ClassSuitConversionOperation.UpgradeTierIV));
        Check.True(
            upgraded.Committed &&
            upgraded.EquipmentAfter.Id == 1035 &&
            upgraded.EquipmentAfter.ClassAttribute1 == 200 &&
            upgraded.EquipmentAfter.ClassAttribute2 is null &&
            upgraded.EquipmentAfter.ElementalAttribute1 == 480 &&
            upgraded.EquipmentAfter.ElementalAttribute2 == 483,
            "Tier III to IV preserves one class and two elemental attributes");

        var wrongClassAttributeBag = Stage(
            tierThree with { ClassAttribute1 = 220 },
            tierFourInsignia);
        AssertRejected(
            ClassSuitConversionPlanner.Create(
                TestItemContent.Catalog,
                wrongClassAttributeBag,
                profession: 0,
                playerLevel: 200,
                ForwardRequest(
                    wrongClassAttributeBag,
                    ClassSuitConversionOperation.UpgradeTierIV)),
            wrongClassAttributeBag,
            ClassSuitConversionStatus.InvalidEquipment,
            "Tier III to IV rejects another profession's Class Suit attribute");

        var invalidTierTwoBag = Stage(
            RichEquipment(1033, bound: 1) with
            {
                ClassAttribute1 = 200
            },
            Material(
                ClassSuitConversionCatalog.PromotionalInsigniaIII,
                stack: 3,
                bound: 1));
        AssertRejected(
            ClassSuitConversionPlanner.Create(
                TestItemContent.Catalog,
                invalidTierTwoBag,
                profession: 0,
                playerLevel: 200,
                ForwardRequest(
                    invalidTierTwoBag,
                    ClassSuitConversionOperation.UpgradeTierIII)),
            invalidTierTwoBag,
            ClassSuitConversionStatus.InvalidEquipment,
            "Tier II class attributes are rejected instead of silently dropped");

        var foreignKitBag = Stage(
            RichEquipment(1032, bound: 1),
            Material(
                ClassSuitConversionCatalog.PromotionalInsigniaII,
                stack: 3,
                bound: 1));
        AssertRejected(
            ClassSuitConversionPlanner.Create(
                TestItemContent.Catalog,
                foreignKitBag,
                profession: 1,
                playerLevel: 200,
                ForwardRequest(
                    foreignKitBag,
                    ClassSuitConversionOperation.UpgradeTierII)),
            foreignKitBag,
            ClassSuitConversionStatus.ProfessionMismatch,
            "another profession cannot upgrade a Class Suit");

        var wrongInsigniaBag = Stage(
            RichEquipment(1032, bound: 1),
            Material(
                ClassSuitConversionCatalog.PromotionalInsigniaI,
                stack: 3,
                bound: 1));
        AssertRejected(
            ClassSuitConversionPlanner.Create(
                TestItemContent.Catalog,
                wrongInsigniaBag,
                profession: 0,
                playerLevel: 200,
                ForwardRequest(
                    wrongInsigniaBag,
                    ClassSuitConversionOperation.UpgradeTierII)),
            wrongInsigniaBag,
            ClassSuitConversionStatus.InvalidInsignia,
            "each target tier requires its exact insignia");
    }

    private static void AssertBranch(
        byte profession,
        uint source,
        ClassSuitTier targetTier,
        uint target,
        int cost)
    {
        Check.True(
            ClassSuitConversionCatalog.TryResolveForward(
                profession,
                source,
                targetTier,
                out var rule),
            $"Class Suit mapping {profession}:{source}->{targetTier}");
        Check.Equal(target, rule.TargetItemId, $"Class Suit target for {profession}:{source}");
        Check.Equal(cost, rule.InsigniaQuantity, $"Class Suit insignia cost for {profession}:{source}");
    }

    private static ClassSuitConversionRequest ForwardRequest(
        string kitBag,
        ClassSuitConversionOperation operation) =>
        new(
            operation,
            ClassSuitSlotSelection.Capture(kitBag, GearSlot),
            ClassSuitSlotSelection.Capture(kitBag, MaterialSlot));

    private static string Stage(
        CompactItemEntry gear,
        CompactItemEntry material)
    {
        var kitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            GearSlot,
            gear.ToCompactString());
        return KitBagSlots.SetSlot(
            kitBag,
            MaterialSlot,
            material.ToCompactString());
    }

    private static CompactItemEntry RichEquipment(
        uint itemId,
        short bound) =>
        CompactItemEntry.Empty with
        {
            Id = itemId,
            Attribute1 = 40,
            Attribute2 = 60,
            AttributeLevel1 = 3,
            AttributeLevel2 = 1,
            Quality = 20,
            Grade = 25,
            Bound = bound,
            Stack = 1,
            Exp = 777,
            HolySuitCode = 705,
            SocketCount = 2,
            Socket1EffectId = 501,
            Socket1Level = 4,
            Socket2EffectId = 502,
            Socket2Level = 3
        };

    private static CompactItemEntry Material(
        uint itemId,
        short stack,
        short bound) =>
        CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 1,
            Grade = 1,
            Bound = bound,
            Stack = stack
        };

    private static int Quantity(
        string kitBag,
        uint itemId,
        short bound) =>
        Enumerable.Range(0, 96)
            .Select(slot => KitBagSlots.GetItem(kitBag, slot))
            .Where(item => item.Id == itemId && item.Bound == bound)
            .Sum(static item => item.Stack);

    private static void AssertRejected(
        ClassSuitConversionResult result,
        string originalKitBag,
        ClassSuitConversionStatus status,
        string scenario)
    {
        Check.True(
            result.Status == status,
            $"{scenario} rejection status");
        Check.Equal(originalKitBag, result.UpdatedKitBag, $"{scenario} leaves the bag unchanged");
        Check.Equal(0, result.Mutations.Count, $"{scenario} has no committed mutations");
        Check.Equal(0, result.Materials.Count, $"{scenario} has no committed material plan");
    }
}
