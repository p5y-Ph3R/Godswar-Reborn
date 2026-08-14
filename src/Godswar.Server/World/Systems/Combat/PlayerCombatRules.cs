using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Scalar combat rules kept in parity with MonsterCombatResolver and
/// SkillCombatResolver without retaining a mutable character or skill DTO.
/// </summary>
internal static class PlayerCombatRules
{
    public const float DefaultBasicAttackRange = 1.7f;
    public const float MaximumBasicAttackPositionCorrection = 0.5f;
    public const float TargetCollisionAllowance = 3f;

    public static readonly TimeSpan BasicAttackCooldown =
        TimeSpan.FromMilliseconds(1475);

    public static TimeSpan ResolveBasicAttackCooldown(
        int attackIntervalMilliseconds)
    {
        const int schedulingAllowanceMilliseconds = 25;
        var boundedInterval = Math.Clamp(
            attackIntervalMilliseconds,
            250,
            ushort.MaxValue);
        return TimeSpan.FromMilliseconds(
            Math.Max(
                1,
                boundedInterval - schedulingAllowanceMilliseconds));
    }

    public static float ResolveBasicAttackRange(float authoredRange) =>
        float.IsFinite(authoredRange)
            ? Math.Clamp(authoredRange, 0.5f, 10f)
            : DefaultBasicAttackRange;

    public static uint CalculateBasicAttack(
        in PlayerCombatOffenseComponent offense)
    {
        var attacker = CombatCharacterStatsAdapter.FromOffense(offense);
        return AuthoredCombatV1.ResolveBasicAttackForOutcome(
            attacker,
            target: default,
            CombatHitOutcome.Normal).Damage;
    }

    public static uint CalculateSkillDamage(
        in PlayerCombatOffenseComponent offense,
        in PlayerCombatSkillSnapshot skill)
    {
        var attacker = CombatCharacterStatsAdapter.FromOffense(offense);
        return AuthoredCombatV1.ResolveSkillDamageForOutcome(
            attacker,
            target: default,
            skill.Property,
            skill.Power1,
            skill.Power2,
            CombatHitOutcome.Normal).Damage;
    }

    public static CombatResolution ResolveBasicAttack(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        ulong eventId,
        int targetOrder = 0) =>
        AuthoredCombatV1.ResolveBasicAttack(
            attacker,
            target,
            eventId,
            targetOrder);

    public static CombatResolution ResolveSkillDamage(
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        in PlayerCombatSkillSnapshot skill,
        ulong eventId,
        int targetOrder = 0) =>
        AuthoredCombatV1.ResolveSkillDamage(
            attacker,
            target,
            skill.Property,
            skill.Power1,
            skill.Power2,
            eventId,
            targetOrder);

    public static bool IsHostileSingleTargetSkill(
        in PlayerCombatSkillSnapshot skill) =>
        skill.Target == 44 &&
        skill.AffectObject == 28 &&
        skill.AreaRadius <= 0f;

    public static bool IsHostileAreaSkill(
        in PlayerCombatSkillSnapshot skill)
    {
        return IsHostileSelfAreaSkill(skill) ||
               IsHostileGroundAreaSkill(skill);
    }

    public static bool IsHostileSelfAreaSkill(
        in PlayerCombatSkillSnapshot skill)
    {
        const int targetSelf = 1;
        const int targetMonster = 8;
        const int targetPosition = 16;
        return (skill.Target & targetSelf) != 0 &&
               (skill.Target & targetPosition) == 0 &&
               (skill.AffectObject & targetMonster) != 0 &&
               skill.AreaRadius > 0f;
    }

    public static bool IsHostileGroundAreaSkill(
        in PlayerCombatSkillSnapshot skill)
    {
        const int targetPosition = 16;
        const int targetMonster = 8;
        return (skill.Target & targetPosition) != 0 &&
               (skill.AffectObject & targetMonster) != 0 &&
               skill.Distance > 0f &&
               skill.AreaRadius > 0f;
    }

    public static bool TryResolveBasicAttackPosition(
        float serverX,
        float serverZ,
        float reportedX,
        float reportedZ,
        out float resolvedX,
        out float resolvedZ)
    {
        resolvedX = serverX;
        resolvedZ = serverZ;
        if (!IsWithinInclusiveRange(
                serverX,
                serverZ,
                reportedX,
                reportedZ,
                MaximumBasicAttackPositionCorrection))
        {
            return false;
        }

        resolvedX = reportedX;
        resolvedZ = reportedZ;
        return true;
    }

    public static bool IsWithinBasicAttackRange(
        float attackerX,
        float attackerZ,
        float targetX,
        float targetZ,
        float attackRange = DefaultBasicAttackRange) =>
        IsWithinInclusiveRange(
            attackerX,
            attackerZ,
            targetX,
            targetZ,
            attackRange);

    public static bool IsWithinSkillRange(
        float casterX,
        float casterZ,
        float targetX,
        float targetZ,
        in PlayerCombatSkillSnapshot skill) =>
        IsWithinInclusiveRange(
            casterX,
            casterZ,
            targetX,
            targetZ,
            Math.Max(0f, skill.Distance) + TargetCollisionAllowance);

    public static bool IsWithinGroundTargetRange(
        float casterX,
        float casterZ,
        float targetX,
        float targetZ,
        in PlayerCombatSkillSnapshot skill) =>
        IsWithinInclusiveRange(
            casterX,
            casterZ,
            targetX,
            targetZ,
            Math.Max(0f, skill.Distance));

    public static bool IsWithinArea(
        float centerX,
        float centerZ,
        float targetX,
        float targetZ,
        float radius)
    {
        if (!HasFiniteCoordinates(centerX, centerZ, targetX, targetZ) ||
            !float.IsFinite(radius) ||
            radius <= 0f)
        {
            return false;
        }

        var deltaX = (double)targetX - centerX;
        var deltaZ = (double)targetZ - centerZ;
        return (deltaX * deltaX) + (deltaZ * deltaZ) <
               (double)radius * radius;
    }

    private static bool IsWithinInclusiveRange(
        float sourceX,
        float sourceZ,
        float targetX,
        float targetZ,
        float range)
    {
        if (!HasFiniteCoordinates(sourceX, sourceZ, targetX, targetZ) ||
            !float.IsFinite(range))
        {
            return false;
        }

        var deltaX = (double)targetX - sourceX;
        var deltaZ = (double)targetZ - sourceZ;
        var boundedRange = Math.Max(0f, range);
        return (deltaX * deltaX) + (deltaZ * deltaZ) <=
               boundedRange * boundedRange;
    }

    private static bool HasFiniteCoordinates(
        float firstX,
        float firstZ,
        float secondX,
        float secondZ) =>
        float.IsFinite(firstX) &&
        float.IsFinite(firstZ) &&
        float.IsFinite(secondX) &&
        float.IsFinite(secondZ);
}
