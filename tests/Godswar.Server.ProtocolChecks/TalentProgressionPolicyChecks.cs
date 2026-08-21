using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Talents;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class TalentProgressionPolicyChecks
{
    public const string CheckName =
        "All-class talent rank-100 progression policy";

    private static readonly short[] ExpectedProfessionIds = [0, 1, 2, 3];

    public static Task RunAsync()
    {
        Check.Equal(
            100,
            TalentProgression.RankCap,
            "talent rank cap remains 100");
        Check.Equal(
            140,
            TalentProgression.CalculateRequiredPlayerLevel(59),
            "rank 60 requires character level 140");
        Check.Equal(
            141,
            TalentProgression.CalculateRequiredPlayerLevel(60),
            "rank 61 continues the level curve without an artificial jump");
        Check.Equal(
            150,
            TalentProgression.CalculateRequiredPlayerLevel(79),
            "rank 80 remains reachable before the final level gate");
        Check.Equal(
            160,
            TalentProgression.CalculateRequiredPlayerLevel(99),
            "rank 100 is reachable at character level 160");
        Check.Equal(
            0,
            TalentProgression.CalculateUpgradeCost(100),
            "rank 100 remains terminal");
        var effectiveRankMilestones = new Dictionary<int, int>
        {
            [40] = 40,
            [60] = 80,
            [80] = 140,
            [90] = 190,
            [100] = 260
        };
        foreach (var milestone in effectiveRankMilestones)
        {
            Check.Equal(
                milestone.Value,
                TalentProgression.CalculateEffectiveRankValue(milestone.Key),
                $"talent rank {milestone.Key} has the reviewed effective rank");
        }

        var spearplay = SkillTalentSeeds.Talents.Single(
            static talent => talent.Id == 55 && talent.ClassId == 1);
        Check.Equal(
            13_000m,
            spearplay.EffectValue *
            TalentProgression.CalculateEffectiveRankValue(100) * 10_000m,
            "Archaian Spearplay rank 100 contributes exactly 13000bp");

        foreach (var professionId in ExpectedProfessionIds)
        {
            var classDefinition = SkillTalentSeeds.Classes.SingleOrDefault(
                candidate => candidate.Id == professionId);
            Check.True(
                !string.IsNullOrWhiteSpace(classDefinition.Name),
                $"profession {professionId} has a class definition");
            Check.True(
                SkillTalentSeeds.Talents.Any(
                    talent => talent.ClassId == professionId),
                $"profession {professionId} has authoritative talent definitions");

            var finalRank = CharacterTalentProjection.FromPersistedRank(
                SkillTalentSeeds.Talents.First(
                    talent => talent.ClassId == professionId).Id,
                TalentProgression.RankCap);
            Check.Equal(
                TalentProgression.RankCap,
                finalRank.Rank,
                $"profession {professionId} projects rank 100");
            Check.Equal(
                0,
                finalRank.NextCost,
                $"profession {professionId} exposes no upgrade beyond rank 100");
        }

        return Task.CompletedTask;
    }
}
