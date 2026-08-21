using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private static async Task CheckZodiacOffensiveRuntimeAsync()
    {
        await CheckZodiacScalarRuntimeAsync();
        await CheckZodiacAreaRuntimeAsync();
    }

    private static async Task CheckZodiacScalarRuntimeAsync()
    {
        var skill = PublishedSkill(10);
        var now = DateTimeOffset.Parse("2026-08-20T08:00:00Z");
        var baselineAttacker = Player(
            8_801,
            8_801,
            "ZodiacScalar",
            map: 0,
            camp: 0,
            profession: 0);
        var projectedAttacker = Player(
            8_801,
            8_801,
            "ZodiacScalar",
            map: 0,
            camp: 0,
            profession: 0);
        projectedAttacker.ZodiacSkillGridLevels[4] = 1;
        projectedAttacker.ZodiacSkillGridSkillIds[4] = 20_001;

        await using var baseline = await Fixture.CreateAsync(
            attacker: baselineAttacker);
        await using var projected = await Fixture.CreateAsync(
            attacker: projectedAttacker);
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
        var expected = ZodiacOffensiveSkillProjection.Resolve(
            projected.Attacker,
            skill);

        Check.True(
            baselineDecision.Accepted &&
            projectedDecision.Accepted &&
            baselineDecision.Combat.Resolution.Outcome ==
                projectedDecision.Combat.Resolution.Outcome &&
            projectedDecision.Combat.Resolution.Damage >
                baselineDecision.Combat.Resolution.Damage &&
            baseline.Attacker.CurrentMp ==
                baseline.Attacker.MaxMp - skill.Mp &&
            projected.Attacker.CurrentMp ==
                projected.Attacker.MaxMp - expected.Skill.Mp &&
            expected.Skill.Mp ==
                skill.Mp +
                ZodiacOffensiveSkillProjection
                    .ResolveRoundedUpAdditionalMana(skill.Mp, 5),
            "dummy scalar validates the authored definition, then commits Type-2 damage and rounded MP");
    }

    private static async Task CheckZodiacAreaRuntimeAsync()
    {
        var skill = PublishedSkill(30);
        var now = DateTimeOffset.Parse("2026-08-20T08:05:00Z");
        await using var baseline = await AreaFixture.CreateAsync(
            attackerProfession: 0);
        await using var projected = await AreaFixture.CreateAsync(
            attackerProfession: 0);
        projected.Attacker.ZodiacSkillGridLevels[0] = 1;
        projected.Attacker.ZodiacSkillGridSkillIds[0] = 10_003;

        var revision = FindAreaHittingRevision(baseline, skill);
        var baselineDecision = await baseline.ResolveAsync(
            skill,
            () => revision,
            now);
        var projectedDecision = await projected.ResolveAsync(
            skill,
            () => revision,
            now);
        var expected = ZodiacOffensiveSkillProjection.Resolve(
            projected.Attacker,
            skill);

        Check.True(
            baselineDecision.Accepted &&
            projectedDecision.Accepted &&
            baselineDecision.Combats.Count ==
                projectedDecision.Combats.Count &&
            baselineDecision.Combats.Zip(projectedDecision.Combats)
                .All(pair =>
                    pair.First.Resolution.Outcome ==
                        pair.Second.Resolution.Outcome &&
                    pair.Second.Resolution.Damage >
                        pair.First.Resolution.Damage) &&
            baseline.Attacker.CurrentMp ==
                baseline.Attacker.MaxMp - skill.Mp &&
            projected.Attacker.CurrentMp ==
                projected.Attacker.MaxMp - expected.Skill.Mp &&
            expected.Skill.Mp == skill.Mp + 2 &&
            expected.Skill.Power2 == skill.Power2 + 100m,
            "dummy area validates the authored definition, then commits Type-1 power and fixed MP once");
    }
}
