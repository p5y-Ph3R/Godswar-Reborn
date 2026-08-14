using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

internal enum PetEggHatchStatus
{
    Succeeded,
    CharacterNotFound,
    InvalidBagSlot,
    ItemNotFound,
    NotPetEgg,
    InvalidEggStack,
    InvalidEggRarity,
    UnsupportedEggRarity,
    PetCapacityReached
}

internal sealed record PetEggHatchResult(
    PetEggHatchStatus Status,
    GameCharacter? Character,
    long PetId,
    int SpeciesType,
    PetAptitude Aptitude,
    PetHatchRankRoll? HatchRank,
    string? HatchRankContentRevision,
    PetSavvy InitialSavvy,
    PetInitialSavvyRoll? InitialSavvyRoll,
    PetGrowthRoll? Growth)
{
    public bool Succeeded =>
        Status == PetEggHatchStatus.Succeeded &&
        Character is not null &&
        PetId > 0 &&
        HatchRank is not null &&
        !string.IsNullOrWhiteSpace(HatchRankContentRevision) &&
        InitialSavvy.IsNonNegative &&
        InitialSavvyRoll is not null &&
        Growth is not null;

    public static PetEggHatchResult Rejected(
        PetEggHatchStatus status,
        GameCharacter? character = null) =>
        new(
            status,
            character,
            PetId: 0,
            SpeciesType: 0,
            Aptitude: default,
            HatchRank: null,
            HatchRankContentRevision: null,
            InitialSavvy: PetSavvy.Zero,
            InitialSavvyRoll: null,
            Growth: null);
}
