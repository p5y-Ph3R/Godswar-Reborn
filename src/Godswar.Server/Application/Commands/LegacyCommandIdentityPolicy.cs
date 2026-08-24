namespace Godswar.Server.Application.Commands;

internal static class LegacyCommandIdentityPolicy
{
    public static CommandIdentityStrength GetIdentityStrength(
        CommandFamily family) =>
        family switch
        {
            CommandFamily.TalentUpgrade or
            CommandFamily.ZodiacSkillGridActivation =>
                CommandIdentityStrength.LegacyAggregateVersion,
            CommandFamily.DeveloperItemGrant or
            CommandFamily.DeveloperBagClear or
            CommandFamily.EquipmentForge or
            CommandFamily.GearMentorMakeAttributeStone or
            CommandFamily.GearMentorTransformCrystal or
            CommandFamily.GearMentorCombineGemPieces or
            CommandFamily.GearMentorDecomposeGear or
            CommandFamily.GearMentorEnhanceAttribute or
            CommandFamily.GearMentorAddAttribute or
            CommandFamily.GearMentorDeleteAttribute or
            CommandFamily.KitBagItemDelete or
            CommandFamily.KitBagItemMove or
            CommandFamily.EquipmentBagTransfer or
            CommandFamily.HolyStoneMount or
            CommandFamily.HolyStoneRemove or
            CommandFamily.HolyStoneDrill or
            CommandFamily.HolyStoneAdvancedDrill or
            CommandFamily.HolyStoneUpgrade or
            CommandFamily.HolyStoneCombine or
            CommandFamily.HolyStoneImplementSpirit or
            CommandFamily.MountGearDrill or
            CommandFamily.HolySuitStoreExperience or
            CommandFamily.HolySuitTransferExperience or
            CommandFamily.HolySuitConsumeWare or
            CommandFamily.HolySuitTransformExperience or
            CommandFamily.ClassSuitExchangeTierI or
            CommandFamily.ClassSuitConvertToCommon or
            CommandFamily.ClassSuitUpgradeTierII or
            CommandFamily.ClassSuitUpgradeTierIII or
            CommandFamily.ClassSuitUpgradeTierIV or
            CommandFamily.ClassSuitAddAttribute or
            CommandFamily.ClassSuitDeleteAttribute or
            CommandFamily.ZodiacSkillGridUpgrade or
            CommandFamily.CharacterCreate or
            CommandFamily.CharacterDelete or
            CommandFamily.CharacterRestore or
            CommandFamily.CharacterPurge or
            CommandFamily.BagItemActivation or
            CommandFamily.PetPresenceTransition or
            CommandFamily.PetLevelUpgrade or
            CommandFamily.PetSkillUnlearn or
            CommandFamily.PetGrowthReset or
            CommandFamily.PetBasicSavvyReset or
            CommandFamily.PetOwnerMergeToggle or
            CommandFamily.PetToPetMerge or
            CommandFamily.PetRebirth or
            CommandFamily.PetAppearanceChange or
            CommandFamily.PetBind or
            CommandFamily.PetSoulContract or
            CommandFamily.WarehouseTransfer or
            CommandFamily.WarehouseExpansion =>
                CommandIdentityStrength.ClientOperationId,
            CommandFamily.ZodiacSkillGridSelection =>
                CommandIdentityStrength.ClientOperationId,
            CommandFamily.MonsterRewardSettlement or
            CommandFamily.ProgressionIntervalSettlement =>
                CommandIdentityStrength.ServerOperationId,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
}
