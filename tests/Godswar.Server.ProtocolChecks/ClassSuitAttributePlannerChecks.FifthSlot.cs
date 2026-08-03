using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ClassSuitAttributePlannerChecks
{
    private static void CheckTierBoundClassSlotRules()
    {
        var commonBag = StageAdd(
            FiveOrdinaryAttributes(1013),
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
            "common gear cannot use dedicated class-attribute slots");

        foreach (var itemId in new uint[]
                 {
                     1032, 1033,
                     2131, 2132,
                     3131, 3132
                 })
        {
            var kitBag = StageAdd(
                FiveOrdinaryAttributes(itemId),
                Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
                Material(9950, 1, 1));
            AssertRejected(
                ClassSuitAttributePlanner.Create(
                    TestItemContent.Catalog,
                    kitBag,
                    profession: 0,
                    AddRequest(kitBag)),
                kitBag,
                ClassSuitAttributeStatus.InvalidWeapon,
                $"Class Suit item {itemId} cannot receive a class stone before Tier III");
        }

        var tierTwoGapBag = StageAdd(
            FiveOrdinaryAttributes(1033) with
            {
                Attribute4 = null,
                AttributeLevel4 = null
            },
            Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
            Material(9950, 1, 1));
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                tierTwoGapBag,
                profession: 0,
                AddRequest(tierTwoGapBag)),
            tierTwoGapBag,
            ClassSuitAttributeStatus.InvalidWeapon,
            "Tier II remains ineligible even when an ordinary slot is empty");

        var lowerTierDeleteBag = StageDelete(
            FiveOrdinaryAttributes(1032) with
            {
                ClassAttribute1 = 200
            },
            Material(
                GearEnhancementMaterialCatalog.WaterGrainItemId,
                1,
                1));
        AssertRejected(
            ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                lowerTierDeleteBag,
                profession: 0,
                DeleteRequest(lowerTierDeleteBag)),
            lowerTierDeleteBag,
            ClassSuitAttributeStatus.InvalidWeapon,
            "Class Suit I cannot retain or delete an out-of-policy class stat");

        foreach (var itemId in new uint[]
                 {
                     1034, 1035,
                     2133, 2134,
                     3133, 3134
                 })
        {
            var kitBag = StageAdd(
                FiveOrdinaryAttributes(itemId),
                Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
                Material(9950, 1, 1));
            var first = ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                kitBag,
                profession: 0,
                AddRequest(kitBag));
            Check.True(
                first.Committed &&
                first.EquipmentAfter.Attribute1 == 40 &&
                first.EquipmentAfter.Attribute2 == 60 &&
                first.EquipmentAfter.Attribute3 == 80 &&
                first.EquipmentAfter.Attribute4 == 110 &&
                first.EquipmentAfter.Attribute5 == 130 &&
                first.EquipmentAfter.ClassAttribute1 == 200 &&
                first.EquipmentAfter.ClassAttribute2 is null,
                $"Tier III/IV Class Suit item {itemId} adds its first separate class stat");

            var secondBag = StageAdd(
                first.EquipmentAfter,
                Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
                Material(9951, 1, 1));
            var second = ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                secondBag,
                profession: 0,
                AddRequest(secondBag));
            AssertRejected(
                second,
                secondBag,
                ClassSuitAttributeStatus.AttributeSlotsFull,
                $"Tier III/IV Class Suit item {itemId} rejects a second non-elemental class stat");

            var firstElementBag = StageAdd(
                first.EquipmentAfter,
                Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
                Material(16300, 1, 1));
            var firstElement = ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                firstElementBag,
                profession: 0,
                AddRequest(firstElementBag));
            Check.True(
                firstElement.Committed &&
                firstElement.EquipmentAfter.ClassAttribute1 == 200 &&
                firstElement.EquipmentAfter.ElementalAttribute1 == 480 &&
                firstElement.EquipmentAfter.ElementalAttribute2 is null,
                $"Class Suit item {itemId} adds Fire Power independently of its class stat");

            var sameElementBag = StageAdd(
                firstElement.EquipmentAfter,
                Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
                Material(16301, 1, 1));
            AssertRejected(
                ClassSuitAttributePlanner.Create(
                    TestItemContent.Catalog,
                    sameElementBag,
                    profession: 0,
                    AddRequest(sameElementBag)),
                sameElementBag,
                ClassSuitAttributeStatus.ElementAlreadyPresent,
                $"Class Suit item {itemId} rejects a second Fire family");

            var secondElementBag = StageAdd(
                firstElement.EquipmentAfter,
                Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
                Material(16303, 1, 1));
            var secondElement = ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                secondElementBag,
                profession: 0,
                AddRequest(secondElementBag));
            Check.True(
                secondElement.Committed &&
                secondElement.EquipmentAfter.ElementalAttribute1 == 480 &&
                secondElement.EquipmentAfter.ElementalAttribute2 == 483,
                $"Class Suit item {itemId} accepts two different elements");

            var thirdElementBag = StageAdd(
                secondElement.EquipmentAfter,
                Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
                Material(16306, 1, 1));
            AssertRejected(
                ClassSuitAttributePlanner.Create(
                    TestItemContent.Catalog,
                    thirdElementBag,
                    profession: 0,
                    AddRequest(thirdElementBag)),
                thirdElementBag,
                ClassSuitAttributeStatus.ElementalSlotsFull,
                $"Class Suit item {itemId} rejects a third elemental stat");
        }
    }

    private static CompactItemEntry FiveOrdinaryAttributes(uint itemId) =>
        Weapon(itemId, bound: 1) with
        {
            Attribute1 = 40,
            Attribute2 = 60,
            Attribute3 = 80,
            Attribute4 = 110,
            Attribute5 = 130,
            AttributeLevel1 = 1,
            AttributeLevel2 = 1,
            AttributeLevel3 = 1,
            AttributeLevel4 = 1,
            AttributeLevel5 = 1
        };
}
