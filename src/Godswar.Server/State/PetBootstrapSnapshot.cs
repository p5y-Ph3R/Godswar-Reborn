namespace Godswar.Server.State;

/// <summary>
/// A complete, read-only projection of one persisted pet for client bootstrap.
/// It deliberately retains the relational child rows so the protocol layer can
/// encode the verified legacy layout without querying persistence directly.
/// </summary>
internal sealed record PetBootstrapSnapshot(
    long PetId,
    int AccountId,
    int OwnerCharacterId,
    short SpeciesId,
    string Name,
    byte Sex,
    short Level,
    long Experience,
    PetAptitude Aptitude,
    decimal Rank,
    short CompletedRebirths,
    short RebirthsRemaining,
    int CompletedPetMerges,
    bool HasSoulContract,
    bool HasOwnerMergeTalent,
    int CurrentEnergy,
    int MaximumEnergy,
    int Amity,
    int Satiety,
    int RemainingLifetime,
    int AvailableStatPoints,
    bool GrowthRevealed,
    bool IsBound,
    string ActivityState,
    bool IsCarried,
    bool IsSummoned,
    bool ContributesToCharacter,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PetStatValueSnapshot> StatValues,
    IReadOnlyList<PetCharacterBonusSnapshot> CharacterBonuses,
    IReadOnlyList<PetSkillSnapshot> Skills);

internal sealed record PetStatValueSnapshot(
    short StatCode,
    decimal InitialSavvy,
    decimal AddedSavvy,
    decimal BaseGrowthRate,
    decimal GrowthAcceleration,
    long Revision,
    decimal? BirthInitialSavvy = null,
    decimal? RarityAddedSavvy = null);

internal sealed record PetCharacterBonusSnapshot(
    short EffectCode,
    decimal EffectValue,
    long Revision);

internal sealed record PetSkillSnapshot(
    int SkillId,
    short SlotIndex,
    short SkillRank,
    int SkillExperience,
    bool IsActive,
    long Revision);
