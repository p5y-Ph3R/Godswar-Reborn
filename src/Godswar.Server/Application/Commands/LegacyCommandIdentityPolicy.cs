namespace Godswar.Server.Application.Commands;

internal static class LegacyCommandIdentityPolicy
{
    public static CommandIdentityStrength GetIdentityStrength(
        CommandFamily family) =>
        family switch
        {
            CommandFamily.TalentUpgrade =>
                CommandIdentityStrength.LegacyAggregateVersion,
            CommandFamily.PetLevelUpgrade or
            CommandFamily.EquipmentForge =>
                CommandIdentityStrength.UnsupportedLegacyRetry,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
}
