using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

internal enum PetSkillLearnRejection
{
    None,
    UnknownCurve,
    AlreadyLearned,
    PriorTierRequired,
    TraitRequirementNotMet
}

internal sealed record PetLearnedSkillEffect(
    int FamilyType,
    short LearnedPriority,
    int RuntimeSkillId,
    short MinimumPetRank,
    int Genre,
    int Effect,
    decimal AbsoluteValue);

internal static class PetLearnedSkillResolver
{
    public static bool CanLearn(
        IPetLearnedSkillContentCatalog content,
        int familyType,
        int targetPriority,
        int currentlyLearnedPriority,
        PetSavvy traitsAtLearnTime,
        out PetSkillLearnRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.TryGetCurve(familyType, targetPriority, out var curve))
        {
            rejection = PetSkillLearnRejection.UnknownCurve;
            return false;
        }
        if (currentlyLearnedPriority >= targetPriority)
        {
            rejection = PetSkillLearnRejection.AlreadyLearned;
            return false;
        }
        if (targetPriority != currentlyLearnedPriority + 1)
        {
            rejection = PetSkillLearnRejection.PriorTierRequired;
            return false;
        }
        if (!Meets(traitsAtLearnTime, curve.LearnTraitRequirement))
        {
            rejection = PetSkillLearnRejection.TraitRequirementNotMet;
            return false;
        }
        rejection = PetSkillLearnRejection.None;
        return true;
    }

    public static bool TryResolveEffect(
        IPetLearnedSkillContentCatalog content,
        int familyType,
        int learnedPriority,
        decimal currentPetRank,
        out PetLearnedSkillEffect effect)
    {
        ArgumentNullException.ThrowIfNull(content);
        effect = null!;
        if (!PetRankWirePolicy.IsRepresentable(currentPetRank) ||
            !content.TryGetCurve(
                familyType,
                learnedPriority,
                out var curve))
        {
            return false;
        }
        var step = curve.Steps[0];
        foreach (var candidate in curve.Steps)
        {
            if (candidate.MinimumPetRank > currentPetRank)
            {
                break;
            }
            step = candidate;
        }
        effect = new(
            curve.FamilyType,
            curve.Priority,
            step.RuntimeSkillId,
            step.MinimumPetRank,
            curve.Genre,
            curve.Effect,
            step.AbsoluteValue);
        return true;
    }

    private static bool Meets(
        PetSavvy actual,
        PetSkillTraitRequirement required) =>
        actual.Agility >= required.Agility &&
        actual.Strength >= required.Strength &&
        actual.Accuracy >= required.Accuracy &&
        actual.Technique >= required.Technique &&
        actual.Wisdom >= required.Wisdom &&
        actual.Luck >= required.Luck;
}
