namespace Godswar.Server.State;

internal enum PetLevelUpgradeStatus
{
    Succeeded,
    CharacterNotFound,
    PetNotFound,
    PetUnavailable,
    MaximumLevel,
    InsufficientExperience
}

internal sealed record PetLevelUpgradeResult(
    PetLevelUpgradeStatus Status,
    long PetId,
    short PreviousLevel,
    short Level,
    long PreviousExperience,
    long Experience,
    int ExperienceSpent,
    long Revision,
    PetSavvy BasicSavvy = default)
{
    public bool Succeeded => Status == PetLevelUpgradeStatus.Succeeded;

    public static PetLevelUpgradeResult Rejected(
        PetLevelUpgradeStatus status,
        long petId,
        short level = 0,
        long experience = 0,
        long revision = 0)
    {
        if (status == PetLevelUpgradeStatus.Succeeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "A rejected pet level-up cannot use the succeeded status.");
        }

        return new(
            status,
            petId,
            level,
            level,
            experience,
            experience,
            ExperienceSpent: 0,
            revision);
    }
}
