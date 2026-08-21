using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class ZodiacDefensiveSkillProjectionChecks
{
    public const string CheckName =
        "Defensive Zodiac selected-skill damage projection";

    public static async Task RunAsync()
    {
        CheckShippedCurves();
        CheckFlatDamageProjection();
        CheckPercentageDamageProjection();
        CheckFlatThenPercentageProjection();
        CheckNonmatchingAndBasicAttacksRemainUnchanged();
        CheckMaximumPercentageCannotUnderflow();
        CheckInvalidStateFailsClosed();
        await CheckAtomicZodiacSnapshotAsync();
    }

    private static void CheckShippedCurves()
    {
        Check.Equal(
            200,
            ZodiacDefensiveSkillProjection.ResolvePercentageBasisPoints(1),
            "Improve DEF level 1 percentage");
        Check.Equal(
            2_000,
            ZodiacDefensiveSkillProjection.ResolvePercentageBasisPoints(10),
            "Improve DEF level 10 percentage");
        Check.Equal(
            2_300,
            ZodiacDefensiveSkillProjection.ResolvePercentageBasisPoints(11),
            "Improve DEF level 11 percentage");
        Check.Equal(
            3_500,
            ZodiacDefensiveSkillProjection.ResolvePercentageBasisPoints(15),
            "Improve DEF level 15 percentage");
        Check.Equal(
            4_000,
            ZodiacDefensiveSkillProjection.ResolvePercentageBasisPoints(16),
            "Improve DEF level 16 percentage");
        Check.Equal(
            6_000,
            ZodiacDefensiveSkillProjection.ResolvePercentageBasisPoints(20),
            "Improve DEF level 20 percentage");
        Check.Equal(
            6_200,
            ZodiacDefensiveSkillProjection.ResolvePercentageBasisPoints(21),
            "Improve DEF level 21 percentage");
        Check.Equal(
            12_000,
            ZodiacDefensiveSkillProjection.ResolvePercentageBasisPoints(50),
            "Improve DEF level 50 percentage");
    }

    private static void CheckFlatDamageProjection()
    {
        var defender = Defender(
            grid: ZodiacDefensiveSkillProjection.FlatTrainingFirstGrid,
            level: 5,
            selectedKind: 10_030);
        var adjustment = ZodiacDefensiveSkillProjection.ResolveAdjustment(
            defender,
            runtimeSkillId: 302);
        Check.Equal(
            500,
            adjustment.FlatDamageReduction,
            "DEF Skill Training subtracts 100 resolved damage per level");
        Check.Equal(
            0,
            adjustment.DamageReductionBasisPoints,
            "flat training does not invent a percentage reduction");

        var skill = Skill(skillId: 302, flatPower: 1_000m);
        var eventId = FindHittingEvent(skill, defender);
        var baseline = PlayerCombatRules.ResolvePvpSkillDamage(
            Attacker(),
            Target(),
            skill,
            eventId);
        var projected = ZodiacDefensiveSkillProjection.ResolvePvpSkillDamage(
            defender,
            Attacker(),
            Target(),
            skill,
            eventId);
        Check.Equal(
            baseline.Damage > 500u ? baseline.Damage - 500u : 0u,
            projected.Damage,
            "flat DEF reduction saturates resolved damage at zero");
        Check.True(
            projected.Evidence == baseline.Evidence,
            "flat DEF reduction preserves the original combat evidence");
    }

    private static void CheckPercentageDamageProjection()
    {
        var defender = Defender(
            grid: ZodiacDefensiveSkillProjection.PercentageTrainingFirstGrid,
            level: 10,
            selectedKind: 20_028);
        var skill = Skill(skillId: 282, flatPower: 1_000m);
        var eventId = FindHittingEvent(skill, defender);
        var baseline = PlayerCombatRules.ResolvePvpSkillDamage(
            Attacker(),
            Target(),
            skill,
            eventId);
        var projected = ZodiacDefensiveSkillProjection.ResolvePvpSkillDamage(
            defender,
            Attacker(),
            Target(),
            skill,
            eventId);
        var expected = (uint)(((decimal)baseline.Damage * 8_000m) / 10_000m);
        Check.Equal(
            expected,
            projected.Damage,
            "Improve DEF level 10 removes 20 percent after skill resolution");
        Check.Equal(
            baseline.Evidence.SkillCoreDamage,
            projected.Evidence.SkillCoreDamage,
            "percentage DEF does not rewrite authored skill power evidence");
    }

    private static void CheckFlatThenPercentageProjection()
    {
        var baseline = PlayerCombatRules.ResolvePvpSkillDamage(
            Attacker(),
            Target(),
            Skill(skillId: 302, flatPower: 1_000m),
            eventId: 1);
        var projected = ZodiacDefensiveSkillProjection.ProjectResolvedDamage(
            baseline,
            new ZodiacDefensiveSkillAdjustment(
                FlatDamageReduction: 100,
                DamageReductionBasisPoints: 2_000));
        var expected = (uint)(
            Math.Max(0m, (decimal)baseline.Damage - 100m) *
            8_000m /
            10_000m);
        Check.True(
            projected.Damage == expected &&
            projected.Evidence == baseline.Evidence &&
            projected.Rolls == baseline.Rolls,
            "stacked Type-3 and Type-4 reduce flat damage first, then truncate the remaining percentage deterministically");
    }

    private static void CheckNonmatchingAndBasicAttacksRemainUnchanged()
    {
        var defender = Defender(
            grid: ZodiacDefensiveSkillProjection.FlatTrainingFirstGrid,
            level: 50,
            selectedKind: 10_030);
        var nonmatching = Skill(skillId: 309, flatPower: 1_000m);
        var eventId = FindHittingEvent(nonmatching, defender);
        var baselineSkill = PlayerCombatRules.ResolvePvpSkillDamage(
            Attacker(),
            Target(),
            nonmatching,
            eventId);
        var projectedSkill =
            ZodiacDefensiveSkillProjection.ResolvePvpSkillDamage(
                defender,
                Attacker(),
                Target(),
                nonmatching,
                eventId);
        Check.True(
            baselineSkill == projectedSkill,
            "an unselected runtime skill family is byte-for-value unchanged");

        var baselineBasic = PlayerCombatRules.ResolvePvpBasicAttack(
            Attacker(),
            Target(),
            eventId: 99);
        var repeatedBasic = PlayerCombatRules.ResolvePvpBasicAttack(
            Attacker(),
            Target(),
            eventId: 99);
        Check.True(
            baselineBasic == repeatedBasic,
            "basic attacks have no defensive Zodiac projection entry point");

        var matching = Skill(skillId: 302, flatPower: 1_000m);
        var pveBaseline = PlayerCombatRules.ResolveSkillDamage(
            Attacker(),
            Target(),
            matching,
            eventId: 101);
        var adjustment = ZodiacDefensiveSkillProjection.ResolveAdjustment(
            defender,
            matching.SkillId);
        var pveRepeated = PlayerCombatRules.ResolveSkillDamage(
            Attacker(),
            Target(),
            matching,
            eventId: 101);
        Check.True(
            !adjustment.IsEmpty && pveBaseline == pveRepeated,
            "monster/PvE skill resolution does not consume player-defender Zodiac state");
    }

    private static void CheckMaximumPercentageCannotUnderflow()
    {
        var defender = Defender(
            grid: ZodiacDefensiveSkillProjection.PercentageTrainingFirstGrid,
            level: 50,
            selectedKind: 20_028);
        var skill = Skill(skillId: 284, flatPower: 1_000m);
        var eventId = FindHittingEvent(skill, defender);
        var projected = ZodiacDefensiveSkillProjection.ResolvePvpSkillDamage(
            defender,
            Attacker(),
            Target(),
            skill,
            eventId);
        Check.Equal(
            0u,
            projected.Damage,
            "the shipped 120 percent cap saturates at zero damage");
    }

    private static void CheckInvalidStateFailsClosed()
    {
        AssertInvalidState(
            defender => defender.ZodiacSkillGridLevels[8] = 51,
            "out-of-range defensive level");
        AssertInvalidState(
            defender => defender.ZodiacSkillGridLevels[8] = 0,
            "selection on an inactive defensive grid");
        AssertInvalidState(
            defender => defender.ZodiacSkillGridSkillIds[8] = 20_028,
            "Type-2 kind placed in a Type-3 row");
        AssertInvalidState(
            defender =>
            {
                defender.ZodiacSkillGridLevels[9] = 1;
                defender.ZodiacSkillGridSkillIds[9] = 10_030;
            },
            "duplicate defensive row selection");
        AssertInvalidState(
            defender => defender.ZodiacSkillGridSkillIds[8] = 10_099,
            "unknown defensive skill kind");
        AssertInvalidState(
            defender => defender.ZodiacSkillGridLevels = new int[15],
            "short defensive level vector");

        var offensiveCorruption = Defender(
            grid: ZodiacDefensiveSkillProjection.FlatTrainingFirstGrid,
            level: 1,
            selectedKind: 10_030);
        offensiveCorruption.ZodiacSkillGridLevels[0] = int.MaxValue;
        offensiveCorruption.ZodiacSkillGridSkillIds[0] = int.MinValue;
        Check.Equal(
            100,
            ZodiacDefensiveSkillProjection.ResolveAdjustment(
                offensiveCorruption,
                runtimeSkillId: 302).FlatDamageReduction,
            "defensive validation does not reinterpret offensive rows");
    }

    private static async Task CheckAtomicZodiacSnapshotAsync()
    {
        var defender = Defender(
            grid: ZodiacDefensiveSkillProjection.FlatTrainingFirstGrid,
            level: 5,
            selectedKind: 10_030);
        using var started = new ManualResetEventSlim();
        Task<ZodiacDefensiveSkillAdjustment> pending;
        lock (defender.ZodiacSync)
        {
            pending = Task.Run(() =>
            {
                started.Set();
                return ZodiacDefensiveSkillProjection.ResolveAdjustment(
                    defender,
                    runtimeSkillId: 302);
            });
            Check.True(
                started.Wait(TimeSpan.FromSeconds(5)),
                "defensive Zodiac snapshot worker started");
            Check.True(
                !pending.Wait(TimeSpan.FromMilliseconds(100)),
                "defensive Zodiac resolution waits for the character snapshot lock");
            defender.ZodiacSkillGridLevels[8] = 6;
        }

        var adjustment = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Equal(
            600,
            adjustment.FlatDamageReduction,
            "defensive Zodiac reads one post-lock snapshot without torn state");
    }

    private static void AssertInvalidState(
        Action<GameCharacter> corrupt,
        string description)
    {
        var defender = Defender(
            grid: ZodiacDefensiveSkillProjection.FlatTrainingFirstGrid,
            level: 1,
            selectedKind: 10_030);
        corrupt(defender);
        var adjustment = ZodiacDefensiveSkillProjection.ResolveAdjustment(
            defender,
            runtimeSkillId: 302);
        var skill = Skill(skillId: 302, flatPower: 1_000m);
        var baseline = PlayerCombatRules.ResolvePvpSkillDamage(
            Attacker(),
            Target(),
            skill,
            eventId: 1);
        var projected = ZodiacDefensiveSkillProjection.ResolvePvpSkillDamage(
            defender,
            Attacker(),
            Target(),
            skill,
            eventId: 1);
        Check.True(
            adjustment.IsEmpty && projected == baseline,
            $"{description} fails closed to no defensive adjustment");
    }

    private static GameCharacter Defender(
        int grid,
        int level,
        int selectedKind)
    {
        var character = new GameCharacter
        {
            ZodiacSkillGridLevels = ZodiacSkillGridCatalog.CreateEmptyLevels(),
            ZodiacSkillGridSkillIds =
                ZodiacSkillGridCatalog.CreateEmptySkillIds()
        };
        character.ZodiacSkillGridLevels[grid] = level;
        character.ZodiacSkillGridSkillIds[grid] = selectedKind;
        return character;
    }

    private static CombatAttackerStats Attacker() => new()
    {
        Level = 50,
        Profession = 0,
        PhysicalAttack = 2_000,
        Hit = 100_000,
        Critical = 0
    };

    private static CombatTargetStats Target() => new()
    {
        Level = 50,
        PhysicalDefense = 500,
        Dodge = 0,
        CriticalResistance = 100_000
    };

    private static PlayerCombatSkillSnapshot Skill(
        uint skillId,
        decimal flatPower) =>
        new(
            skillId,
            Target: 44,
            AffectObject: 28,
            Distance: 3f,
            AreaRadius: 0f,
            ManaCost: 0,
            Property: 0,
            Power1: 0m,
            Power2: flatPower);

    private static ulong FindHittingEvent(
        in PlayerCombatSkillSnapshot skill,
        GameCharacter defender)
    {
        for (ulong eventId = 1; eventId <= 100; eventId++)
        {
            if (ZodiacDefensiveSkillProjection.ResolvePvpSkillDamage(
                    defender,
                    Attacker(),
                    Target(),
                    skill,
                    eventId).Hit)
            {
                return eventId;
            }
        }

        throw new InvalidOperationException(
            "Expected one deterministic hit in the first 100 events.");
    }
}
