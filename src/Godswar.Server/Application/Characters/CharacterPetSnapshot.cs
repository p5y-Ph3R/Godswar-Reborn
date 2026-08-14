using System.Collections.Immutable;

namespace Godswar.Server.Application.Characters;

internal sealed record CharacterPetSnapshot(
    long PetId,
    int AccountId,
    int OwnerCharacterId,
    short SpeciesId,
    string Name,
    byte Sex,
    short Level,
    long Experience,
    short Aptitude,
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
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ImmutableArray<CharacterPetStatValueSnapshot> StatValues,
    ImmutableArray<CharacterPetBonusSnapshot> CharacterBonuses,
    ImmutableArray<CharacterPetSkillSnapshot> Skills,
    short OpenedSkillSlots = 1,
    short AvailableSkillSlots = 1,
    short TalentMask = 0,
    string? InitialSavvySourceVersion = null,
    byte SoulContractStage = 0)
{
    public byte ProjectedSoulContractStage =>
        SoulContractStage == 0 && HasSoulContract
            ? (byte)1
            : SoulContractStage;
}

internal sealed record CharacterPetStatValueSnapshot(
    short StatCode,
    decimal InitialSavvy,
    decimal AddedSavvy,
    decimal BaseGrowthRate,
    decimal GrowthAcceleration,
    long Revision,
    decimal? BirthInitialSavvy,
    decimal? RarityAddedSavvy);

internal sealed record CharacterPetBonusSnapshot(
    short EffectCode,
    decimal EffectValue,
    long Revision);
internal sealed record CharacterPetSkillSnapshot(
    int SkillId,
    short SlotIndex,
    short SkillRank,
    int SkillExperience,
    bool IsActive,
    long Revision);
