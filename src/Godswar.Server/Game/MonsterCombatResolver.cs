using Godswar.Server.State;

namespace Godswar.Server.Game;

internal static class MonsterCombatResolver
{
    internal const float DefaultPlayerBasicAttackRange = 2.5f;

    public static uint CalculatePlayerBasicAttack(GameCharacter character)
    {
        var stats = character.CalculatedStats ?? CharacterStats.FromCharacter(character);
        var attack = character.Profession is 2 or 3
            ? stats.MagicAttack
            : stats.PhysicalAttack;
        return (uint)Math.Max(1, attack);
    }

    public static uint CalculateMonsterPhysicalAttack(uint tier, GameCharacter target)
    {
        var boundedTier = (int)Math.Clamp(tier, 1u, 10_000u);
        // Captures establish tier 1/2/3 base attacks of 24/27/31. Keep the
        // extrapolation isolated here until higher-tier combat data is captured.
        var baseAttack = 21 + (3 * boundedTier) + (boundedTier / 3);
        var stats = target.CalculatedStats ?? CharacterStats.FromCharacter(target);
        return (uint)Math.Max(1, baseAttack - Math.Max(0, stats.PhysicalDefense));
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
        return (deltaX * deltaX) + (deltaZ * deltaZ) <
               boundedRange * boundedRange;
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
