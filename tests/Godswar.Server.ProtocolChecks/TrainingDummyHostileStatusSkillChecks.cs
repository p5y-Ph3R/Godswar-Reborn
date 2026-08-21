using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummyHostileStatusSkillChecks
{
    public const string CheckName =
        "Authored training-dummy hostile status skills";

    public static async Task RunAsync()
    {
        CheckCatalog();
        CheckControlSemantics();
        CheckClientProjection();
        await CheckStatusOnlyHandlerRuntimeAsync();
    }

    private static void CheckCatalog()
    {
        var expected = new[]
        {
            Entry(70, 0, 331, 150, 11, 1, 3, 30, 25, 1, 3),
            Entry(71, 0, 331, 190, 11, 1, 3, 26, 60, 1, 3),
            Entry(72, 0, 331, 200, 11, 1, 3, 23, 110, 1, 3),
            Entry(73, 0, 331, 230, 11, 1, 3, 20, 180, 1, 3),
            Entry(74, 0, 331, 250, 11, 1, 3, 18, 250, 1, 3),
            Entry(80, 0, 100, 240, 4, 1, 20, 60, 50, 2, 10),
            Entry(81, 0, 101, 240, 4, 2, 20, 60, 80, 2, 10),
            Entry(82, 0, 102, 240, 4, 3, 20, 60, 110, 2, 10),
            Entry(83, 0, 103, 240, 4, 4, 20, 60, 150, 2, 10),
            Entry(84, 0, 104, 240, 4, 5, 20, 60, 200, 2, 10),
            Entry(350, 1, 301, 150, 10, 1, 3, 60, 50, 1, 9),
            Entry(351, 1, 302, 190, 10, 2, 4, 55, 80, 1, 9),
            Entry(352, 1, 303, 200, 10, 3, 5, 50, 120, 1, 9),
            Entry(353, 1, 304, 230, 10, 4, 6, 45, 180, 1, 9),
            Entry(354, 1, 305, 250, 10, 5, 6, 40, 250, 1, 9),
            Entry(320, 1, 130, 200, 5, 1, 12, 36, 60, 2, 10,
                HostileStatusApplicationTrigger.CommittedDamagingHit, 1_000),
            Entry(321, 1, 131, 220, 5, 2, 20, 36, 100, 2, 10,
                HostileStatusApplicationTrigger.CommittedDamagingHit, 1_000),
            Entry(322, 1, 132, 240, 5, 3, 12, 36, 200, 2, 10,
                HostileStatusApplicationTrigger.CommittedDamagingHit, 1_500),
            Entry(323, 1, 133, 300, 5, 4, 20, 36, 350, 2, 10,
                HostileStatusApplicationTrigger.CommittedDamagingHit, 1_500),
            Entry(324, 1, 133, 300, 5, 4, 20, 36, 420, 2, 10,
                HostileStatusApplicationTrigger.CommittedDamagingHit, 1_500),
            Entry(330, 1, 130, 200, 5, 1, 12, 36, 180, 2, 10,
                HostileStatusApplicationTrigger.CommittedDamagingHit, 1_000),
            Entry(331, 1, 131, 220, 5, 2, 20, 36, 270, 2, 10,
                HostileStatusApplicationTrigger.CommittedDamagingHit, 1_000),
            Entry(332, 1, 132, 240, 5, 3, 12, 36, 450, 2, 10,
                HostileStatusApplicationTrigger.CommittedDamagingHit, 1_500),
            Entry(333, 1, 133, 300, 5, 4, 20, 36, 750, 2, 10,
                HostileStatusApplicationTrigger.CommittedDamagingHit, 1_500),
            Entry(334, 1, 133, 300, 5, 4, 20, 36, 900, 2, 10,
                HostileStatusApplicationTrigger.CommittedDamagingHit, 1_500),
            Entry(600, 3, 360, 220, 12, 1, 10, 50, 100, 1, 11),
            Entry(601, 3, 360, 250, 12, 1, 10, 42, 180, 1, 11),
            Entry(602, 3, 360, 280, 12, 1, 10, 38, 250, 1, 11),
            Entry(603, 3, 363, 280, 12, 1, 11, 38, 350, 1, 11),
            Entry(604, 3, 364, 280, 12, 1, 12, 38, 490, 1, 11),
            Entry(790, 2, 400, 220, 13, 1, 10, 50, 100, 1, 7),
            Entry(791, 2, 400, 250, 13, 1, 10, 42, 160, 1, 7),
            Entry(792, 2, 400, 280, 13, 1, 10, 38, 240, 1, 7),
            Entry(793, 2, 407, 280, 13, 1, 11, 38, 336, 1, 7),
            Entry(794, 2, 408, 280, 13, 1, 12, 38, 470, 1, 7)
        };

        Check.Equal(
            expected.Length,
            TrainingDummyHostileStatusSkillCatalog.Count,
            "all requested hostile-status ranks are sealed");
        foreach (var entry in expected)
        {
            Check.True(
                TrainingDummyHostileStatusSkillCatalog.TryGet(
                    entry.SkillId,
                    out var actual),
                $"hostile status skill {entry.SkillId} is present");
            actual.Validate();
            Check.True(
                actual.SkillId == entry.SkillId &&
                actual.RequiredProfession == entry.Profession &&
                actual.StatusId == entry.StatusId &&
                actual.NativeStatusOddsRating == entry.StatusOdds &&
                actual.Kind == entry.Kind &&
                actual.Priority == entry.Priority &&
                actual.Duration == TimeSpan.FromSeconds(entry.Duration) &&
                actual.Cooldown == TimeSpan.FromSeconds(entry.Cooldown) &&
                actual.ManaCost == entry.Mana &&
                (byte)actual.TargetMode == entry.TargetMode &&
                actual.Range == entry.Range &&
                actual.Trigger == entry.Trigger &&
                actual.PhysicalDamageTakenIncreaseBasisPoints ==
                    entry.PhysicalDamageTakenIncreaseBasisPoints &&
                actual.MagicDamageTakenIncreaseBasisPoints == 0,
                $"hostile status skill {entry.SkillId} preserves stock metadata");

            Check.True(
                GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                    entry.SkillId,
                    out var published) &&
                published.Mp == entry.Mana &&
                published.Cooldown ==
                    TimeSpan.FromSeconds(entry.Cooldown) &&
                published.Distance ==
                    (entry.TargetMode == 1 ? entry.Range : 0f) &&
                published.Range ==
                    (entry.TargetMode == 2 ? entry.Range : 0f),
                $"hostile status skill {entry.SkillId} matches published combat content");
        }

        Check.True(
            !TrainingDummyHostileStatusSkillCatalog.TryGet(69, out _) &&
            !TrainingDummyHostileStatusSkillCatalog.TryGet(85, out _) &&
            !TrainingDummyHostileStatusSkillCatalog.TryGet(319, out _) &&
            !TrainingDummyHostileStatusSkillCatalog.TryGet(325, out _) &&
            !TrainingDummyHostileStatusSkillCatalog.TryGet(329, out _) &&
            !TrainingDummyHostileStatusSkillCatalog.TryGet(335, out _),
            "only the bounded Spear/Meteor Injury rank families are admitted");
    }

    private static void CheckControlSemantics()
    {
        var full = HostileStatusControlFlags.HaltIntonate |
            HostileStatusControlFlags.NonMoving |
            HostileStatusControlFlags.NonMagicUsing |
            HostileStatusControlFlags.NonTechniqueUsing |
            HostileStatusControlFlags.NonAttackUsing |
            HostileStatusControlFlags.NonItemUsing;
        var expose = Required(84);
        var freeze = Required(354);
        var stun = Required(74);
        var silence = Required(604);
        var cage = Required(794);
        var injuries = new[]
        {
            Required(320), Required(321), Required(322), Required(323),
            Required(324), Required(330), Required(331), Required(332),
            Required(333), Required(334)
        };
        Check.True(
            expose.Control == HostileStatusControlFlags.None &&
            expose.PhysicalDefenseModifier == -400 &&
            expose.MagicDefenseModifier == -300,
            "Expose Armor 5 preserves flat defense reductions");
        Check.True(
            freeze.Control ==
                (HostileStatusControlFlags.HaltIntonate |
                 HostileStatusControlFlags.NonMoving),
            "Freeze interrupts an active cast and roots without full action lock");
        Check.True(
            stun.Control == full && cage.Control == full,
            "Stun and Cage retain the complete stock action lock");
        Check.True(
            silence.Control ==
                (HostileStatusControlFlags.NonMagicUsing |
                 HostileStatusControlFlags.NonTechniqueUsing),
            "Silence blocks skills without rooting or blocking basic attacks");
        Check.True(
            cage.PhysicalDamageReductionBasisPoints == 7_500 &&
            cage.MagicDamageReductionBasisPoints == 7_500,
            "Cage retains the tooltip-backed 75-percent incoming mitigation");
        Check.True(
            injuries.All(static injury =>
                injury.Trigger ==
                    HostileStatusApplicationTrigger.CommittedDamagingHit &&
                injury.Control == HostileStatusControlFlags.None &&
                injury.PhysicalDamageTakenIncreaseBasisPoints ==
                    (injury.StatusId is 130 or 131 ? 1_000 : 1_500)),
            "every Blast rank uses committed-hit physical-only Injury");
        Check.True(
            HostileStatusControlCatalog.FrozenKind == 10 &&
            HostileStatusControlCatalog.StunnedKind == 11 &&
            HostileStatusControlCatalog.CagedKind == 13,
            "Frozen, Stuned, and Caged retain their stock status kinds");
    }

    private static HostileStatusEffectDefinition Required(int skillId)
    {
        if (!TrainingDummyHostileStatusSkillCatalog.TryGet(
                skillId,
                out var definition))
        {
            throw new InvalidOperationException(
                $"Missing hostile status skill {skillId}.");
        }

        return definition;
    }

    private static Expected Entry(
        int skillId,
        byte profession,
        uint statusId,
        int statusOdds,
        int kind,
        int priority,
        int duration,
        int cooldown,
        int mana,
        byte targetMode,
        float range,
        HostileStatusApplicationTrigger trigger =
            HostileStatusApplicationTrigger.CommittedCast,
        int physicalDamageTakenIncreaseBasisPoints = 0) =>
        new(
            skillId,
            profession,
            statusId,
            statusOdds,
            kind,
            priority,
            duration,
            cooldown,
            mana,
            targetMode,
            range,
            trigger,
            physicalDamageTakenIncreaseBasisPoints);

    private readonly record struct Expected(
        int SkillId,
        byte Profession,
        uint StatusId,
        int StatusOdds,
        int Kind,
        int Priority,
        int Duration,
        int Cooldown,
        int Mana,
        byte TargetMode,
        float Range,
        HostileStatusApplicationTrigger Trigger,
        int PhysicalDamageTakenIncreaseBasisPoints);
}
