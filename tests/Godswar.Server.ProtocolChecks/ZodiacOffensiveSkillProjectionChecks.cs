using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class ZodiacOffensiveSkillProjectionChecks
{
    public const string CheckName =
        "Zodiac selected-skill offense and MP projection";

    public static Task RunAsync()
    {
        CheckFixedTrainingCurve();
        CheckPercentageTrainingCurveAndCap();
        CheckCombinedRowProjection();
        CheckFamilyMatchingAndFailClosedState();
        CheckDirectEcsAndPvpFormulaParity();
        CheckPriestHealingRecognition();
        return Task.CompletedTask;
    }

    private static void CheckFixedTrainingCurve()
    {
        var samples = new (int Level, int Power, int Mana)[]
        {
            (1, 100, 2),
            (10, 1_000, 20),
            (11, 1_100, 24),
            (20, 2_000, 60),
            (21, 2_100, 62),
            (50, 5_000, 120)
        };
        foreach (var sample in samples)
        {
            var character = SelectedWarrior(
                grid: 0,
                sample.Level,
                skillKind: 10_003);
            var authored = Skill(
                skillId: 30,
                mana: 90,
                power1: 0.25m,
                power2: 40m);
            var projected = ZodiacOffensiveSkillProjection.Resolve(
                character,
                authored);
            var additionalMana = Math.Min(90, sample.Mana);
            Check.True(
                projected.Applied &&
                projected.FlatGridIndex == 0 &&
                projected.FlatLevel == sample.Level &&
                projected.PercentageGridIndex == -1 &&
                projected.AdditionalMana == additionalMana &&
                projected.Skill.Mp == 90 + additionalMana &&
                projected.Skill.Power1 == authored.Power1 &&
                projected.Skill.Power2 == 40m + sample.Power,
                $"Type-1 level {sample.Level} uses the shipped fixed power and MP entries");
        }
    }

    private static void CheckPercentageTrainingCurveAndCap()
    {
        var samples = new
            (int Requested, int ManaEffective, decimal Power, int Percent)[]
        {
            (1, 1, 0.02m, 5),
            (10, 10, 0.20m, 50),
            (11, 11, 0.23m, 60),
            (20, 20, 0.60m, 150),
            (21, 21, 0.62m, 155),
            (30, 30, 0.80m, 200),
            (31, 31, 0.82m, 210),
            (36, 36, 0.92m, 255),
            (45, 45, 1.10m, 300),
            (46, 45, 1.12m, 300),
            (50, 45, 1.20m, 300)
        };
        foreach (var sample in samples)
        {
            var character = SelectedWarrior(
                grid: 4,
                sample.Requested,
                skillKind: 20_010);
            var authored = Skill(
                skillId: 100,
                mana: 90,
                power1: 0.25m,
                power2: 40m);
            var projected = ZodiacOffensiveSkillProjection.Resolve(
                character,
                authored);
            var additionalMana = Math.Min(
                90,
                (90 * sample.Percent + 99) / 100);
            Check.True(
                projected.Applied &&
                projected.PercentageLevel == sample.Requested &&
                projected.PercentageManaEffectiveLevel ==
                    sample.ManaEffective &&
                projected.Skill.Power1 == 0.25m + sample.Power &&
                projected.Skill.Power2 == authored.Power2 &&
                projected.AdditionalMana == additionalMana &&
                projected.Skill.Mp == 90 + additionalMana,
                $"Type-2 level {sample.Requested} uses MP level {sample.ManaEffective}");
        }

        Check.True(
            ZodiacOffensiveSkillProjection.ResolveRoundedUpAdditionalMana(
                90,
                5) == 5 &&
            ZodiacOffensiveSkillProjection.ResolveRoundedUpAdditionalMana(
                1,
                5) == 1 &&
            ZodiacOffensiveSkillProjection.ResolveRoundedUpAdditionalMana(
                21,
                5) == 2 &&
            ZodiacOffensiveSkillProjection.ResolveRoundedUpAdditionalMana(
                0,
                300) == 0,
            "percentage MP is an additive integer surcharge rounded up deterministically");
    }

    private static void CheckCombinedRowProjection()
    {
        var authored = Skill(
            skillId: 30,
            mana: 90,
            power1: 0.25m,
            power2: 40m);
        var succeeded =
            ZodiacOffensiveSkillProjection.TryProjectMatchedLevels(
                authored,
                flatLevel: 50,
                percentageLevel: 50,
                out var projected,
                out var additionalMana,
                out var percentageManaEffectiveLevel);
        Check.True(
            succeeded &&
            percentageManaEffectiveLevel == 45 &&
            additionalMana == 90 &&
            projected.Mp == 180 &&
            projected.Power1 == 1.45m &&
            projected.Power2 == 5_040m,
            "matching Type-1 and Type-2 rows retain their power while the combined MP surcharge is capped at authored MP");
    }

    private static void CheckFamilyMatchingAndFailClosedState()
    {
        var character = SelectedWarrior(
            grid: 0,
            level: 1,
            skillKind: 10_003);
        for (var skillId = 30; skillId <= 34; skillId++)
        {
            Check.True(
                ZodiacOffensiveSkillProjection.Resolve(
                    character,
                    Skill(skillId)).Applied,
                $"all five runtime ranks in family 10003 receive the selected effect ({skillId})");
        }

        var outsideFamily = Skill(skillId: 35);
        var unchanged = ZodiacOffensiveSkillProjection.Resolve(
            character,
            outsideFamily);
        var backhaul = Skill(skillId: 3_062);
        var backhaulProjection = ZodiacOffensiveSkillProjection.Resolve(
            character,
            backhaul);
        Check.True(
            unchanged.Status ==
                ZodiacOffensiveSkillProjectionStatus.Unchanged &&
            unchanged.Skill == outsideFamily &&
            backhaulProjection.Status ==
                ZodiacOffensiveSkillProjectionStatus.Unchanged &&
            backhaulProjection.Skill == backhaul,
            "unselected families and backhaul keep their authored combat definition");

        AssertInvalidState(
            character,
            values => values.ZodiacSkillGridLevels = new int[8],
            "short level vector");
        AssertInvalidState(
            character,
            values => values.ZodiacSkillGridLevels[0] = 51,
            "out-of-range level");
        AssertInvalidState(
            character,
            values =>
            {
                values.ZodiacSkillGridLevels[1] = 1;
                values.ZodiacSkillGridSkillIds[1] = 10_003;
            },
            "duplicate row selection");
        AssertInvalidState(
            character,
            values => values.ZodiacSkillGridLevels[0] = 0,
            "selection on inactive grid");
        AssertInvalidState(
            character,
            values => values.ZodiacSkillGridSkillIds[0] = 20_010,
            "wrong selection kind for fixed row");

        var defensiveCorruption = Clone(character);
        defensiveCorruption.ZodiacSkillGridLevels[8] = int.MaxValue;
        defensiveCorruption.ZodiacSkillGridSkillIds[8] = int.MinValue;
        Check.True(
            ZodiacOffensiveSkillProjection.Resolve(
                defensiveCorruption,
                Skill(30)).Applied,
            "offensive projection does not own or reinterpret defensive rows");
    }

    private static void CheckDirectEcsAndPvpFormulaParity()
    {
        var character = SelectedWarrior(0, 1, 10_003);
        character.CalculatedStats = new CharacterStats
        {
            PhysicalAttack = 100,
            MagicAttack = 100
        };
        var authored = Skill(30, mana: 90, power1: 0m, power2: 0m);
        var projected = ZodiacOffensiveSkillProjection.Resolve(
            character,
            authored).Skill;
        Check.Equal(
            200u,
            SkillCombatResolver.CalculateDamage(character, projected),
            "direct monster formula consumes projected Type-1 Power2");

        var request = PlayerCombatEcsRequest.HostileSkill(
            PlayerCombatIntentKind.SingleTargetSkill,
            DateTimeOffset.UnixEpoch,
            targetObjectId: 1,
            projected);
        var offense = new PlayerCombatOffenseComponent(
            Profession: 0,
            PhysicalAttack: 100,
            MagicAttack: 100,
            PhysicalDamageBonus: 0,
            MagicDamageBonus: 0,
            PhysicalAppendDamage: 0,
            MagicAppendDamage: 0);
        Check.True(
            request.Skill.ManaCost == 92 &&
            request.Skill.Power1 == projected.Power1 &&
            request.Skill.Power2 == projected.Power2 &&
            PlayerCombatRules.CalculateSkillDamage(offense, request.Skill) ==
                SkillCombatResolver.CalculateDamage(character, projected),
            "ECS snapshot preserves projected MP and formula values with direct parity");

        var typeTwoCharacter = SelectedWarrior(4, 1, 20_010);
        var typeTwoAuthored = Skill(
            100,
            mana: 90,
            power1: 0m,
            power2: 0m);
        var typeTwo = ZodiacOffensiveSkillProjection.Resolve(
            typeTwoCharacter,
            typeTwoAuthored).Skill;
        var baseSnapshot = TrainingDummyDamageSkillPolicy.Snapshot(
            typeTwoAuthored);
        var projectedSnapshot = TrainingDummyDamageSkillPolicy.Snapshot(
            typeTwo);
        var attacker = new CombatAttackerStats
        {
            Level = 1,
            Profession = 0,
            PhysicalAttack = 100,
            MagicAttack = 100
        };
        var foundNormal = false;
        for (ulong eventId = 1; eventId <= 100; eventId++)
        {
            var baseline = PlayerCombatRules.ResolvePvpSkillDamage(
                attacker,
                target: default,
                baseSnapshot,
                eventId);
            if (baseline.Outcome != CombatHitOutcome.Normal)
            {
                continue;
            }

            var improved = PlayerCombatRules.ResolvePvpSkillDamage(
                attacker,
                target: default,
                projectedSnapshot,
                eventId);
            Check.True(
                improved.Outcome == CombatHitOutcome.Normal &&
                improved.Damage == 102 &&
                improved.Damage > baseline.Damage &&
                projectedSnapshot.ManaCost == 95,
                "PvP/training formula consumes projected Type-2 Power1 and rounded MP");
            foundNormal = true;
            break;
        }
        Check.True(foundNormal, "deterministic PvP sample includes a normal hit");
    }

    private static void CheckPriestHealingRecognition()
    {
        CheckProjectedHeal(
            skillId: 750,
            skillKind: 10_075,
            PriestHealingSkillKind.SingleTarget,
            authoredHeal: 500);
        CheckProjectedHeal(
            skillId: 760,
            skillKind: 10_076,
            PriestHealingSkillKind.Area,
            authoredHeal: 300);

        static void CheckProjectedHeal(
            int skillId,
            int skillKind,
            PriestHealingSkillKind expectedKind,
            int authoredHeal)
        {
            Check.True(
                GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                    skillId,
                    out var authored),
                $"healing skill {skillId} exists");
            var character = new GameCharacter { Profession = 2 };
            character.ZodiacSkillGridLevels[0] = 1;
            character.ZodiacSkillGridSkillIds[0] = skillKind;
            var projected = ZodiacOffensiveSkillProjection.Resolve(
                character,
                authored);
            Check.True(
                projected.Applied &&
                projected.Skill.Power2 == authoredHeal + 100m &&
                projected.Skill.Mp == authored.Mp + 2 &&
                PriestHealingSkillCatalog.TryResolve(
                    projected.Skill,
                    out var healing) &&
                healing.Kind == expectedKind &&
                healing.HealAmount == authoredHeal + 100,
                $"Zodiac Type-1 projects before Priest healing recognition for {skillId}");
        }
    }

    private static void AssertInvalidState(
        GameCharacter source,
        Action<GameCharacter> corrupt,
        string description)
    {
        var character = Clone(source);
        corrupt(character);
        var authored = Skill(30);
        var projected = ZodiacOffensiveSkillProjection.Resolve(
            character,
            authored);
        Check.True(
            projected.Status ==
                ZodiacOffensiveSkillProjectionStatus.InvalidState &&
            projected.Skill == authored,
            $"{description} fails closed to the authored skill without rejecting cast");
    }

    private static GameCharacter SelectedWarrior(
        int grid,
        int level,
        int skillKind)
    {
        var character = new GameCharacter { Profession = 0 };
        character.ZodiacSkillGridLevels[grid] = level;
        character.ZodiacSkillGridSkillIds[grid] = skillKind;
        return character;
    }

    private static GameCharacter Clone(GameCharacter source) =>
        new()
        {
            Profession = source.Profession,
            ZodiacSkillGridLevels =
                (int[])source.ZodiacSkillGridLevels.Clone(),
            ZodiacSkillGridSkillIds =
                (int[])source.ZodiacSkillGridSkillIds.Clone()
        };

    private static SkillCombatDefinition Skill(
        int skillId,
        int mana = 90,
        decimal power1 = 0.25m,
        decimal power2 = 40m) =>
        new(
            skillId,
            Target: 44,
            AffectObj: 28,
            Distance: 8f,
            Range: 0f,
            Property: 0,
            Mp: mana,
            Power1: power1,
            Power2: power2,
            CastTime: TimeSpan.Zero,
            Cooldown: TimeSpan.FromSeconds(5));
}
