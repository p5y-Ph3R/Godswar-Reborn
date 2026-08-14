using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetLearnedSkillContentChecks
{
    public const string CheckName =
        "Database-owned learned pet-skill rank curves";

    public static Task RunAsync()
    {
        var content = PetLearnedSkillContentBaseline.Create();
        Check.True(
            content.Revision.Sha256 ==
                PetLearnedSkillContentBaseline.ExpectedRevision &&
            content.Revision.SourceSha256 ==
                PetLearnedSkillContentBaseline.SourceSha256 &&
            content.Curves.Count == 384 &&
            content.Curves.Sum(static curve => curve.Steps.Count) == 1655 &&
            content.Curves.Select(static curve => curve.FamilyType)
                .Distinct().Count() == 67,
            "normalized baseline pins the reviewed 384 curves and 1,655 rank steps");

        Check.True(
            content.TryGetCurve(0, 1, out var vitalOne) &&
            vitalOne.Genre == 0 && vitalOne.Effect == 0 &&
            vitalOne.OpaqueAdd == 1 && vitalOne.OpaqueFlag == 1 &&
            vitalOne.LearnTraitRequirement.Wisdom == 10m &&
            vitalOne.Steps.Select(static step => step.MinimumPetRank)
                .SequenceEqual(new short[] { 0, 5, 11, 17, 21 }) &&
            vitalOne.Steps.Select(static step => step.AbsoluteValue)
                .SequenceEqual(new decimal[]
                    { 600m, 900m, 1125m, 1350m, 1500m }),
            "Vital Boost I retains separate fields, rank thresholds, and absolute values");

        Check.True(
            PetLearnedSkillResolver.TryResolveEffect(
                content, 0, 1, 0m, out var rankZero) &&
            rankZero.RuntimeSkillId == 400 &&
            rankZero.AbsoluteValue == 600m &&
            PetLearnedSkillResolver.TryResolveEffect(
                content, 0, 1, 5m, out var rankFive) &&
            rankFive.RuntimeSkillId == 401 &&
            rankFive.AbsoluteValue == 900m &&
            PetLearnedSkillResolver.TryResolveEffect(
                content, 0, 1, 21m, out var rankTwentyOne) &&
            rankTwentyOne.RuntimeSkillId == 404 &&
            rankTwentyOne.AbsoluteValue == 1500m,
            "learned tier always has its rank-zero effect and selects the highest reached absolute step");

        var enoughWisdom = new PetSavvy(0m, 0m, 0m, 0m, 48m, 0m);
        Check.True(
            !PetLearnedSkillResolver.CanLearn(
                content, 0, 2, 0, enoughWisdom, out var skipped) &&
            skipped == PetSkillLearnRejection.PriorTierRequired &&
            !PetLearnedSkillResolver.CanLearn(
                content, 0, 2, 1,
                enoughWisdom with { Wisdom = 47.99m },
                out var insufficient) &&
            insufficient == PetSkillLearnRejection.TraitRequirementNotMet &&
            PetLearnedSkillResolver.CanLearn(
                content, 0, 2, 1, enoughWisdom, out var accepted) &&
            accepted == PetSkillLearnRejection.None,
            "learning requires the immediately prior tier and the one-time Trait threshold");

        Check.True(
            PetLearnedSkillResolver.TryResolveEffect(
                content, 0, 2, 0m, out var afterRedistribution) &&
            afterRedistribution.AbsoluteValue == 1500m,
            "effect resolution does not re-check Trait after Fairy redistribution");

        Check.True(
            content.TryGetCurveByRuntimeSkillId(3004, out var anomaly) &&
            anomaly.FamilyType == 401 &&
            anomaly.Steps[^1].AbsoluteValue == 60m &&
            content.TryGetCurveByRuntimeSkillId(552, out var factAnomaly) &&
            factAnomaly.Steps.Single(static step =>
                step.RuntimeSkillId == 552).AbsoluteValue == 926m &&
            content.TryGetCurveByRuntimeSkillId(6023, out var fullWidthComma) &&
            fullWidthComma.Steps[^1].AbsoluteValue == 1820m,
            "reviewed source anomalies are normalized deterministically");

        var invalidRank = content.Curves.Select(curve =>
            curve.FamilyType == 0 && curve.Priority == 1
                ? curve with
                {
                    Steps = curve.Steps.Select(step =>
                        step.StepOrder == 1
                            ? step with { MinimumPetRank = 656 }
                            : step).ToArray()
                }
                : curve).ToArray();
        Check.Throws<InvalidDataException>(
            () => PinnedPetLearnedSkillContentCatalog.Create(
                content.Revision.Source,
                content.Revision.SourceSha256,
                invalidRank),
            "catalog rejects rank thresholds outside the PostgreSQL boundary");
        return Task.CompletedTask;
    }
}
