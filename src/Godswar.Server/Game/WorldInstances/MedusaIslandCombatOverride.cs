using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Pure combat-profile composition for an explicitly identified Medusa Island
/// run. Runtime routing owns the difficulty and enemy role; this policy never
/// attempts to recover either value from shared map content.
/// </summary>
internal static partial class MedusaIslandCombatOverride
{
    internal static MonsterCombatProfile ApplyMonsterAttackProfile(
        MedusaEncounterDifficulty difficulty,
        MedusaEncounterEnemyRole role,
        in MonsterCombatProfile source)
    {
        var enemy = ResolveEnemyDefinition(difficulty, role);
        if (source.AttackKind is not (
                MonsterAttackDamageKind.Physical or
                MonsterAttackDamageKind.Magical or
                MonsterAttackDamageKind.Special))
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source.AttackKind,
                "Unknown monster-template attack channel.");
        }

        var attackKind = ResolveAttackKind(role, source.AttackKind);
        var attackRating = attackKind switch
        {
            MonsterAttackDamageKind.Physical =>
                enemy.AttackRatings.Physical,
            MonsterAttackDamageKind.Magical =>
                enemy.AttackRatings.Magical,
            _ => throw new ArgumentOutOfRangeException(
                nameof(source),
                attackKind,
                "Medusa Island attacks require a physical or magical channel.")
        };
        if (attackRating <= 0)
        {
            throw new InvalidOperationException(
                $"Medusa Island {difficulty}/{role} has no positive " +
                $"{attackKind} attack rating.");
        }

        return source with
        {
            AttackKind = attackKind,
            PhysicalAttack = attackKind == MonsterAttackDamageKind.Physical
                ? attackRating
                : 0,
            MagicAttack = attackKind == MonsterAttackDamageKind.Magical
                ? attackRating
                : 0
        };
    }

    private static MonsterAttackDamageKind ResolveAttackKind(
        MedusaEncounterEnemyRole role,
        MonsterAttackDamageKind sourceKind) => role switch
        {
            MedusaEncounterEnemyRole.Ordinary or
            MedusaEncounterEnemyRole.UtilityCarrier or
            MedusaEncounterEnemyRole.Elite => sourceKind switch
            {
                MonsterAttackDamageKind.Physical =>
                    MonsterAttackDamageKind.Physical,
                MonsterAttackDamageKind.Magical =>
                    MonsterAttackDamageKind.Magical,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(sourceKind),
                    sourceKind,
                    "A non-boss Medusa Island template must declare a " +
                    "physical or magical attack channel.")
            },
            MedusaEncounterEnemyRole.Euryale or
            MedusaEncounterEnemyRole.Medusa =>
                MonsterAttackDamageKind.Magical,
            MedusaEncounterEnemyRole.Chrysaor or
            MedusaEncounterEnemyRole.Stheno =>
                MonsterAttackDamageKind.Physical,
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Unknown Medusa Island enemy role.")
        };

    private static MedusaEncounterEnemyDefinition ResolveEnemyDefinition(
        MedusaEncounterDifficulty difficulty,
        MedusaEncounterEnemyRole role)
    {
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out var definition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(difficulty),
                difficulty,
                "Unknown Medusa Island difficulty.");
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Unknown Medusa Island enemy role.");
        }

        foreach (var enemy in definition.Enemies)
        {
            if (enemy.Role == role)
            {
                return enemy;
            }
        }

        throw new InvalidOperationException(
            $"Medusa Island {difficulty} has no authored {role} profile.");
    }
}
