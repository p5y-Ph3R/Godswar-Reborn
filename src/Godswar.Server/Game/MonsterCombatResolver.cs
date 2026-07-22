using Godswar.Server.State;

namespace Godswar.Server.Game;

internal static class MonsterCombatResolver
{
    internal const float DefaultPlayerBasicAttackRange = 2.5f;
    // A basic-attack request carries the client's final auto-approach position.
    // It can differ from the last Walk sample due to client interpolation, so
    // accept a bounded correction instead of testing reach from a stale point.
    internal const float MaximumBasicAttackPositionCorrection = 0.5f;

    public static uint CalculatePlayerBasicAttack(GameCharacter character)
    {
        var stats = character.CalculatedStats ?? CharacterStats.FromCharacter(character);
        var attack = character.Profession is 2 or 3
            ? stats.MagicAttack
            : stats.PhysicalAttack;
        return (uint)Math.Max(1, attack);
    }

    public static uint CalculateMonsterPhysicalAttack(
        uint tier,
        GameCharacter target,
        decimal receivedDamageReduction = 0m)
    {
        var boundedTier = (int)Math.Clamp(tier, 1u, 10_000u);
        // Captures establish tier 1/2/3 base attacks of 24/27/31. Keep the
        // extrapolation isolated here until higher-tier combat data is captured.
        var baseAttack = 21 + (3 * boundedTier) + (boundedTier / 3);
        var stats = target.CalculatedStats ?? CharacterStats.FromCharacter(target);
        var damageAfterDefense = Math.Max(1, baseAttack - Math.Max(0, stats.PhysicalDefense));
        var boundedReduction = Math.Clamp(receivedDamageReduction, 0m, 1m);
        var reducedDamage = decimal.ToInt32(decimal.Truncate(
            damageAfterDefense * (1m - boundedReduction)));
        return (uint)Math.Max(1, reducedDamage);
    }

    public static bool IsWithinBasicAttackRange(
        float attackerX,
        float attackerZ,
        float targetX,
        float targetZ,
        float attackRange = DefaultPlayerBasicAttackRange)
    {
        if (!float.IsFinite(attackerX) || !float.IsFinite(attackerZ) ||
            !float.IsFinite(targetX) || !float.IsFinite(targetZ))
        {
            return false;
        }

        var deltaX = (double)targetX - attackerX;
        var deltaZ = (double)targetZ - attackerZ;
        var boundedRange = Math.Max(0f, attackRange);
        return (deltaX * deltaX) + (deltaZ * deltaZ) <=
               boundedRange * boundedRange;
    }

    public static bool TryResolvePlayerBasicAttackPosition(
        float serverX,
        float serverZ,
        float reportedX,
        float reportedZ,
        out float resolvedX,
        out float resolvedZ)
    {
        resolvedX = serverX;
        resolvedZ = serverZ;
        if (!IsWithinBasicAttackRange(
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

    public static float ResolvePlayerBasicAttackRange(CapturedMonsterSpawn target)
    {
        var exact = MonsterTemplateSeeds.Monsters.FirstOrDefault(template =>
            template.SourceMapId == target.MapId &&
            string.Equals(template.TemplateKey, target.TemplateKey, StringComparison.Ordinal));
        if (exact.CollisionRange is > 0)
        {
            return exact.CollisionRange.Value;
        }

        var fallback = MonsterTemplateSeeds.Monsters.FirstOrDefault(template =>
            string.Equals(template.TemplateKey, target.TemplateKey, StringComparison.Ordinal));
        return fallback.CollisionRange is > 0
            ? fallback.CollisionRange.Value
            : DefaultPlayerBasicAttackRange;
    }
}
