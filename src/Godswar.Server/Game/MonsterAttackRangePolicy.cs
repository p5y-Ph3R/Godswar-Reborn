using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

internal static class MonsterAttackRangePolicy
{
    public const float MeleeRange = 3f;
    public const float RangedRange = 9f;

    public static float Resolve(
        in MonsterCombatProfile profile,
        CapturedMonsterSpawn definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // AttackType primarily identifies the damage channel. The stock
        // Gorgon Archer is authored as physical despite using a bow.
        var isRangedRole = definition.DisplayName.Contains(
            "Archer",
            StringComparison.OrdinalIgnoreCase);
        var authoredReach =
            profile.AttackKind != MonsterAttackDamageKind.Physical ||
            isRangedRole
            ? RangedRange
            : MeleeRange;
        // The client stops center-to-center movement at the model's authored
        // collision range. A smaller server reach leaves large melee models
        // visibly touching the player while endlessly trying to close a gap
        // the client cannot represent.
        return Math.Max(authoredReach, profile.CollisionRange);
    }
}
