namespace Godswar.Server.Application.Pets;

internal readonly record struct PetSkillTraitRequirement(
    decimal Agility,
    decimal Strength,
    decimal Accuracy,
    decimal Technique,
    decimal Wisdom,
    decimal Luck)
{
    public bool IsValid =>
        Agility >= 0m && Strength >= 0m && Accuracy >= 0m &&
        Technique >= 0m && Wisdom >= 0m && Luck >= 0m &&
        new[] { Agility, Strength, Accuracy, Technique, Wisdom, Luck }
            .Count(static value => value > 0m) <= 1;
}

internal sealed record PetLearnedSkillStepContentDefinition(
    short StepOrder,
    int RuntimeSkillId,
    short MinimumPetRank,
    decimal AbsoluteValue);

internal sealed record PetLearnedSkillCurveContentDefinition(
    int FamilyType,
    short Priority,
    int Genre,
    int Effect,
    int OpaqueAdd,
    int OpaqueFlag,
    PetSkillTraitRequirement LearnTraitRequirement,
    int FirstRuntimeSkillId,
    IReadOnlyList<PetLearnedSkillStepContentDefinition> Steps);

internal sealed record PetLearnedSkillContentRevision(
    string Sha256,
    int CurveCount,
    int StepCount,
    string Source,
    string SourceSha256);

internal interface IPetLearnedSkillContentCatalog
{
    PetLearnedSkillContentRevision Revision { get; }

    IReadOnlyList<PetLearnedSkillCurveContentDefinition> Curves { get; }

    bool TryGetCurve(
        int familyType,
        int priority,
        out PetLearnedSkillCurveContentDefinition definition);

    bool TryGetCurveByRuntimeSkillId(
        int runtimeSkillId,
        out PetLearnedSkillCurveContentDefinition definition);
}
