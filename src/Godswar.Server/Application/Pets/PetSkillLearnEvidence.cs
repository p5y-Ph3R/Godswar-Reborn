namespace Godswar.Server.Application.Pets;

internal sealed record PetSkillLearnEvidence(
    long PetId,
    long ItemInstanceId,
    uint ItemTemplateId,
    short SpeciesId,
    int FamilyType,
    short PreviousPriority,
    short LearnedPriority,
    int PreviousRuntimeSkillId,
    int LearnedRuntimeSkillId,
    short SkillSlot,
    PetSkillTraitRequirement TraitRequirement,
    PetContentStatVector TraitsAtLearnTime,
    string ItemContentRevision,
    string LearnedSkillContentRevision)
{
    public bool IsValid =>
        PetId > 0 &&
        ItemInstanceId > 0 &&
        ItemTemplateId > 0 &&
        SpeciesId > 0 &&
        FamilyType >= 0 &&
        LearnedPriority is >= 1 and <= 6 &&
        PreviousPriority == LearnedPriority - 1 &&
        ((PreviousPriority == 0 && PreviousRuntimeSkillId == 0) ||
         (PreviousPriority > 0 && PreviousRuntimeSkillId > 0)) &&
        LearnedRuntimeSkillId > 0 &&
        SkillSlot is >= 0 and < 12 &&
        TraitRequirement.IsValid &&
        TraitsAtLearnTime.IsNonNegative &&
        IsDigest(ItemContentRevision) &&
        IsDigest(LearnedSkillContentRevision);

    private static bool IsDigest(string value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
