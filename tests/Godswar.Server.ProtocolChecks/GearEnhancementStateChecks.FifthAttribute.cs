using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearEnhancementStateChecks
{
    private static void CheckFifthAttributeReservation()
    {
        foreach (var itemId in new uint[] { 1000, 1032, 1033 })
        {
            var firstFourFull = Item(itemId) with
            {
                Attribute1 = 0,
                Attribute2 = 40,
                Attribute3 = 60,
                Attribute4 = 80,
                AttributeLevel1 = 1,
                AttributeLevel2 = 1,
                AttributeLevel3 = 1,
                AttributeLevel4 = 1
            };
            var (kitBag, request) = Stage(
                GearEnhancementOperation.Add,
                firstFourFull,
                Item(9935),
                Item(GearEnhancementMaterialCatalog.FlameSparkItemId));
            CheckRejectedUnchanged(
                GearEnhancementPlanner.Create(
                    TestItemContent.Catalog,
                    kitBag,
                    request),
                kitBag,
                GearEnhancementStatus.AttributeSlotsFull,
                $"ordinary Add reserves slot five for item {itemId}");
        }

        var gapBeforeSlotFive = Item(1000) with
        {
            Attribute1 = 40,
            Attribute3 = 60,
            Attribute4 = 80,
            Attribute5 = 110,
            AttributeLevel1 = 1,
            AttributeLevel3 = 1,
            AttributeLevel4 = 1,
            AttributeLevel5 = 1
        };
        var (gapBag, gapRequest) = Stage(
            GearEnhancementOperation.Add,
            gapBeforeSlotFive,
            Item(9930),
            Item(GearEnhancementMaterialCatalog.FlameSparkItemId));
        var gapResult = GearEnhancementPlanner.Create(
            TestItemContent.Catalog,
            gapBag,
            gapRequest);
        Check.True(
            gapResult.Committed &&
            gapResult.EquipmentAfter.Attribute2 == 0 &&
            gapResult.EquipmentAfter.Attribute5 == 110,
            "ordinary Add fills slots one through four and preserves slot five");

        var slotFiveAttribute = Item(1000) with
        {
            Attribute5 = 0,
            AttributeLevel5 = 1
        };
        var (enhanceBag, enhanceRequest) = Stage(
            GearEnhancementOperation.Enhance,
            slotFiveAttribute,
            Item(9930),
            Item(9960));
        var enhanced = GearEnhancementPlanner.Create(
            TestItemContent.Catalog,
            enhanceBag,
            enhanceRequest);
        Check.True(
            enhanced.Committed &&
            enhanced.EquipmentAfter.Attribute5 == 1 &&
            enhanced.EquipmentAfter.AttributeLevel5 == 2,
            "ordinary Enhance retains support for an existing slot-five attribute");

        var (deleteBag, deleteRequest) = Stage(
            GearEnhancementOperation.Delete,
            slotFiveAttribute,
            Item(9930),
            Item(GearEnhancementMaterialCatalog.WaterGrainItemId));
        var deleted = GearEnhancementPlanner.Create(
            TestItemContent.Catalog,
            deleteBag,
            deleteRequest);
        Check.True(
            deleted.Committed &&
            deleted.EquipmentAfter.Attribute5 is null &&
            deleted.EquipmentAfter.AttributeLevel5 is null,
            "ordinary Delete retains support for an existing slot-five attribute");
    }
}
