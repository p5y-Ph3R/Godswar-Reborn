using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaIslandCombatOverrideChecks
{
    private static void CheckFinalDamageProfiles()
    {
        foreach (var difficulty in Enum.GetValues<
                     MedusaEncounterDifficulty>())
        {
            CheckUnchangedDamage(
                difficulty,
                MedusaEncounterEnemyRole.Stheno,
                CombatDamageChannel.Physical,
                uint.MaxValue,
                $"{difficulty} Stheno accepts full physical damage");
            CheckAdjustedDamage(
                difficulty,
                MedusaEncounterEnemyRole.Stheno,
                CombatDamageChannel.Magic,
                sourceDamage: 12_345,
                expectedDamage: 1_235);
            CheckUnchangedDamage(
                difficulty,
                MedusaEncounterEnemyRole.Medusa,
                CombatDamageChannel.Magic,
                uint.MaxValue,
                $"{difficulty} Medusa accepts full magical damage");
            CheckAdjustedDamage(
                difficulty,
                MedusaEncounterEnemyRole.Medusa,
                CombatDamageChannel.Physical,
                sourceDamage: 12_345,
                expectedDamage: 1_235);

            foreach (var role in new[]
                     {
                         MedusaEncounterEnemyRole.Ordinary,
                         MedusaEncounterEnemyRole.UtilityCarrier,
                         MedusaEncounterEnemyRole.Elite,
                         MedusaEncounterEnemyRole.Euryale,
                         MedusaEncounterEnemyRole.Chrysaor
                     })
            {
                CheckUnchangedDamage(
                    difficulty,
                    role,
                    CombatDamageChannel.Physical,
                    777,
                    $"{difficulty}/{role} has no final physical multiplier");
                CheckUnchangedDamage(
                    difficulty,
                    role,
                    CombatDamageChannel.Magic,
                    777,
                    $"{difficulty}/{role} has no final magical multiplier");
            }
        }
    }

    private static void CheckFinalDamageRounding()
    {
        foreach (var (source, expected) in new (uint Source, uint Expected)[]
                 {
                     (1, 1),
                     (4, 1),
                     (5, 1),
                     (14, 1),
                     (15, 2),
                     (24, 2),
                     (25, 3),
                     (uint.MaxValue, 429_496_730)
                 })
        {
            CheckAdjustedDamage(
                MedusaEncounterDifficulty.Mythic,
                MedusaEncounterEnemyRole.Stheno,
                CombatDamageChannel.Magic,
                source,
                expected);
        }

        var zero = Resolution(
            CombatDamageChannel.Physical,
            CombatHitOutcome.Normal,
            damage: 0);
        var adjustedZero = MedusaIslandCombatOverride
            .ApplyFinalIncomingDamage(
                MedusaEncounterDifficulty.Normal,
                MedusaEncounterEnemyRole.Medusa,
                zero);
        Check.Equal(zero, adjustedZero, "zero damage remains exactly unchanged");

        var miss = Resolution(
            CombatDamageChannel.Physical,
            CombatHitOutcome.Miss,
            uint.MaxValue);
        var adjustedMiss = MedusaIslandCombatOverride
            .ApplyFinalIncomingDamage(
                MedusaEncounterDifficulty.Enhanced,
                MedusaEncounterEnemyRole.Medusa,
                miss);
        Check.Equal(miss, adjustedMiss, "miss damage remains exactly unchanged");
    }

    private static void CheckFinalDamagePurity()
    {
        var source = Resolution(
            CombatDamageChannel.Physical,
            CombatHitOutcome.Critical,
            9_995);
        var snapshot = source;
        var first = MedusaIslandCombatOverride.ApplyFinalIncomingDamage(
            MedusaEncounterDifficulty.Enhanced,
            MedusaEncounterEnemyRole.Medusa,
            source);
        var second = MedusaIslandCombatOverride.ApplyFinalIncomingDamage(
            MedusaEncounterDifficulty.Enhanced,
            MedusaEncounterEnemyRole.Medusa,
            source);

        Check.Equal(snapshot, source, "final damage override does not mutate input");
        Check.Equal(first, second, "final damage override is deterministic");
        Check.Equal(
            source with { Damage = 1_000 },
            first,
            "final damage override preserves every non-damage resolution field");
    }

    private static void CheckFinalDamageFailures()
    {
        var physical = Resolution(
            CombatDamageChannel.Physical,
            CombatHitOutcome.Normal,
            1_000);
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandCombatOverride.ApplyFinalIncomingDamage(
                (MedusaEncounterDifficulty)byte.MaxValue,
                MedusaEncounterEnemyRole.Medusa,
                physical),
            "unknown difficulty cannot borrow a final multiplier");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandCombatOverride.ApplyFinalIncomingDamage(
                MedusaEncounterDifficulty.Normal,
                (MedusaEncounterEnemyRole)byte.MaxValue,
                physical),
            "unknown role cannot borrow a final multiplier");

        var unknownChannel = Resolution(
            (CombatDamageChannel)byte.MaxValue,
            CombatHitOutcome.Normal,
            1_000);
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandCombatOverride.ApplyFinalIncomingDamage(
                MedusaEncounterDifficulty.Normal,
                MedusaEncounterEnemyRole.Stheno,
                unknownChannel),
            "final boss unknown damage channel fails closed");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandCombatOverride.ApplyFinalIncomingDamage(
                MedusaEncounterDifficulty.Normal,
                MedusaEncounterEnemyRole.Ordinary,
                unknownChannel),
            "non-final enemy unknown damage channel also fails closed");
    }

    private static void CheckExplicitTypedApiBoundary()
    {
        var exposed = typeof(MedusaIslandCombatOverride)
            .GetMethods(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .Where(static method => method.IsPublic || method.IsAssembly)
            .ToArray();
        Check.Equal(2, exposed.Length, "combat override exposes two pure seams");

        var attack = exposed.Single(method =>
            method.Name == nameof(
                MedusaIslandCombatOverride.ApplyMonsterAttackProfile));
        var attackParameters = attack.GetParameters();
        Check.True(
            attackParameters.Length == 3 &&
            attackParameters[0].ParameterType ==
                typeof(MedusaEncounterDifficulty) &&
            attackParameters[1].ParameterType ==
                typeof(MedusaEncounterEnemyRole) &&
            IsReadOnlyByReference(
                attackParameters[2],
                typeof(MonsterCombatProfile)),
            "attack seam requires explicit difficulty, role, and source profile");

        var damage = exposed.Single(method =>
            method.Name == nameof(
                MedusaIslandCombatOverride.ApplyFinalIncomingDamage));
        var damageParameters = damage.GetParameters();
        Check.True(
            damage.ReturnType == typeof(CombatResolution) &&
            damageParameters.Length == 3 &&
            damageParameters[0].ParameterType ==
                typeof(MedusaEncounterDifficulty) &&
            damageParameters[1].ParameterType ==
                typeof(MedusaEncounterEnemyRole) &&
            IsReadOnlyByReference(
                damageParameters[2],
                typeof(CombatResolution)),
            "final damage seam requires a typed CombatResolution");
        Check.True(
            exposed.SelectMany(static method => method.GetParameters())
                .All(static parameter =>
                    parameter.ParameterType != typeof(short) &&
                    parameter.ParameterType != typeof(uint) &&
                    parameter.ParameterType != typeof(MedusaDamageChannel)),
            "combat API exposes no map inference or channelless damage seam");
    }

    private static bool IsReadOnlyByReference(
        ParameterInfo parameter,
        Type elementType) =>
        parameter.ParameterType.IsByRef &&
        parameter.ParameterType.GetElementType() == elementType &&
        parameter.IsIn;

    private static void CheckAdjustedDamage(
        MedusaEncounterDifficulty difficulty,
        MedusaEncounterEnemyRole role,
        CombatDamageChannel channel,
        uint sourceDamage,
        uint expectedDamage)
    {
        var source = Resolution(
            channel,
            CombatHitOutcome.Normal,
            sourceDamage);
        var adjusted = MedusaIslandCombatOverride.ApplyFinalIncomingDamage(
            difficulty,
            role,
            source);
        Check.Equal(
            source with { Damage = expectedDamage },
            adjusted,
            $"{difficulty}/{role}/{channel} applies exact final damage");
    }

    private static void CheckUnchangedDamage(
        MedusaEncounterDifficulty difficulty,
        MedusaEncounterEnemyRole role,
        CombatDamageChannel channel,
        uint sourceDamage,
        string description)
    {
        var source = Resolution(
            channel,
            CombatHitOutcome.Critical,
            sourceDamage);
        var adjusted = MedusaIslandCombatOverride.ApplyFinalIncomingDamage(
            difficulty,
            role,
            source);
        Check.Equal(source, adjusted, description);
    }

    private static CombatResolution Resolution(
        CombatDamageChannel channel,
        CombatHitOutcome outcome,
        uint damage) => new(
            FormulaVersion: 23,
            EventId: ulong.MaxValue - 17,
            TargetOrder: 19,
            channel,
            outcome,
            damage,
            new CombatRollEvidence(8_765, 1_234, 4_321, 2_345),
            new CombatDamageEvidence(
                Attack: 9_001,
                EffectiveDefense: 2_002,
                AttackAfterDefense: 6_999m,
                SkillCoreDamage: 7_111m,
                DamageAfterTypedBonus: 7_222m,
                CriticalBonusDamage: 3_333m,
                DamageWithAppend: 10_666m,
                DamageAfterReduction: 8_777m,
                DamageAfterTakenIncrease: 9_888m,
                DamageAfterAbsorption: 9_995m));
}
