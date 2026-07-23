using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Scalar combat rules kept in parity with MonsterCombatResolver and
/// SkillCombatResolver without retaining a mutable character or skill DTO.
/// </summary>
internal static class PlayerCombatRules
{
    public const float DefaultBasicAttackRange = 2.5f;
    public const float MaximumBasicAttackPositionCorrection = 0.5f;
    public const float TargetCollisionAllowance = 3f;

    public static readonly TimeSpan BasicAttackCooldown =
        TimeSpan.FromMilliseconds(1475);

    public static uint CalculateBasicAttack(
        in PlayerCombatOffenseComponent offense)
    {
        var attack = offense.Profession is 2 or 3
            ? offense.MagicAttack
            : offense.PhysicalAttack;
        return (uint)Math.Max(1, attack);
    }

    public static uint CalculateSkillDamage(
        in PlayerCombatOffenseComponent offense,
        in PlayerCombatSkillSnapshot skill)
    {
        var usesMagicAttack = skill.Property == 1;
        var attack = usesMagicAttack
            ? offense.MagicAttack
            : offense.PhysicalAttack;
        var damageBonus = usesMagicAttack
            ? offense.MagicDamageBonus
            : offense.PhysicalDamageBonus;
        var appendDamage = usesMagicAttack
            ? offense.MagicAppendDamage
            : offense.PhysicalAppendDamage;

        var attackCoefficient = Math.Max(0m, 1m + skill.Power1);
        var rawDamage = (attackCoefficient * Math.Max(0, attack)) +
                        skill.Power2;
        rawDamage *= 1m + (Math.Max(0, damageBonus) / 10_000m);
        rawDamage += Math.Max(0, appendDamage);
        if (rawDamage <= 0)
        {
            return 0;
        }

        return (uint)Math.Clamp(
            decimal.ToInt64(decimal.Round(
                rawDamage,
                0,
                MidpointRounding.AwayFromZero)),
            1L,
            uint.MaxValue);
    }

    public static bool IsHostileSingleTargetSkill(
        in PlayerCombatSkillSnapshot skill) =>
        skill.Target == 44 &&
        skill.AffectObject == 28 &&
        skill.AreaRadius <= 0f;

    public static bool IsHostileAreaSkill(
        in PlayerCombatSkillSnapshot skill)
    {
        const int targetSelf = 1;
        const int targetMonster = 8;
        return (skill.Target & targetSelf) != 0 &&
               (skill.AffectObject & targetMonster) != 0 &&
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
