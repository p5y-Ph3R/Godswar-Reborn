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
    PetSavvy InitialSavvy,
    PetAddedSavvyRoll? AddedSavvy,
    PetGrowthRoll? Growth)
{
    public bool Succeeded =>
        Status == PetEggHatchStatus.Succeeded &&
        Character is not null &&
        PetId > 0 &&
        InitialSavvy.IsNonNegative &&
        AddedSavvy is not null &&
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
            InitialSavvy: PetSavvy.Zero,
            AddedSavvy: null,
            Growth: null);
}
