using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearEnhancementStateChecks
{
    private static void CheckPhysicalDefenseStoneDomains()
    {
        Check.True(
            GearEnhancementMaterialCatalog.TryGet(9958, out var characterStone) &&
            characterStone.AllowedAttributeIds.SequenceEqual([240]),
            "Spirit of Destruction owns the character-equipment physical-defense-disable attribute");
        Check.True(
            GearEnhancementMaterialCatalog.TryGet(9980, out var mountStone) &&
            mountStone.AllowedAttributeIds.SequenceEqual(
                Enumerable.Range(400, 8)),
            "Stone of Ruin owns the mount-family physical-defense-disable chain");

        var classSuitWeapon = Item(1035) with
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
        var (characterBag, characterRequest) = Stage(
            GearEnhancementOperation.Add,
            classSuitWeapon,
            Item(9958),
            Item(GearEnhancementMaterialCatalog.FlameSparkItemId));
        var characterResult = GearEnhancementPlanner.Create(
            TestItemContent.Catalog,
            characterBag,
            characterRequest);
        Check.True(
            characterResult.Committed &&
            characterResult.EquipmentAfter.Attribute5 == 240,
            "Spirit of Destruction is the character-weapon physical-defense-disable stone");

        var (wrongDomainBag, wrongDomainRequest) = Stage(
            GearEnhancementOperation.Add,
            classSuitWeapon,
            Item(9980),
            Item(GearEnhancementMaterialCatalog.FlameSparkItemId));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(
                TestItemContent.Catalog,
                wrongDomainBag,
                wrongDomainRequest),
            wrongDomainBag,
            GearEnhancementStatus.AttributeNotAllowed,
            "Stone of Ruin does not cross from the mount attribute pool into a character weapon");

        var (mountBag, mountRequest) = Stage(
            GearEnhancementOperation.Add,
            Item(16204),
            Item(9980),
            Item(GearEnhancementMaterialCatalog.FlameSparkItemId));
        var mountResult = GearEnhancementPlanner.Create(
            TestItemContent.Catalog,
            mountBag,
            mountRequest);
        Check.True(
            mountResult.Committed &&
            mountResult.EquipmentAfter.Attribute1 == 401,
            "Stone of Ruin resolves the level-80 mount attribute-chain anchor");
    }
}
