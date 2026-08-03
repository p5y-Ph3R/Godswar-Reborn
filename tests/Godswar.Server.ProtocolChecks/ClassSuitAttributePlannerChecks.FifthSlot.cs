using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ClassSuitAttributePlannerChecks
{
    private static void CheckTierBoundFifthSlotRules()
    {
        var commonBag = StageAdd(
            FourOrdinaryAttributes(1013),
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
            "common weapons cannot use the reserved fifth slot");

        foreach (var itemId in new uint[] { 1032, 1033 })
        {
            var kitBag = StageAdd(
                FourOrdinaryAttributes(itemId),
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
            FourOrdinaryAttributes(1033) with
            {
                Attribute4 = null,
                AttributeLevel4 = null,
                Attribute5 = 130,
                AttributeLevel5 = 1
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

        foreach (var itemId in new uint[] { 1034, 1035 })
        {
            var kitBag = StageAdd(
                FourOrdinaryAttributes(itemId),
                Material(GearEnhancementMaterialCatalog.FlameSparkItemId, 1, 1),
                Material(9950, 1, 1));
            var result = ClassSuitAttributePlanner.Create(
                TestItemContent.Catalog,
                kitBag,
                profession: 0,
                AddRequest(kitBag));
            Check.True(
                result.Committed &&
                result.EquipmentAfter.Attribute1 == 40 &&
                result.EquipmentAfter.Attribute2 == 60 &&
                result.EquipmentAfter.Attribute3 == 80 &&
                result.EquipmentAfter.Attribute4 == 110 &&
                result.EquipmentAfter.Attribute5 == 200 &&
                result.EquipmentAfter.AttributeLevel5 == 1,
                $"Tier III/IV Class Suit item {itemId} can use slot five");
        }
    }

    private static CompactItemEntry FourOrdinaryAttributes(uint itemId) =>
        Weapon(itemId, bound: 1) with
        {
            Attribute1 = 40,
            Attribute2 = 60,
            Attribute3 = 80,
            Attribute4 = 110,
            AttributeLevel1 = 1,
            AttributeLevel2 = 1,
            AttributeLevel3 = 1,
            AttributeLevel4 = 1
        };
}
