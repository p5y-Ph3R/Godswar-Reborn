using System.Collections.Frozen;

namespace Godswar.Server.State;

/// <summary>
/// Installed-client hostile status skills enabled only by the exact training
/// dummy transaction. StatusOdds is retained as native metadata; the runtime
/// landing policy is owned by HostileStatusProcPolicy.
/// </summary>
internal static class TrainingDummyHostileStatusSkillCatalog
{
    private const byte Warrior = 0;
    private const byte Champion = 1;
    private const byte Priest = 2;
    private const byte Mage = 3;

    private const HostileStatusControlFlags FullActionControl =
        HostileStatusControlFlags.HaltIntonate |
        HostileStatusControlFlags.NonMoving |
        HostileStatusControlFlags.NonMagicUsing |
        HostileStatusControlFlags.NonTechniqueUsing |
        HostileStatusControlFlags.NonAttackUsing |
        HostileStatusControlFlags.NonItemUsing;

    private static readonly FrozenDictionary<
        int,
        HostileStatusEffectDefinition> Definitions =
        new HostileStatusEffectDefinition[]
        {
            Stun(70, 150, 25, 30),
            Stun(71, 190, 60, 26),
            Stun(72, 200, 110, 23),
            Stun(73, 230, 180, 20),
            Stun(74, 250, 250, 18),

            ExposeArmor(80, 100, 1, 50, -30, -20),
            ExposeArmor(81, 101, 2, 80, -70, -50),
            ExposeArmor(82, 102, 3, 110, -140, -100),
            ExposeArmor(83, 103, 4, 150, -250, -180),
            ExposeArmor(84, 104, 5, 200, -400, -300),

            Freeze(350, 301, 150, 1, 3, 50, 60),
            Freeze(351, 302, 190, 2, 4, 80, 55),
            Freeze(352, 303, 200, 3, 5, 120, 50),
            Freeze(353, 304, 230, 4, 6, 180, 45),
            Freeze(354, 305, 250, 5, 6, 250, 40),

            InternalInjury(320, 130, 200, 1, 12, 60, 1_000),
            InternalInjury(321, 131, 220, 2, 20, 100, 1_000),
            InternalInjury(322, 132, 240, 3, 12, 200, 1_500),
            InternalInjury(323, 133, 300, 4, 20, 350, 1_500),
            InternalInjury(324, 133, 300, 4, 20, 420, 1_500),

            InternalInjury(330, 130, 200, 1, 12, 180, 1_000),
            InternalInjury(331, 131, 220, 2, 20, 270, 1_000),
            InternalInjury(332, 132, 240, 3, 12, 450, 1_500),
            InternalInjury(333, 133, 300, 4, 20, 750, 1_500),
            InternalInjury(334, 133, 300, 4, 20, 900, 1_500),

            Silence(600, 360, 220, 10, 100, 50),
            Silence(601, 360, 250, 10, 180, 42),
            Silence(602, 360, 280, 10, 250, 38),
            Silence(603, 363, 280, 11, 350, 38),
            Silence(604, 364, 280, 12, 490, 38),

            Shackle(790, 400, 220, 10, 100, 50),
            Shackle(791, 400, 250, 10, 160, 42),
            Shackle(792, 400, 280, 10, 240, 38),
            Shackle(793, 407, 280, 11, 336, 38),
            Shackle(794, 408, 280, 12, 470, 38)
        }.ToFrozenDictionary(static definition => definition.SkillId);

    public static int Count => Definitions.Count;

    public static bool TryGet(
        int skillId,
        out HostileStatusEffectDefinition definition) =>
        Definitions.TryGetValue(skillId, out definition);

    private static HostileStatusEffectDefinition Stun(
        int skillId,
        int nativeOdds,
        int mana,
        int cooldownSeconds) =>
        Definition(
            skillId,
            Warrior,
            statusId: 331,
            nativeOdds,
            kind: 11,
            priority: 1,
            durationSeconds: 3,
            cooldownSeconds,
            mana,
            HostileStatusTargetMode.SingleTarget,
            range: 3f,
            control: FullActionControl);

    private static HostileStatusEffectDefinition ExposeArmor(
        int skillId,
        uint statusId,
        int priority,
        int mana,
        int physicalDefense,
        int magicDefense) =>
        Definition(
            skillId,
            Warrior,
            statusId,
            nativeOdds: 240,
            kind: 4,
            priority,
            durationSeconds: 20,
            cooldownSeconds: 60,
            mana,
            HostileStatusTargetMode.SelfCenteredArea,
            range: 10f,
            physicalDefense,
            magicDefense);

    private static HostileStatusEffectDefinition Freeze(
        int skillId,
        uint statusId,
        int nativeOdds,
        int priority,
        int durationSeconds,
        int mana,
        int cooldownSeconds) =>
        Definition(
            skillId,
            Champion,
            statusId,
            nativeOdds,
            kind: 10,
            priority,
            durationSeconds,
            cooldownSeconds,
            mana,
            HostileStatusTargetMode.SingleTarget,
            range: 9f,
            control: HostileStatusControlFlags.HaltIntonate |
                HostileStatusControlFlags.NonMoving);

    private static HostileStatusEffectDefinition Silence(
        int skillId,
        uint statusId,
        int nativeOdds,
        int durationSeconds,
        int mana,
        int cooldownSeconds) =>
        Definition(
            skillId,
            Mage,
            statusId,
            nativeOdds,
            kind: 12,
            priority: 1,
            durationSeconds,
            cooldownSeconds,
            mana,
            HostileStatusTargetMode.SingleTarget,
            range: 11f,
            control: HostileStatusControlFlags.NonMagicUsing |
                HostileStatusControlFlags.NonTechniqueUsing);

    private static HostileStatusEffectDefinition InternalInjury(
        int skillId,
        uint statusId,
        int nativeOdds,
        int priority,
        int durationSeconds,
        int mana,
        int physicalDamageTakenIncreaseBasisPoints) =>
        new(
            skillId,
            Champion,
            statusId,
            nativeOdds,
            Kind: 5,
            Priority: priority,
            Duration: TimeSpan.FromSeconds(durationSeconds),
            Cooldown: TimeSpan.FromSeconds(36),
            ManaCost: mana,
            TargetMode: HostileStatusTargetMode.SelfCenteredArea,
            Range: 10f,
            Trigger: HostileStatusApplicationTrigger.CommittedDamagingHit,
            PhysicalDamageTakenIncreaseBasisPoints:
                physicalDamageTakenIncreaseBasisPoints);

    private static HostileStatusEffectDefinition Shackle(
        int skillId,
        uint statusId,
        int nativeOdds,
        int durationSeconds,
        int mana,
        int cooldownSeconds) =>
        Definition(
            skillId,
            Priest,
            statusId,
            nativeOdds,
            kind: 13,
            priority: 1,
            durationSeconds,
            cooldownSeconds,
            mana,
            HostileStatusTargetMode.SingleTarget,
            range: 7f,
            physicalReductionBasisPoints: 7_500,
            magicReductionBasisPoints: 7_500,
            control: FullActionControl);

    private static HostileStatusEffectDefinition Definition(
        int skillId,
        byte profession,
        uint statusId,
        int nativeOdds,
        int kind,
        int priority,
        int durationSeconds,
        int cooldownSeconds,
        int mana,
        HostileStatusTargetMode targetMode,
        float range,
        int physicalDefense = 0,
        int magicDefense = 0,
        int physicalReductionBasisPoints = 0,
        int magicReductionBasisPoints = 0,
        HostileStatusControlFlags control =
            HostileStatusControlFlags.None) =>
        new(
            skillId,
            profession,
            statusId,
            nativeOdds,
            kind,
            priority,
            TimeSpan.FromSeconds(durationSeconds),
            TimeSpan.FromSeconds(cooldownSeconds),
            mana,
            targetMode,
            range,
            HostileStatusApplicationTrigger.CommittedCast,
            PhysicalDefenseModifier: physicalDefense,
            MagicDefenseModifier: magicDefense,
            PhysicalDamageReductionBasisPoints:
                physicalReductionBasisPoints,
            MagicDamageReductionBasisPoints:
                magicReductionBasisPoints,
            Control: control);
}
