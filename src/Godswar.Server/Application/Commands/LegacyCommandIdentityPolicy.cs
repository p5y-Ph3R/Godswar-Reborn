namespace Godswar.Server.Application.Commands;

internal static class LegacyCommandIdentityPolicy
{
    public static CommandIdentityStrength GetIdentityStrength(
        CommandFamily family) =>
        family switch
        {
            CommandFamily.TalentUpgrade =>
                CommandIdentityStrength.LegacyAggregateVersion,
            CommandFamily.DeveloperItemGrant or
            CommandFamily.DeveloperBagClear or
            CommandFamily.GearMentorMakeAttributeStone or
            CommandFamily.GearMentorTransformCrystal or
            CommandFamily.GearMentorCombineGemPieces or
            CommandFamily.GearMentorDecomposeGear or
            CommandFamily.GearMentorEnhanceAttribute or
            CommandFamily.GearMentorAddAttribute or
            CommandFamily.GearMentorDeleteAttribute =>
                CommandIdentityStrength.ClientOperationId,
            CommandFamily.PetLevelUpgrade or
            CommandFamily.EquipmentForge =>
                CommandIdentityStrength.UnsupportedLegacyRetry,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
}
