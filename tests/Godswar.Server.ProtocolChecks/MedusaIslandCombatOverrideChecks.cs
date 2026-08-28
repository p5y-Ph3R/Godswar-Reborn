using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaIslandCombatOverrideChecks
{
    public const string CheckName =
        "Medusa Island explicit combat-profile and final-damage overrides";

    public static Task RunAsync()
    {
        CheckApprovedAttackProfiles();
        CheckAttackProfilePurity();
        CheckAttackProfileFailures();
        CheckFinalDamageProfiles();
        CheckFinalDamageRounding();
        CheckFinalDamagePurity();
        CheckFinalDamageFailures();
        CheckExplicitTypedApiBoundary();
        return Task.CompletedTask;
    }

    private static void CheckApprovedAttackProfiles()
    {
        CheckDifficultyAttackProfiles(
            MedusaEncounterDifficulty.Normal,
            ordinaryPhysical: 6_000,
            ordinaryMagical: 5_000,
            utilityPhysical: 5_700,
            utilityMagical: 4_700,
            elitePhysical: 6_400,
            eliteMagical: 5_400,
            euryaleMagical: 9_000,
            chrysaorPhysical: 10_000,
            sthenoPhysical: 10_000,
            medusaMagical: 9_000);
        CheckDifficultyAttackProfiles(
            MedusaEncounterDifficulty.Enhanced,
            ordinaryPhysical: 6_300,
            ordinaryMagical: 5_300,
            utilityPhysical: 6_000,
            utilityMagical: 5_000,
            elitePhysical: 6_800,
            eliteMagical: 5_800,
            euryaleMagical: 11_000,
            chrysaorPhysical: 12_000,
            sthenoPhysical: 13_000,
            medusaMagical: 12_000);
        CheckDifficultyAttackProfiles(
            MedusaEncounterDifficulty.Mythic,
            ordinaryPhysical: 7_000,
            ordinaryMagical: 5_900,
            utilityPhysical: 6_600,
            utilityMagical: 5_500,
            elitePhysical: 7_600,
            eliteMagical: 6_500,
            euryaleMagical: 13_500,
            chrysaorPhysical: 14_500,
            sthenoPhysical: 16_000,
            medusaMagical: 15_000);
    }

    private static void CheckDifficultyAttackProfiles(
        MedusaEncounterDifficulty difficulty,
        int ordinaryPhysical,
        int ordinaryMagical,
        int utilityPhysical,
        int utilityMagical,
        int elitePhysical,
        int eliteMagical,
        int euryaleMagical,
        int chrysaorPhysical,
        int sthenoPhysical,
        int medusaMagical)
    {
        CheckTemplateChannelRole(
            difficulty,
            MedusaEncounterEnemyRole.Ordinary,
            ordinaryPhysical,
            ordinaryMagical);
        CheckTemplateChannelRole(
            difficulty,
            MedusaEncounterEnemyRole.UtilityCarrier,
            utilityPhysical,
            utilityMagical);
        CheckTemplateChannelRole(
            difficulty,
            MedusaEncounterEnemyRole.Elite,
            elitePhysical,
            eliteMagical);
        CheckForcedBossRole(
            difficulty,
            MedusaEncounterEnemyRole.Euryale,
            MonsterAttackDamageKind.Magical,
            euryaleMagical);
        CheckForcedBossRole(
            difficulty,
            MedusaEncounterEnemyRole.Chrysaor,
            MonsterAttackDamageKind.Physical,
            chrysaorPhysical);
        CheckForcedBossRole(
            difficulty,
            MedusaEncounterEnemyRole.Stheno,
            MonsterAttackDamageKind.Physical,
            sthenoPhysical);
        CheckForcedBossRole(
            difficulty,
            MedusaEncounterEnemyRole.Medusa,
            MonsterAttackDamageKind.Magical,
            medusaMagical);
    }

    private static void CheckTemplateChannelRole(
        MedusaEncounterDifficulty difficulty,
        MedusaEncounterEnemyRole role,
        int expectedPhysical,
        int expectedMagical)
    {
        var physicalSource = SourceProfile(
            MonsterAttackDamageKind.Physical);
        var physical = MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
            difficulty,
            role,
            physicalSource);
        Check.True(
            physical.AttackKind == MonsterAttackDamageKind.Physical &&
            physical.PhysicalAttack == expectedPhysical &&
            physical.MagicAttack == 0,
            $"{difficulty}/{role} selects its approved physical rating");
        CheckPreservedProfileFields(physicalSource, physical, difficulty, role);

        var magicalSource = SourceProfile(
            MonsterAttackDamageKind.Magical);
        var magical = MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
            difficulty,
            role,
            magicalSource);
        Check.True(
            magical.AttackKind == MonsterAttackDamageKind.Magical &&
            magical.PhysicalAttack == 0 &&
            magical.MagicAttack == expectedMagical,
            $"{difficulty}/{role} selects its approved magical rating");
        CheckPreservedProfileFields(magicalSource, magical, difficulty, role);
    }

    private static void CheckForcedBossRole(
        MedusaEncounterDifficulty difficulty,
        MedusaEncounterEnemyRole role,
        MonsterAttackDamageKind expectedKind,
        int expectedAttack)
    {
        var specialSource = SourceProfile(MonsterAttackDamageKind.Special);
        var adjusted = MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
            difficulty,
            role,
            specialSource);
        Check.True(
            adjusted.AttackKind == expectedKind &&
            adjusted.PhysicalAttack == (
                expectedKind == MonsterAttackDamageKind.Physical
                    ? expectedAttack
                    : 0) &&
            adjusted.MagicAttack == (
                expectedKind == MonsterAttackDamageKind.Magical
                    ? expectedAttack
                    : 0),
            $"{difficulty}/{role} forces its authored channel from type 3");
        CheckPreservedProfileFields(specialSource, adjusted, difficulty, role);
    }

    private static void CheckAttackProfilePurity()
    {
        var source = SourceProfile(MonsterAttackDamageKind.Magical);
        var snapshot = source;
        var first = MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
            MedusaEncounterDifficulty.Enhanced,
            MedusaEncounterEnemyRole.Elite,
            source);
        var second = MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
            MedusaEncounterDifficulty.Enhanced,
            MedusaEncounterEnemyRole.Elite,
            source);

        Check.Equal(snapshot, source, "attack override does not mutate input");
        Check.Equal(first, second, "attack override is deterministic");

        foreach (var difficulty in MedusaIslandEncounterPolicy.Difficulties)
        {
            foreach (var enemy in difficulty.Enemies)
            {
                int[] selectedRatings = enemy.Role switch
                {
                    MedusaEncounterEnemyRole.Euryale or
                    MedusaEncounterEnemyRole.Medusa =>
                        [enemy.AttackRatings.Magical],
                    MedusaEncounterEnemyRole.Chrysaor or
                    MedusaEncounterEnemyRole.Stheno =>
                        [enemy.AttackRatings.Physical],
                    _ => new[]
                    {
                        enemy.AttackRatings.Physical,
                        enemy.AttackRatings.Magical
                    }
                };
                Check.True(
                    selectedRatings.All(static rating => rating > 0),
                    $"{difficulty.Difficulty}/{enemy.Role} has no zero " +
                    "selectable attack rating");
            }
        }
    }

    private static void CheckAttackProfileFailures()
    {
        var physical = SourceProfile(MonsterAttackDamageKind.Physical);
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
                (MedusaEncounterDifficulty)byte.MaxValue,
                MedusaEncounterEnemyRole.Ordinary,
                physical),
            "unknown difficulty cannot borrow a combat profile");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
                MedusaEncounterDifficulty.Normal,
                (MedusaEncounterEnemyRole)byte.MaxValue,
                physical),
            "unknown enemy role cannot borrow a combat profile");

        var special = SourceProfile(MonsterAttackDamageKind.Special);
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
                MedusaEncounterDifficulty.Normal,
                MedusaEncounterEnemyRole.Ordinary,
                special),
            "type 3 cannot silently select a non-boss attack rating");
        var unknownChannel = SourceProfile(
            (MonsterAttackDamageKind)short.MaxValue);
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
                MedusaEncounterDifficulty.Mythic,
                MedusaEncounterEnemyRole.Elite,
                unknownChannel),
            "unknown template channel fails closed");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
                MedusaEncounterDifficulty.Mythic,
                MedusaEncounterEnemyRole.Medusa,
                unknownChannel),
            "a forced boss also rejects an unknown source channel");
    }

    private static MonsterCombatProfile SourceProfile(
        MonsterAttackDamageKind attackKind) => new(
            attackKind,
            CollisionRange: 4.25f,
            Level: 137,
            PhysicalAttack: 111,
            MagicAttack: 222,
            PhysicalDefense: 3_333,
            MagicDefense: 4_444,
            Hit: 5_555,
            Dodge: 6_666,
            Critical: 7_777,
            CriticalResistance: 8_888,
            IsElite: true,
            IsBoss: false);

    private static void CheckPreservedProfileFields(
        in MonsterCombatProfile source,
        in MonsterCombatProfile adjusted,
        MedusaEncounterDifficulty difficulty,
        MedusaEncounterEnemyRole role)
    {
        Check.True(
            adjusted.CollisionRange == source.CollisionRange &&
            adjusted.Level == source.Level &&
            adjusted.PhysicalDefense == source.PhysicalDefense &&
            adjusted.MagicDefense == source.MagicDefense &&
            adjusted.Hit == source.Hit &&
            adjusted.Dodge == source.Dodge &&
            adjusted.Critical == source.Critical &&
            adjusted.CriticalResistance == source.CriticalResistance &&
            adjusted.IsElite == source.IsElite &&
            adjusted.IsBoss == source.IsBoss,
            $"{difficulty}/{role} preserves every non-attack profile field");
    }
}
