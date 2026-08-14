namespace Godswar.Server.Infrastructure.Pets;

internal sealed record LockedSkillBookPet(
    long PetId,
    short SpeciesId,
    short Level,
    long Experience,
    long Revision,
    bool IsSummoned,
    short OpenedSkillSlots,
    short AvailableSkillSlots,
    string? InitialSavvySourceVersion,
    byte SoulContractStage);

internal sealed record LockedSkillBookSkill(
    int SkillId,
    short SlotIndex,
    short SkillRank,
    int SkillExperience,
    bool IsActive,
    long Revision);

internal sealed record LockedSkillBookTrait(
    short StatCode,
    decimal Initial,
    decimal Added,
    decimal Growth,
    decimal Acceleration,
    decimal? Birth,
    decimal? Rarity);
