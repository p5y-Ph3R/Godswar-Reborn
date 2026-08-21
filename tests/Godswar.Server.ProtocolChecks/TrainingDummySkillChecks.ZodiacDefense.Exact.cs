using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private const int CooledTypedReductionBasisPoints = 3_850;
    private const int TechniqueReductionBasisPoints = 3_000;
    private const int DoubledTechniquePhysicalCancellation = 122_058;

    private static async Task CheckAresBulwarkMitigationOrderAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-21T02:00:00Z");
        var baselineAttacker = ExactSpearHitAttacker();
        var selectedAttacker = ExactSpearHitAttacker();
        var baselineTarget = ExactAresBulwark();
        var selectedTarget = ExactAresBulwark();
        SelectAresBulwarkSpearHitDefense(selectedTarget);
        var selectedAdjustment =
            ZodiacDefensiveSkillProjection.ResolveAdjustment(
                selectedTarget,
                runtimeSkillId: 294);
        Check.True(
            selectedAdjustment.FlatDamageReduction == 0 &&
            selectedAdjustment.DamageReductionBasisPoints == 2_000,
            "AresBulwark selects the matching Spear Hit defense row " +
            $"(flat={selectedAdjustment.FlatDamageReduction}, " +
            $"percent={selectedAdjustment.DamageReductionBasisPoints})");
        SetElementalProfile(
            baselineTarget,
            ElementalProfile((ElementKind.Earth, 10)));
        SetElementalProfile(
            selectedTarget,
            ElementalProfile((ElementKind.Earth, 10)));

        await using var baseline = await Fixture.CreateAsync(
            baselineAttacker,
            baselineTarget,
            bindElementalOwnership: true);
        await using var selected = await Fixture.CreateAsync(
            selectedAttacker,
            selectedTarget,
            bindElementalOwnership: true);
        var revision = FindExactCriticalRevision(
            baseline.Attacker,
            baseline.Target);
        var baselineDecision = await baseline.ResolveAsync(
            revision,
            now,
            SpearHit());
        var selectedDecision = await selected.ResolveAsync(
            revision,
            now,
            SpearHit());

        var baselineCombat = baselineDecision.Combat.Resolution;
        var selectedCombat = selectedDecision.Combat.Resolution;
        Check.True(
            baselineDecision.Accepted &&
            selectedDecision.Accepted &&
            baselineCombat.Outcome == CombatHitOutcome.Critical &&
            selectedCombat.Outcome == CombatHitOutcome.Critical,
            "exact AresBulwark Spear Hit resolves as the same critical event");
        Check.True(
            baselineCombat.Evidence.EffectiveDefense == 6_753 &&
            baselineCombat.Evidence.SkillCoreDamage == 176_087.08m &&
            baselineCombat.Evidence.DamageAfterTypedBonus ==
                1_305_368.741456m &&
            baselineCombat.Evidence.CriticalBonusDamage ==
                738_768.318194779968m &&
            baselineCombat.Evidence.DamageWithAppend ==
                2_102_743.059650779968m,
            "fixed pet append and critical cancellation precede mitigation");
        Check.True(
            baselineCombat.Evidence.DamageAfterReduction ==
                662_364.063789995689920m &&
            baselineCombat.Evidence.DamageAfterAbsorption ==
                511_154.063789995689920m,
            "Technique 30 percent plus Cooled 38.5 percent applies before doubled fixed cancellation");
        Check.True(
            baselineCombat.Damage == 470_261u &&
            selectedCombat.Damage == 376_209u &&
            selectedCombat.Evidence == baselineCombat.Evidence,
            "matching defender Zodiac applies flat then percent before Gaia " +
            "while an unselected defender remains at 470261 " +
            $"(baseline={baselineCombat.Damage}, " +
            $"selected={selectedCombat.Damage})");
        Check.True(
            baseline.Attacker.CurrentMp == 9_280 &&
            selected.Attacker.CurrentMp == 9_280,
            "defender Zodiac does not change the attacker's projected Spear Hit MP");
    }

    private static GameCharacter ExactSpearHitAttacker()
    {
        var attacker = Player(
            8_825,
            8_825,
            "test25",
            map: 0,
            camp: 0,
            profession: 1);
        attacker.CalculatedStats = new CharacterStats
        {
            CharacterId = attacker.Id,
            AccountId = attacker.AccountId,
            Name = attacker.Name,
            Profession = attacker.Profession,
            Level = attacker.Level,
            CurrentHp = attacker.CurrentHp,
            MaxHp = attacker.MaxHp,
            CurrentMp = attacker.CurrentMp,
            MaxMp = attacker.MaxMp,
            PhysicalAttack = 53_142,
            Hit = 100_000,
            Critical = 100_000,
            PhysicalDamageBonus = 64_132,
            PhysicalAppendDamage = 58_606,
            IgnorePhysicalDefense = 17_242,
            CriticalDamagePercent = 6_866,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 1.7f
        };
        attacker.ZodiacSkillGridLevels[4] = 4;
        attacker.ZodiacSkillGridSkillIds[4] = 20_029;
        return attacker;
    }

    private static GameCharacter ExactAresBulwark()
    {
        var target = Dummy();
        target.CalculatedStats = new CharacterStats
        {
            CharacterId = target.Id,
            AccountId = target.AccountId,
            Name = target.Name,
            Profession = target.Profession,
            Level = target.Level,
            CurrentHp = target.CurrentHp,
            MaxHp = target.MaxHp,
            CurrentMp = target.CurrentMp,
            MaxMp = target.MaxMp,
            PhysicalDefense = 33_765,
            Dodge = 0,
            CriticalResistance = 0,
            PhysicalDamageReduction =
                CooledTypedReductionBasisPoints +
                TechniqueReductionBasisPoints,
            MagicDamageReduction =
                CooledTypedReductionBasisPoints +
                TechniqueReductionBasisPoints,
            // Seven 6% critical-reduction Cooled stones.
            CriticalDamageReduction = 4_200,
            // Existing 29,152 plus doubled Technique cancellation 122,058.
            PhysicalFlatAbsorption =
                29_152 + DoubledTechniquePhysicalCancellation,
            // Existing 7,000 plus fixed Wisdom cancellation 152,623.
            CriticalDamageFlatReduction = 159_623,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 1.7f
        };
        return target;
    }

    private static void SelectAresBulwarkSpearHitDefense(
        GameCharacter target)
    {
        target.ZodiacSkillGridLevels[
            ZodiacDefensiveSkillProjection.PercentageTrainingFirstGrid] = 10;
        target.ZodiacSkillGridSkillIds[
            ZodiacDefensiveSkillProjection.PercentageTrainingFirstGrid] =
                20_029;
    }

    private static long FindExactCriticalRevision(
        GameCharacter attacker,
        GameCharacter target)
    {
        var projected = ZodiacOffensiveSkillProjection.Resolve(
            attacker,
            SpearHit()).Skill;
        var skill = TrainingDummyDamageSkillPolicy.Snapshot(projected);
        var source = CombatCharacterStatsAdapter.FromCharacter(attacker);
        var defender = CombatCharacterStatsAdapter.ToTarget(
            target.Level,
            target.CalculatedStats!);
        for (var revision = 1L; revision <= 1_000; revision++)
        {
            var eventId = CombatEventIdentity.ForPlayerSkill(
                attacker.Id,
                target.Id,
                attacker.VitalsRevision,
                target.VitalsRevision,
                revision,
                skill.SkillId);
            if (PlayerCombatRules.ResolvePvpSkillDamage(
                    source,
                    defender,
                    skill,
                    eventId).Outcome == CombatHitOutcome.Critical)
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            "Expected one exact critical Spear Hit within 1,000 revisions.");
    }
}
