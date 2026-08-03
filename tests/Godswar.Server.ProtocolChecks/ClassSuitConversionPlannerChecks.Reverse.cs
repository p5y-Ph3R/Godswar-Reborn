using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ClassSuitConversionPlannerChecks
{
    private static void CheckTierIIReverseRefundAndAttributeRemoval()
    {
        var tierTwo = RichEquipment(1033, bound: 1) with
        {
            Attribute1 = 40,
            AttributeLevel1 = 3,
            Attribute2 = 60,
            AttributeLevel2 = 2,
            Attribute3 = null,
            AttributeLevel3 = null,
            Attribute4 = null,
            AttributeLevel4 = null,
            Attribute5 = null,
            AttributeLevel5 = null
        };
        var kitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            GearSlot,
            tierTwo.ToCompactString());
        var result = ClassSuitConversionPlanner.Create(
            TestItemContent.Catalog,
            kitBag,
            profession: 0,
            playerLevel: 200,
            new ClassSuitConversionRequest(
                ClassSuitConversionOperation.ConvertToCommon,
                ClassSuitSlotSelection.Capture(kitBag, GearSlot)));

        Check.True(
            result.Committed,
            $"Class Suit II reverses safely ({result.RejectionReason})");
        var common = KitBagSlots.GetItem(result.UpdatedKitBag, GearSlot);
        Check.Equal(1013u, common.Id, "Tier II reverse common weapon ID");
        Check.Equal(
            40,
            common.Attribute1 ?? -1,
            "ordinary attribute one survives reverse");
        Check.Equal(
            (short)3,
            common.AttributeLevel1 ?? -1,
            "ordinary attribute level one survives reverse");
        Check.Equal(
            60,
            common.Attribute2 ?? -1,
            "ordinary attributes compact after class removal");
        Check.Equal(
            (short)2,
            common.AttributeLevel2 ?? -1,
            "compacted attribute level stays paired");
        Check.True(
            common.Attribute3 is null &&
            common.Attribute4 is null &&
            common.Attribute5 is null &&
            common.ClassAttribute1 is null &&
            common.ClassAttribute2 is null &&
            common.ElementalAttribute1 is null &&
            common.ElementalAttribute2 is null,
            "Tier II reverse retains only ordinary weapon attributes");
        Check.Equal((short)20, common.Quality, "reverse preserves quality");
        Check.Equal((short)25, common.Grade, "reverse preserves grade");
        Check.Equal(777, common.Exp, "reverse preserves stored EXP");
        Check.Equal(
            705,
            common.HolySuitCode,
            "reverse preserves Holy Suit code");
        Check.Equal(
            3,
            Quantity(
                result.UpdatedKitBag,
                ClassSuitConversionCatalog.PromotionalInsigniaI,
                1),
            "Tier II reverse refunds cumulative tier-I insignias");
        Check.Equal(
            3,
            Quantity(
                result.UpdatedKitBag,
                ClassSuitConversionCatalog.PromotionalInsigniaII,
                1),
            "Tier II reverse refunds tier-II insignias");
        Check.Equal(
            2,
            result.Materials.Count,
            "Tier II reverse has two refund plan entries");
        Check.True(
            result.Materials.All(static value =>
                value.Direction == ClassSuitMaterialDirection.Granted),
            "reverse material plan contains grants only");

        var splitRefundBag = GameDefaults.EmptyKitBag;
        splitRefundBag = KitBagSlots.SetSlot(
            splitRefundBag,
            0,
            Material(
                ClassSuitConversionCatalog.PromotionalInsigniaI,
                98,
                bound: 1).ToCompactString());
        splitRefundBag = KitBagSlots.SetSlot(
            splitRefundBag,
            1,
            Material(
                ClassSuitConversionCatalog.PromotionalInsigniaII,
                98,
                bound: 1).ToCompactString());
        splitRefundBag = KitBagSlots.SetSlot(
            splitRefundBag,
            GearSlot,
            tierTwo.ToCompactString());
        var splitRefund = ClassSuitConversionPlanner.Create(
            TestItemContent.Catalog,
            splitRefundBag,
            profession: 0,
            playerLevel: 200,
            new ClassSuitConversionRequest(
                ClassSuitConversionOperation.ConvertToCommon,
                ClassSuitSlotSelection.Capture(
                    splitRefundBag,
                    GearSlot)));
        Check.True(
            splitRefund.Committed && splitRefund.Mutations.Count == 5,
            "Tier II reverse permits the bounded five-slot split-refund plan");

        var hiddenElement = tierTwo with
        {
            Attribute2 = 480,
            AttributeLevel2 = 1
        };
        var hiddenElementBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            GearSlot,
            hiddenElement.ToCompactString());
        AssertRejected(
            ClassSuitConversionPlanner.Create(
                TestItemContent.Catalog,
                hiddenElementBag,
                profession: 0,
                playerLevel: 200,
                new ClassSuitConversionRequest(
                    ClassSuitConversionOperation.ConvertToCommon,
                    ClassSuitSlotSelection.Capture(
                        hiddenElementBag,
                        GearSlot))),
            hiddenElementBag,
            ClassSuitConversionStatus.InvalidEquipment,
            "reverse rejects an elemental ID hidden in an ordinary slot");
    }

    private static void CheckTierIVReverseAndCapacityAreAtomic()
    {
        var tierFour = RichEquipment(1035, bound: 1) with
        {
            Attribute1 = 40,
            AttributeLevel1 = 3,
            Attribute2 = 60,
            AttributeLevel2 = 1,
            ClassAttribute1 = 200,
            ElementalAttribute1 = 480,
            ElementalAttribute2 = 483
        };
        var tierFourBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            GearSlot,
            tierFour.ToCompactString());
        var reversed = ClassSuitConversionPlanner.Create(
            TestItemContent.Catalog,
            tierFourBag,
            profession: 0,
            playerLevel: 200,
            new ClassSuitConversionRequest(
                ClassSuitConversionOperation.ConvertToCommon,
                ClassSuitSlotSelection.Capture(tierFourBag, GearSlot)));
        Check.True(
            reversed.Committed,
            $"Class Suit IV reverses safely ({reversed.RejectionReason})");
        var common = KitBagSlots.GetItem(
            reversed.UpdatedKitBag,
            GearSlot);
        Check.True(
            common.Id == 1013 &&
            common.Attribute1 == 40 &&
            common.AttributeLevel1 == 3 &&
            common.Attribute2 == 60 &&
            common.AttributeLevel2 == 1 &&
            common.ClassAttribute1 is null &&
            common.ClassAttribute2 is null &&
            common.ElementalAttribute1 is null &&
            common.ElementalAttribute2 is null,
            "Tier IV reverse restores common gear and strips all dedicated attributes");
        foreach (var tier in new[]
                 {
                     ClassSuitTier.TierI,
                     ClassSuitTier.TierII,
                     ClassSuitTier.TierIII,
                     ClassSuitTier.TierIV
                 })
        {
            Check.Equal(
                3,
                Quantity(
                    reversed.UpdatedKitBag,
                    ClassSuitConversionCatalog.InsigniaFor(tier),
                    bound: 1),
                $"Tier IV reverse refunds cumulative {tier} insignias");
        }
        Check.True(
            reversed.Materials.Count == 4 &&
            reversed.Materials.All(static material =>
                material.Direction == ClassSuitMaterialDirection.Granted),
            "Tier IV reverse records four bound refund grants");
        Check.Equal(
            5,
            reversed.Mutations.Count,
            "Tier IV reverse mutates its gear and four refund slots");

        var equippedReverse = ClassSuitConversionPlanner
            .CreateForEquippedGear(
                TestItemContent.Catalog,
                GameDefaults.EmptyKitBag,
                profession: 0,
                playerLevel: 200,
                tierFour,
                new ClassSuitEquippedConversionRequest(
                    ClassSuitConversionOperation.ConvertToCommon,
                    EquipmentSlots.Weapon,
                    tierFour));
        Check.True(
            equippedReverse.Committed &&
            equippedReverse.EquipmentAfter.Id == 1013 &&
            equippedReverse.EquipmentAfter.Attribute1 == 40 &&
            equippedReverse.EquipmentAfter.Attribute2 == 60 &&
            equippedReverse.EquipmentAfter.ClassAttribute1 is null &&
            equippedReverse.EquipmentAfter.ClassAttribute2 is null &&
            equippedReverse.Mutations.Count == 4,
            "equipped Tier IV weapon reverses and returns all four refunds to the bag");
        foreach (var tier in new[]
                 {
                     ClassSuitTier.TierI,
                     ClassSuitTier.TierII,
                     ClassSuitTier.TierIII,
                     ClassSuitTier.TierIV
                 })
        {
            Check.Equal(
                3,
                Quantity(
                    equippedReverse.UpdatedKitBag,
                    ClassSuitConversionCatalog.InsigniaFor(tier),
                    bound: 1),
                $"equipped Tier IV reverse refunds cumulative {tier} insignias");
        }

        var fullBag = GameDefaults.EmptyKitBag;
        for (var slot = 0; slot < 96; slot++)
        {
            fullBag = KitBagSlots.SetSlot(
                fullBag,
                slot,
                Material(4234, 99, bound: 1).ToCompactString());
        }
        foreach (var freeSlot in new[] { 0, 1, 2 })
        {
            fullBag = KitBagSlots.ClearSlot(fullBag, freeSlot);
        }
        fullBag = KitBagSlots.SetSlot(
            fullBag,
            GearSlot,
            tierFour.ToCompactString());
        AssertRejected(
            ClassSuitConversionPlanner.Create(
                TestItemContent.Catalog,
                fullBag,
                profession: 0,
                playerLevel: 200,
                new ClassSuitConversionRequest(
                    ClassSuitConversionOperation.ConvertToCommon,
                    ClassSuitSlotSelection.Capture(fullBag, GearSlot))),
            fullBag,
            ClassSuitConversionStatus.InsufficientCapacity,
            "Tier IV reverse is atomic when only three of four refunds fit");
    }
}
