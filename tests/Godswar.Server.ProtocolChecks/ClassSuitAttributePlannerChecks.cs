using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Focused checks for dialogue-37 class-specific weapon attributes.
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
        CheckTierBoundFifthSlotRules();
        CheckDeleteCompactsAttributesAndConsumesWater();
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
                result.EquipmentAfter.Attribute1 == row.Attribute,
                $"profession {row.Profession} stone {row.Stone} adds attribute {row.Attribute}");
        }
    }

    private static void CheckAddConsumesExactMaterialsAndPreservesGear()
    {
        var gear = Weapon(1034, bound: 0) with
        {
            Attribute1 = 40,
            AttributeLevel1 = 2,
            Attribute2 = 60,
            AttributeLevel2 = 3
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
            $"class-specific weapon attribute add commits ({result.RejectionReason})");
        var updated = KitBagSlots.GetItem(result.UpdatedKitBag, GearSlot);
        Check.Equal(1034u, updated.Id, "class-stat add preserves weapon ID");
        Check.Equal(40, updated.Attribute1 ?? -1, "class-stat add preserves attribute one");
        Check.Equal(60, updated.Attribute2 ?? -1, "class-stat add preserves attribute two");
        Check.Equal(200, updated.Attribute3 ?? -1, "Primal Stone adds profession-zero stat 200");
        Check.Equal((short)1, updated.AttributeLevel3 ?? -1, "new class stat starts at level one");
        Check.Equal((short)1, updated.Bound, "bound class stone binds the weapon");
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
            Attribute1 = 201,
            AttributeLevel1 = 1
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
            "a weapon cannot hold a second class-specific stat");

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
            "common weapons cannot receive Class Suit stats");

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
            "another profession cannot modify the weapon");
    }

    private static void CheckDeleteCompactsAttributesAndConsumesWater()
    {
        var gear = Weapon(1032, bound: 0) with
        {
            Attribute1 = 40,
            AttributeLevel1 = 2,
            Attribute2 = 200,
            AttributeLevel2 = 1,
            Attribute3 = 60,
            AttributeLevel3 = 4
        };
        var kitBag = StageDelete(
            gear,
            Material(GearEnhancementMaterialCatalog.WaterGrainItemId, 1, 1));
        var result = ClassSuitAttributePlanner.Create(
            TestItemContent.Catalog,
            kitBag,
            profession: 0,
            DeleteRequest(kitBag));

        Check.True(
            result.Committed,
            $"class-specific weapon attribute delete commits ({result.RejectionReason})");
        var updated = KitBagSlots.GetItem(result.UpdatedKitBag, GearSlot);
        Check.Equal(40, updated.Attribute1 ?? -1, "delete preserves ordinary attribute one");
        Check.Equal((short)2, updated.AttributeLevel1 ?? -1, "delete preserves ordinary level one");
        Check.Equal(60, updated.Attribute2 ?? -1, "delete compacts ordinary attribute two");
        Check.Equal((short)4, updated.AttributeLevel2 ?? -1, "delete compacts paired ordinary level");
        Check.True(
            updated.Attribute3 is null && updated.Attribute4 is null && updated.Attribute5 is null,
            "delete removes only the class-specific stat");
        Check.Equal((short)1, updated.Bound, "bound Water Grain binds the resulting weapon");
        Check.Equal((short)20, updated.Quality, "delete preserves weapon quality");
        Check.Equal((short)25, updated.Grade, "delete preserves weapon grade");
        Check.True(
            KitBagSlots.GetItem(result.UpdatedKitBag, CatalystSlot).IsEmpty,
            "delete consumes one Water Grain");
        Check.Equal(2, result.Mutations.Count, "delete has exact gear/water mutations");
        Check.Equal(1, result.Materials.Count, "delete has one consumption plan entry");
    }

    private static void CheckDeleteAndStaleRejectionsAreAtomic()
    {
        var missingBag = StageDelete(
            Weapon(1032, bound: 1) with
            {
                Attribute1 = 40,
                AttributeLevel1 = 2
            },
            Material(GearEnhancementMaterialCatalog.WaterGrainItemId, 1, 1));
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                missingBag,
                profession: 0,
                DeleteRequest(missingBag)),
            missingBag,
            ClassSuitAttributeStatus.ClassAttributeMissing,
            "delete requires an existing class-specific stat");

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
            ClassSuitSlotSelection.Capture(kitBag, CatalystSlot));

    private static string StageAdd(
        CompactItemEntry gear,
        CompactItemEntry catalyst,
        CompactItemEntry stone)
    {
        var bag = StageDelete(gear, catalyst);
        return KitBagSlots.SetSlot(
            bag,
            StoneSlot,
            stone.ToCompactString());
    }

    private static string StageDelete(
        CompactItemEntry gear,
        CompactItemEntry catalyst)
    {
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            GearSlot,
            gear.ToCompactString());
        return KitBagSlots.SetSlot(
            bag,
            CatalystSlot,
            catalyst.ToCompactString());
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
