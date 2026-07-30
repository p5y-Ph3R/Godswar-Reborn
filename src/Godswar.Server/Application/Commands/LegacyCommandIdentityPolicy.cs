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
            CommandFamily.ZodiacSkillGridUpgrade =>
                CommandIdentityStrength.ClientOperationId,
            CommandFamily.PetLevelUpgrade =>
                CommandIdentityStrength.UnsupportedLegacyRetry,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
}
