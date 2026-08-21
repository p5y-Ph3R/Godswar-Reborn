using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private static async Task CheckZodiacDefensiveRuntimeAsync()
    {
        await CheckZodiacDefensiveScalarRuntimeAsync();
        await CheckZodiacDefensiveAreaRuntimeAsync();
        await CheckAresBulwarkMitigationOrderAsync();
    }

    private static async Task CheckZodiacDefensiveScalarRuntimeAsync()
    {
        var skill = PublishedSkill(10);
        var now = DateTimeOffset.Parse("2026-08-20T08:10:00Z");
        var baselineAttacker = Player(
            8_811,
            8_811,
            "ZodiacDefenseScalar",
            map: 0,
            camp: 0,
            profession: 0);
        var projectedAttacker = Player(
            8_811,
            8_811,
            "ZodiacDefenseScalar",
            map: 0,
            camp: 0,
            profession: 0);
        var baselineTarget = Dummy();
        var projectedTarget = Dummy();
        projectedTarget.ZodiacSkillGridLevels[
            ZodiacDefensiveSkillProjection.PercentageTrainingFirstGrid] = 10;
        projectedTarget.ZodiacSkillGridSkillIds[
            ZodiacDefensiveSkillProjection.PercentageTrainingFirstGrid] =
                20_001;

        await using var baseline = await Fixture.CreateAsync(
            baselineAttacker,
            baselineTarget);
        await using var projected = await Fixture.CreateAsync(
            projectedAttacker,
            projectedTarget);
        var revision = FindHittingRevision(
            baseline.Attacker,
            baseline.Target,
            skill);
        var baselineDecision = await baseline.ResolveAsync(
            revision,
            now,
            skill);
        var projectedDecision = await projected.ResolveAsync(
            revision,
            now,
            skill);
        var expectedDamage = (uint)(
            (decimal)baselineDecision.Combat.Resolution.Damage *
            8_000m /
            10_000m);

        Check.True(
            baselineDecision.Accepted &&
            projectedDecision.Accepted &&
            baselineDecision.Combat.Resolution.Outcome ==
                projectedDecision.Combat.Resolution.Outcome &&
            projectedDecision.Combat.Resolution.Damage == expectedDamage &&
            projectedDecision.Combat.Resolution.Damage <
                baselineDecision.Combat.Resolution.Damage &&
            projectedDecision.Combat.Resolution.Evidence ==
                baselineDecision.Combat.Resolution.Evidence &&
            baseline.Attacker.CurrentMp ==
                baseline.Attacker.MaxMp - skill.Mp &&
            projected.Attacker.CurrentMp ==
                projected.Attacker.MaxMp - skill.Mp,
            "dummy scalar commits matching Improve DEF after resolution without changing attacker MP");
    }

    private static async Task CheckZodiacDefensiveAreaRuntimeAsync()
    {
        var skill = PublishedSkill(30);
        var now = DateTimeOffset.Parse("2026-08-20T08:15:00Z");
        await using var baseline = await AreaFixture.CreateAsync(
            attackerProfession: 0);
        await using var projected = await AreaFixture.CreateAsync(
            attackerProfession: 0);
        foreach (var dummy in projected.Dummies)
        {
            dummy.ZodiacSkillGridLevels[
                ZodiacDefensiveSkillProjection.FlatTrainingFirstGrid] = 1;
            dummy.ZodiacSkillGridSkillIds[
                ZodiacDefensiveSkillProjection.FlatTrainingFirstGrid] =
                    10_003;
        }

        var revision = FindAreaHittingRevision(baseline, skill);
        var baselineDecision = await baseline.ResolveAsync(
            skill,
            () => revision,
            now);
        var projectedDecision = await projected.ResolveAsync(
            skill,
            () => revision,
            now);

        Check.True(
            baselineDecision.Accepted &&
            projectedDecision.Accepted &&
            baselineDecision.Combats.Count == projectedDecision.Combats.Count &&
            baselineDecision.Combats.Zip(projectedDecision.Combats)
                .All(pair =>
                    pair.First.Resolution.Outcome ==
                        pair.Second.Resolution.Outcome &&
                    pair.Second.Resolution.Evidence ==
                        pair.First.Resolution.Evidence &&
                    pair.Second.Resolution.Damage ==
                        (pair.First.Resolution.Damage > 100u
                            ? pair.First.Resolution.Damage - 100u
                            : 0u)) &&
            baseline.Attacker.CurrentMp ==
                baseline.Attacker.MaxMp - skill.Mp &&
            projected.Attacker.CurrentMp ==
                projected.Attacker.MaxMp - skill.Mp,
            "dummy area applies matching DEF Skill Training to every target exactly once");
    }
}
