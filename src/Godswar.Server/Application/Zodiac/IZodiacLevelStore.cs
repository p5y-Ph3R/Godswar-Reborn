using Godswar.Server.Application.Characters;

namespace Godswar.Server.Application.Zodiac;

/// <summary>
/// Applies one authoritative Zodiac-level upgrade for the currently owned
/// character. Implementations must hold the player-ownership fence for the
/// complete valuable mutation and revalidate it after the transaction ends.
/// </summary>
internal interface IZodiacLevelStore
{
    Task<ZodiacLevelUpgradeStoreResult?> UpgradeAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default);
}

internal enum ZodiacLevelUpgradeStoreStatus : byte
{
    Succeeded = 0,
    CharacterLevelTooLow = 1,
    InsufficientEnergy = 2,
    MaximumLevelReached = 3
}

internal sealed record ZodiacLevelUpgradeStoreResult(
    ZodiacLevelUpgradeStoreStatus Status,
    byte PreviousLevel,
    byte CurrentLevel,
    int RequiredCharacterLevel,
    int EnergyCost,
    int CurrentEnergy,
    int CurrentEnergyRemainderX100)
{
    public bool Committed =>
        Status == ZodiacLevelUpgradeStoreStatus.Succeeded;

    public void Validate()
    {
        if (!Enum.IsDefined(Status) ||
            PreviousLevel is < 1 or > 30 ||
            CurrentLevel is < 1 or > 30 ||
            CurrentEnergy < 0 ||
            CurrentEnergyRemainderX100 is < 0 or > 99)
        {
            throw new InvalidDataException(
                "Zodiac-level upgrade result is outside its bounded contract.");
        }

        var validTransition = Status switch
        {
            ZodiacLevelUpgradeStoreStatus.Succeeded =>
                PreviousLevel < 30 &&
                CurrentLevel == PreviousLevel + 1 &&
                RequiredCharacterLevel > 0 &&
                EnergyCost > 0,
            ZodiacLevelUpgradeStoreStatus.CharacterLevelTooLow or
                ZodiacLevelUpgradeStoreStatus.InsufficientEnergy =>
                CurrentLevel == PreviousLevel &&
                RequiredCharacterLevel > 0 &&
                EnergyCost > 0,
            ZodiacLevelUpgradeStoreStatus.MaximumLevelReached =>
                PreviousLevel == 30 &&
                CurrentLevel == 30 &&
                RequiredCharacterLevel == 0 &&
                EnergyCost == 0,
            _ => false
        };
        if (!validTransition)
        {
            throw new InvalidDataException(
                "Zodiac-level upgrade result has inconsistent transition evidence.");
        }
    }
}
