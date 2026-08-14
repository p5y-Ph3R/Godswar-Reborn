using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetLearnedSkillOwnerStatProjectionChecks
{
    public const string CheckName =
        "Carried-pet learned-skill owner-stat projection";

    public static Task RunAsync()
    {
        var content = PetLearnedSkillContentBaseline.Create();
        AssertEffect(content, 408, 6, 5.59m, 19, 0.035m);
        AssertEffect(content, 408, 6, 100m, 19, 0.041m);
        AssertEffect(content, 412, 6, 100m, 4, 461m);
        AssertEffect(content, 413, 6, 5.59m, 2, 108m);
        AssertEffect(content, 413, 6, 100m, 2, 119m);
        AssertEffect(content, 419, 6, 100m, 21, 0.080m);
        AssertEffect(content, 423, 6, 100m, 0, 12_500m);

        var cte = PostgresCharacterPetLearnedSkillProjectionSql
            .CommonTableExpression;
        var stats = PostgresCharacterRuntimeItemProjectionSql
            .CalculatedStatsForCharacter;
        Check.True(
            cte.Contains("pet.is_carried", StringComparison.Ordinal) &&
            cte.Contains("pet.activity_state = 'owned'",
                StringComparison.Ordinal) &&
            cte.Contains("skill.is_active", StringComparison.Ordinal) &&
            !cte.Contains("pet.is_summoned", StringComparison.Ordinal) &&
            !cte.Contains("pet.contributes_to_character",
                StringComparison.Ordinal),
            "only the selected carried pet is the passive source; Recall and owner Merge do not remove or duplicate it");
        Check.True(
            cte.Contains("@petLearnedSkillRevision",
                StringComparison.Ordinal) &&
            cte.Contains("candidate.minimum_pet_rank::numeric <= pet.rank",
                StringComparison.Ordinal) &&
            cte.Contains("ORDER BY candidate.minimum_pet_rank DESC",
                StringComparison.Ordinal) &&
            cte.Contains("curve.family_type IN (408, 412, 413, 419, 423)",
                StringComparison.Ordinal),
            "SQL uses the pinned revision, authoritative rank, and reviewed family allow-list");
        Check.True(
            cte.Contains("WHEN 19 THEN step.absolute_value * 10000",
                StringComparison.Ordinal) &&
            cte.Contains("WHEN 21 THEN step.absolute_value * 10000",
                StringComparison.Ordinal) &&
            cte.Contains("WHEN 2 THEN 'hit'",
                StringComparison.Ordinal) &&
            stats.Contains("pet_learned_skill_stat_values",
                StringComparison.Ordinal),
            "fractional stock effects use character basis points and feed calculated stats");
        return Task.CompletedTask;
    }

    private static void AssertEffect(
        Application.Pets.IPetLearnedSkillContentCatalog content,
        int family,
        int priority,
        decimal rank,
        int expectedEffect,
        decimal expectedValue)
    {
        Check.True(
            PetLearnedSkillResolver.TryResolveEffect(
                content,
                family,
                priority,
                rank,
                out var effect) &&
            effect.Effect == expectedEffect &&
            effect.AbsoluteValue == expectedValue,
            $"family {family} tier {priority} resolves its rank-{rank} absolute effect");
    }
}
