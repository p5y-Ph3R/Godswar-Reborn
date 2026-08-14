using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

/// <summary>
/// Adapts authored monster profiles and runtime status mitigation to the
/// shared typed combat seam, including deterministic hit and critical rolls.
/// Attack type selects the physical or magical channel before mitigation.
/// </summary>
internal static class MonsterIncomingCombatPolicy
{
    public static CombatResolution ResolveAttack(
        in MonsterCombatProfile monster,
        GameCharacter target,
        in RuntimeIncomingDamageMitigation runtimeMitigation,
        ulong combatEventId)
    {
        ArgumentNullException.ThrowIfNull(target);
        var attacker = new CombatAttackerStats
        {
            Level = monster.Level,
            Profession = monster.AttackKind ==
                MonsterAttackDamageKind.Magical
                    ? (byte)2
                    : (byte)0,
            PhysicalAttack = monster.PhysicalAttack,
            MagicAttack = monster.MagicAttack,
            Hit = monster.Hit,
            Critical = monster.Critical
        };
        var targetStats = ResolveTargetStats(
            target,
            runtimeMitigation);
        return AuthoredCombatV1.ResolveBasicAttack(
            attacker,
            targetStats,
            combatEventId);
    }

    public static CombatTargetStats ResolveTargetStats(
        GameCharacter target,
        in RuntimeIncomingDamageMitigation runtimeMitigation)
    {
        ArgumentNullException.ThrowIfNull(target);
        var stats = target.CalculatedStats ??
                    CharacterStats.FromCharacter(target);
        return CombatCharacterStatsAdapter.ToTarget(
            target.Level,
            stats,
            runtimeMitigation.PhysicalDefenseBonus,
            runtimeMitigation.MagicDefenseBonus,
            ToBasisPoints(runtimeMitigation.PhysicalDamageReduction),
            ToBasisPoints(runtimeMitigation.MagicDamageReduction));
    }

    private static int ToBasisPoints(decimal fraction) =>
        decimal.ToInt32(decimal.Round(
            Math.Clamp(fraction, 0m, 1m) * 10_000m,
            0,
            MidpointRounding.AwayFromZero));
}
