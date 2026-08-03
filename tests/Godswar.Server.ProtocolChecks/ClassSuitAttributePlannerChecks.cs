using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Focused checks for dialogue-37 class-specific gear attributes.
/// </summary>
internal static partial class ClassSuitAttributePlannerChecks
{
    private const int GearSlot = 20;
    private const int CatalystSlot = 21;
    private const int StoneSlot = 22;

    public static Task RunAsync()
    {
        CheckProfessionStoneMap();
        CheckAddConsumesExactMaterialsAndPreservesGear();
        CheckAddAuthorityAndDuplicateRejections();
        CheckTierBoundClassSlotRules();
        CheckDeleteTargetsExactAttributeAndConsumesWater();
        CheckDeleteAndStaleRejectionsAreAtomic();
        return Task.CompletedTask;
    }

    private static void CheckProfessionStoneMap()
    {
        var rows = new[]
        {
            (Profession: (byte)0, Weapon: 1034u, Stone: 9950u, Attribute: 200),
            (Profession: (byte)0, Weapon: 1034u, Stone: 9951u, Attribute: 201),
            (Profession: (byte)0, Weapon: 1034u, Stone: 9952u, Attribute: 210),
            (Profession: (byte)0, Weapon: 1034u, Stone: 9953u, Attribute: 211),
            (Profession: (byte)1, Weapon: 1434u, Stone: 9950u, Attribute: 200),
            (Profession: (byte)1, Weapon: 1434u, Stone: 9951u, Attribute: 201),
            (Profession: (byte)1, Weapon: 1434u, Stone: 9952u, Attribute: 210),
            (Profession: (byte)1, Weapon: 1434u, Stone: 9953u, Attribute: 211),
            (Profession: (byte)2, Weapon: 1734u, Stone: 9954u, Attribute: 220),
            (Profession: (byte)2, Weapon: 1734u, Stone: 9955u, Attribute: 221),
            (Profession: (byte)2, Weapon: 1734u, Stone: 9956u, Attribute: 230),
            (Profession: (byte)2, Weapon: 1734u, Stone: 9957u, Attribute: 231),
            (Profession: (byte)3, Weapon: 1834u, Stone: 9954u, Attribute: 220),
            (Profession: (byte)3, Weapon: 1834u, Stone: 9955u, Attribute: 221),
            (Profession: (byte)3, Weapon: 1834u, Stone: 9956u, Attribute: 230),
            (Profession: (byte)3, Weapon: 1834u, Stone: 9957u, Attribute: 231)
        };
        foreach (var row in rows)
        {
            var bag = StageAdd(
                Weapon(row.Weapon, bound: 0),
                Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 0),
                Material(row.Stone, 1, 0));
            var result = ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                bag,
                row.Profession,
                AddRequest(bag));
            Check.True(
                result.Committed &&
                result.EquipmentAfter.ClassAttribute1 == row.Attribute,
                $"profession {row.Profession} stone {row.Stone} adds attribute {row.Attribute} " +
                $"(status {result.Status}, actual {result.EquipmentAfter.ClassAttribute1}, " +
                $"reason {result.RejectionReason})");
        }
    }

    private static void CheckAddConsumesExactMaterialsAndPreservesGear()
    {
        var gear = Weapon(1034, bound: 0) with
        {
            Attribute1 = 40,
            AttributeLevel1 = 2,
            Attribute2 = 60,
            AttributeLevel2 = 3,
            Attribute3 = 80,
            AttributeLevel3 = 1,
            Attribute4 = 110,
            AttributeLevel4 = 1,
            Attribute5 = 130,
            AttributeLevel5 = 1
        };
        var kitBag = StageAdd(
            gear,
            Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 2, 0),
            Material(9950, 1, 1));
        var result = ClassSuitAttributePlanner.Create(
            TestItemContent.Catalog,
            kitBag,
            profession: 0,
            AddRequest(kitBag));

        Check.True(
            result.Committed,
            $"class-specific gear attribute add commits ({result.RejectionReason})");
        var updated = KitBagSlots.GetItem(result.UpdatedKitBag, GearSlot);
        Check.Equal(1034u, updated.Id, "class-stat add preserves gear ID");
        Check.Equal(40, updated.Attribute1 ?? -1, "class-stat add preserves attribute one");
        Check.Equal(60, updated.Attribute2 ?? -1, "class-stat add preserves attribute two");
        Check.Equal(80, updated.Attribute3 ?? -1, "class-stat add preserves attribute three");
        Check.Equal(110, updated.Attribute4 ?? -1, "class-stat add preserves attribute four");
        Check.Equal(130, updated.Attribute5 ?? -1, "class-stat add preserves attribute five");
        Check.Equal(200, updated.ClassAttribute1 ?? -1, "Primal Stone adds class stat 200");
        Check.True(updated.ClassAttribute2 is null, "first class stat leaves class slot two empty");
        Check.Equal((short)1, updated.Bound, "bound class stone binds the gear");
        Check.Equal((short)20, updated.Quality, "class-stat add preserves quality");
        Check.Equal((short)25, updated.Grade, "class-stat add preserves grade");
        Check.Equal(777, updated.Exp, "class-stat add preserves stored EXP");
        Check.Equal(705, updated.HolySuitCode, "class-stat add preserves Holy Suit code");
        Check.Equal((short)1, KitBagSlots.GetItem(result.UpdatedKitBag, CatalystSlot).Stack,
            "class-stat add consumes one Flame Spark");
        Check.True(
            KitBagSlots.GetItem(result.UpdatedKitBag, StoneSlot).IsEmpty,
            "class-stat add consumes one class-specific stone");
        Check.Equal(3, result.Mutations.Count, "class-stat add has exact gear/flame/stone mutations");
        Check.Equal(2, result.Materials.Count, "class-stat add has two consumption plan entries");
    }

    private static void CheckAddAuthorityAndDuplicateRejections()
    {
        var duplicateGear = Weapon(1034, bound: 1) with
        {
            ClassAttribute1 = 200
        };
        var duplicateBag = StageAdd(
            duplicateGear,
            Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
            Material(9950, 1, 1));
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                duplicateBag,
                profession: 0,
                AddRequest(duplicateBag)),
            duplicateBag,
            ClassSuitAttributeStatus.ClassAttributeAlreadyPresent,
            "gear cannot hold the same class-specific stat twice");

        var wrongStoneBag = StageAdd(
            Weapon(1034, bound: 1),
            Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
            Material(9954, 1, 1));
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                wrongStoneBag,
                profession: 0,
                AddRequest(wrongStoneBag)),
            wrongStoneBag,
            ClassSuitAttributeStatus.InvalidClassStone,
            "melee professions cannot use a caster Holy Stone");

        var incompatibleExistingBag = StageAdd(
            Weapon(1034, bound: 1) with { ClassAttribute1 = 220 },
            Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
            Material(9950, 1, 1));
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                incompatibleExistingBag,
                profession: 0,
                AddRequest(incompatibleExistingBag)),
            incompatibleExistingBag,
            ClassSuitAttributeStatus.InvalidAttributeState,
            "melee gear rejects an existing caster class stat");

        var commonBag = StageAdd(
            Weapon(1013, bound: 1),
            Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
            Material(9950, 1, 1));
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                commonBag,
                profession: 0,
                AddRequest(commonBag)),
            commonBag,
            ClassSuitAttributeStatus.InvalidWeapon,
            "common gear cannot receive Class Suit stats");

        var foreignBag = StageAdd(
            Weapon(1034, bound: 1),
            Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
            Material(9950, 1, 1));
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                foreignBag,
                profession: 1,
                AddRequest(foreignBag)),
            foreignBag,
            ClassSuitAttributeStatus.ProfessionMismatch,
            "another profession cannot modify the gear");
    }

    private static void CheckDeleteTargetsExactAttributeAndConsumesWater()
    {
        var gear = Weapon(1034, bound: 0) with
        {
            Attribute1 = 40,
            AttributeLevel1 = 2,
            Attribute2 = 60,
            AttributeLevel2 = 4,
            ClassAttribute1 = 200,
            ElementalAttribute1 = 480,
            ElementalAttribute2 = 483
        };
        var kitBag = StageDelete(
            gear,
            Material(GearEnhancementMaterialCatalog.WaterGrainItemId, 1, 1),
            Material(16300, 7, 0));
        var result = ClassSuitAttributePlanner.Create(
            TestItemContent.Catalog,
            kitBag,
            profession: 0,
            DeleteRequest(kitBag));

        Check.True(
            result.Committed,
            $"class-specific gear attribute delete commits ({result.RejectionReason})");
        var updated = KitBagSlots.GetItem(result.UpdatedKitBag, GearSlot);
        Check.Equal(40, updated.Attribute1 ?? -1, "delete preserves ordinary attribute one");
        Check.Equal((short)2, updated.AttributeLevel1 ?? -1, "delete preserves ordinary level one");
        Check.Equal(60, updated.Attribute2 ?? -1, "delete preserves ordinary attribute two");
        Check.Equal((short)4, updated.AttributeLevel2 ?? -1, "delete preserves ordinary level two");
        Check.True(
            updated.ClassAttribute1 == 200 &&
            updated.ElementalAttribute1 == 483 &&
            updated.ElementalAttribute2 is null,
            "Prometheus Stone deletes Fire and compacts the remaining elemental stat");
        Check.Equal((short)1, updated.Bound, "bound Water Grain binds the resulting gear");
        Check.Equal((short)20, updated.Quality, "delete preserves gear quality");
        Check.Equal((short)25, updated.Grade, "delete preserves gear grade");
        Check.True(
            KitBagSlots.GetItem(result.UpdatedKitBag, CatalystSlot).IsEmpty,
            "delete consumes one Water Grain");
        Check.Equal(
            (short)6,
            KitBagSlots.GetItem(result.UpdatedKitBag, StoneSlot).Stack,
            "delete consumes one selector stone");
        Check.Equal(3, result.Mutations.Count, "delete has exact gear/water/stone mutations");
        Check.Equal(2, result.Materials.Count, "delete has two consumption plan entries");

        var secondBag = StageDelete(
            updated,
            Material(GearEnhancementMaterialCatalog.WaterGrainItemId, 1, 1),
            Material(9950, 3, 0));
        var second = ClassSuitAttributePlanner.Create(
            TestItemContent.Catalog,
            secondBag,
            profession: 0,
            DeleteRequest(secondBag));
        Check.True(
            second.Committed &&
            second.EquipmentAfter.ClassAttribute1 is null &&
            second.EquipmentAfter.ElementalAttribute1 == 483,
            "Primal Stone deletes only the matching class-specific stat");

        var thirdBag = StageDelete(
            second.EquipmentAfter,
            Material(GearEnhancementMaterialCatalog.WaterGrainItemId, 1, 1),
            Material(16303, 2, 0));
        var third = ClassSuitAttributePlanner.Create(
            TestItemContent.Catalog,
            thirdBag,
            profession: 0,
            DeleteRequest(thirdBag));
        Check.True(
            third.Committed &&
            third.EquipmentAfter.ClassAttribute1 is null &&
            third.EquipmentAfter.ElementalAttribute1 is null &&
            third.EquipmentAfter.ElementalAttribute2 is null,
            "Poseidon Stone deletes the matching Water stat");
    }

    private static void CheckDeleteAndStaleRejectionsAreAtomic()
    {
        var missingBag = StageDelete(
            Weapon(1034, bound: 1) with
            {
                Attribute1 = 40,
                AttributeLevel1 = 2
            },
            Material(GearEnhancementMaterialCatalog.WaterGrainItemId, 1, 1),
            Material(9950, 1, 1));
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                missingBag,
                profession: 0,
                DeleteRequest(missingBag)),
            missingBag,
            ClassSuitAttributeStatus.ClassAttributeMissing,
            "delete requires an existing class-specific stat");

        var mismatchedSelectorBag = StageDelete(
            Weapon(1034, bound: 1) with
            {
                ClassAttribute1 = 200,
                ElementalAttribute1 = 480
            },
            Material(GearEnhancementMaterialCatalog.WaterGrainItemId, 1, 1),
            Material(16303, 1, 1));
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                mismatchedSelectorBag,
                profession: 0,
                DeleteRequest(mismatchedSelectorBag)),
            mismatchedSelectorBag,
            ClassSuitAttributeStatus.ClassAttributeMissing,
            "a selector stone cannot delete a different Class Suit stat");

        var stagedBag = StageAdd(
            Weapon(1034, bound: 0),
            Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 2, 0),
            Material(9950, 1, 1));
        var request = AddRequest(stagedBag);
        var changedBag = KitBagSlots.SetSlot(
            stagedBag,
            CatalystSlot,
            Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 0)
                .ToCompactString());
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                changedBag,
                profession: 0,
                request),
            changedBag,
            ClassSuitAttributeStatus.StaleSelection,
            "changed material stack invalidates the staged action");
    }

    private static ClassSuitAttributeRequest AddRequest(string kitBag) =>
        new(
            ClassSuitAttributeOperation.AddClassSpecific,
            ClassSuitSlotSelection.Capture(kitBag, GearSlot),
            ClassSuitSlotSelection.Capture(kitBag, CatalystSlot),
            ClassSuitSlotSelection.Capture(kitBag, StoneSlot));

    private static ClassSuitAttributeRequest DeleteRequest(string kitBag) =>
        new(
            ClassSuitAttributeOperation.DeleteClassSpecific,
            ClassSuitSlotSelection.Capture(kitBag, GearSlot),
            ClassSuitSlotSelection.Capture(kitBag, CatalystSlot),
            ClassSuitSlotSelection.Capture(kitBag, StoneSlot));

    private static string StageAdd(
        CompactItemEntry gear,
        CompactItemEntry catalyst,
        CompactItemEntry stone)
    {
        return StageDelete(gear, catalyst, stone);
    }

    private static string StageDelete(
        CompactItemEntry gear,
        CompactItemEntry catalyst,
        CompactItemEntry selectorStone)
    {
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            GearSlot,
            gear.ToCompactString());
        bag = KitBagSlots.SetSlot(
            bag,
            CatalystSlot,
            catalyst.ToCompactString());
        return KitBagSlots.SetSlot(
            bag,
            StoneSlot,
            selectorStone.ToCompactString());
    }

    private static CompactItemEntry Weapon(uint itemId, short bound) =>
        CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 20,
            Grade = 25,
            Bound = bound,
            Stack = 1,
            Exp = 777,
            HolySuitCode = 705,
            SocketCount = 1,
            Socket1EffectId = 501,
            Socket1Level = 4
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

    private static void AssertRejected(
        ClassSuitAttributeResult result,
        string originalKitBag,
        ClassSuitAttributeStatus status,
        string scenario)
    {
        Check.True(result.Status == status, $"{scenario} rejection status");
        Check.Equal(originalKitBag, result.UpdatedKitBag, $"{scenario} leaves bag unchanged");
        Check.Equal(0, result.Mutations.Count, $"{scenario} commits no slot mutation");
        Check.Equal(0, result.Materials.Count, $"{scenario} commits no material change");
    }
}
