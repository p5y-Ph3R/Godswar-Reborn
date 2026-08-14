using System.Collections.Frozen;
using Godswar.Server.Application.World;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal static class MonsterCombatResolver
{
    internal const float DefaultPlayerBasicAttackRange = 2.5f;
    internal const int DamageReductionBasisPointScale = 10_000;
    internal const decimal MaximumCombinedDamageReduction = 1m;
    // A basic-attack request carries the client's final auto-approach position.
    // It can differ from the last Walk sample due to client interpolation, so
    // accept a bounded correction instead of testing reach from a stale point.
    internal const float MaximumBasicAttackPositionCorrection = 0.5f;

    public static uint CalculatePlayerBasicAttack(GameCharacter character)
    {
        var attacker = CombatCharacterStatsAdapter.FromCharacter(character);
        return AuthoredCombatV1.ResolveBasicAttackForOutcome(
            attacker,
            target: default,
            CombatHitOutcome.Normal).Damage;
    }

    public static CombatResolution ResolvePlayerBasicAttack(
        GameCharacter character,
        in CombatTargetStats target,
        ulong combatEventId,
        int targetOrder = 0)
    {
        ArgumentNullException.ThrowIfNull(character);
        var attacker = CombatCharacterStatsAdapter.FromCharacter(character);
        return AuthoredCombatV1.ResolveBasicAttack(
            attacker,
            target,
            combatEventId,
            targetOrder);
    }

    public static uint CalculateMonsterPhysicalAttack(
        uint tier,
        GameCharacter target,
        decimal receivedDamageReduction = 0m,
        int physicalDefenseBonus = 0)
    {
        var boundedTier = (int)Math.Clamp(tier, 1u, 10_000u);
        // Captures establish tier 1/2/3 base attacks of 24/27/31. Keep the
        // extrapolation isolated here until higher-tier combat data is captured.
        var baseAttack = 21 + (3 * boundedTier) + (boundedTier / 3);
        var stats = target.CalculatedStats ?? CharacterStats.FromCharacter(target);
        var effectivePhysicalDefense = Math.Clamp(
            (long)stats.PhysicalDefense + physicalDefenseBonus,
            0L,
            int.MaxValue);
        var damageAfterDefense = Math.Max(
            1L,
            baseAttack - effectivePhysicalDefense);
        // Runtime statuses use a decimal fraction while durable merge stats
        // are projected as basis points. Compose the two only after both have
        // been independently bounded, then cap the aggregate before applying
        // it to the post-defense damage. The final one-damage floor prevents
        // full reduction from becoming immunity.
        var runtimeReduction = Math.Clamp(
            receivedDamageReduction,
            0m,
            MaximumCombinedDamageReduction);
        var mergeReduction = Math.Clamp(
                stats.PhysicalDamageReduction,
                0,
                DamageReductionBasisPointScale) /
            (decimal)DamageReductionBasisPointScale;
        var boundedReduction = Math.Clamp(
            runtimeReduction + mergeReduction,
            0m,
            MaximumCombinedDamageReduction);
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

    public static float ResolvePlayerBasicAttackRange(
        CapturedMonsterSpawn target,
        MonsterCombatRangeCatalog ranges)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ranges);
        return ranges.Resolve(target.MapId, target.TemplateKey);
    }
}

/// <summary>
/// Process-pinned collision lookup built once during composition. Combat never
/// scans the full published monster-template catalog on the simulation path.
/// </summary>
internal sealed class MonsterCombatRangeCatalog
{
    private readonly FrozenDictionary<(short MapId, string TemplateKey), float>
        _byMapAndTemplate;
    private readonly FrozenDictionary<string, float> _byTemplate;

    private MonsterCombatRangeCatalog(
        FrozenDictionary<(short MapId, string TemplateKey), float>
            byMapAndTemplate,
        FrozenDictionary<string, float> byTemplate)
    {
        _byMapAndTemplate = byMapAndTemplate;
        _byTemplate = byTemplate;
    }

    public static MonsterCombatRangeCatalog Empty { get; } = Create(
        GameplayContentCatalog.Empty);

    public static MonsterCombatRangeCatalog Create(
        GameplayContentCatalog gameplay)
    {
        ArgumentNullException.ThrowIfNull(gameplay);
        var ranged = gameplay.MonsterTemplates
            .Where(static template => template.CollisionRange is > 0)
            .ToArray();
        var exactGroups = ranged
            .Where(static template => template.SourceMapId.HasValue)
            .GroupBy(static template =>
                (template.SourceMapId!.Value, template.TemplateKey))
            .ToArray();
        if (exactGroups.Any(static group =>
                group.Select(static template => template.CollisionRange)
                    .Distinct()
                    .Skip(1)
                    .Any()))
        {
            throw new InvalidDataException(
                "Published monster templates contain conflicting collision " +
                "ranges for the same map and template.");
        }

        var exact = exactGroups
            .ToFrozenDictionary(
                static group => group.Key,
                static group => group.First().CollisionRange!.Value);
        var fallback = ranged
            .GroupBy(
                static template => template.TemplateKey,
                StringComparer.Ordinal)
            .ToFrozenDictionary(
                static group => group.Key,
                static group => group.First().CollisionRange!.Value,
                StringComparer.Ordinal);
        return new MonsterCombatRangeCatalog(exact, fallback);
    }

    public float Resolve(short mapId, string templateKey)
    {
        if (string.IsNullOrWhiteSpace(templateKey))
        {
            return MonsterCombatResolver.DefaultPlayerBasicAttackRange;
        }

        return _byMapAndTemplate.TryGetValue(
                (mapId, templateKey),
                out var exact)
            ? exact
            : _byTemplate.TryGetValue(templateKey, out var fallback)
                ? fallback
                : MonsterCombatResolver.DefaultPlayerBasicAttackRange;
    }
}
