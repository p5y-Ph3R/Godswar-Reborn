using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct ZodiacDefensiveSkillAdjustment(
    int FlatDamageReduction,
    int DamageReductionBasisPoints)
{
    public bool IsEmpty =>
        FlatDamageReduction == 0 && DamageReductionBasisPoints == 0;
}

/// <summary>
/// Projects the shipped defensive Zodiac rows onto one matching incoming
/// player-skill family. Basic attacks and skill families not selected by the
/// defender never enter this policy.
/// </summary>
internal static class ZodiacDefensiveSkillProjection
{
    internal const int FlatTrainingFirstGrid = 8;
    internal const int FlatTrainingLastGrid = 11;
    internal const int PercentageTrainingFirstGrid = 12;
    internal const int PercentageTrainingLastGrid = 15;
    internal const int BasisPointScale = 10_000;

    public static ZodiacDefensiveSkillAdjustment ResolveAdjustment(
        GameCharacter defender,
        uint runtimeSkillId)
    {
        ArgumentNullException.ThrowIfNull(defender);
        if (runtimeSkillId > int.MaxValue)
        {
            return default;
        }

        var skillId = checked((int)runtimeSkillId);
        lock (defender.ZodiacSync)
        {
            var levels = defender.ZodiacSkillGridLevels;
            var selections = defender.ZodiacSkillGridSkillIds;
            if (levels is null ||
                selections is null ||
                levels.Length != ZodiacSkillGridCatalog.GridCount ||
                selections.Length != ZodiacSkillGridCatalog.GridCount ||
                !TryValidateDefensiveRows(
                    defender.Profession,
                    levels,
                    selections))
            {
                return default;
            }

            var flatReduction = ResolveGreatestMatchingEffect(
                skillId,
                levels,
                selections,
                FlatTrainingFirstGrid,
                FlatTrainingLastGrid,
                static level => checked(level * 100));
            var percentageReduction = ResolveGreatestMatchingEffect(
                skillId,
                levels,
                selections,
                PercentageTrainingFirstGrid,
                PercentageTrainingLastGrid,
                ResolvePercentageBasisPoints);
            return new ZodiacDefensiveSkillAdjustment(
                flatReduction,
                percentageReduction);
        }
    }

    public static CombatResolution ResolvePvpSkillDamage(
        GameCharacter defender,
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        in PlayerCombatSkillSnapshot skill,
        ulong eventId,
        int targetOrder = 0)
    {
        var adjustment = ResolveAdjustment(defender, skill.SkillId);
        if (adjustment.IsEmpty)
        {
            return PlayerCombatRules.ResolvePvpSkillDamage(
                attacker,
                target,
                skill,
                eventId,
                targetOrder);
        }

        var resolution = PlayerCombatRules.ResolvePvpSkillDamage(
            attacker,
            target,
            skill,
            eventId,
            targetOrder);
        return ProjectResolvedDamage(resolution, adjustment);
    }

    internal static int ResolvePercentageBasisPoints(int level)
    {
        var bounded = Math.Clamp(
            level,
            0,
            ZodiacSkillGridCatalog.MaximumGridLevel);
        return bounded switch
        {
            0 => 0,
            <= 10 => checked(bounded * 200),
            <= 15 => checked(2_000 + ((bounded - 10) * 300)),
            <= 20 => checked(3_500 + ((bounded - 15) * 500)),
            _ => checked(6_000 + ((bounded - 20) * 200))
        };
    }

    internal static CombatResolution ProjectResolvedDamage(
        in CombatResolution resolution,
        in ZodiacDefensiveSkillAdjustment adjustment)
    {
        if (resolution.Damage == 0)
        {
            return resolution;
        }

        var afterFlatReduction = Math.Max(
            0m,
            (decimal)resolution.Damage -
            Math.Max(0, adjustment.FlatDamageReduction));
        var reduction = Math.Clamp(
            adjustment.DamageReductionBasisPoints,
            0,
            BasisPointScale);
        var projected = (uint)((afterFlatReduction *
            (BasisPointScale - reduction)) / BasisPointScale);
        return resolution with { Damage = projected };
    }

    private static bool TryValidateDefensiveRows(
        byte profession,
        IReadOnlyList<int> levels,
        IReadOnlyList<int> selections)
    {
        for (var grid = FlatTrainingFirstGrid;
             grid <= PercentageTrainingLastGrid;
             grid++)
        {
            var level = levels[grid];
            var selectedKind = selections[grid];
            if (level is < 0 or > ZodiacSkillGridCatalog.MaximumGridLevel ||
                level == 0 &&
                    selectedKind != ZodiacSkillGridCatalog.NoSelectedSkill ||
                !ZodiacSkillGridSelectionCatalog.IsAllowedForGrid(
                    grid,
                    selectedKind) ||
                !ZodiacSkillGridSelectionCatalog.IsAllowedForCharacter(
                    profession,
                    grid,
                    selectedKind))
            {
                return false;
            }

            if (selectedKind == ZodiacSkillGridCatalog.NoSelectedSkill)
            {
                continue;
            }

            var rowStart = ZodiacSkillGridSelectionCatalog.RowStart(grid);
            for (var candidate = rowStart; candidate < grid; candidate++)
            {
                if (selections[candidate] == selectedKind)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int ResolveGreatestMatchingEffect(
        int runtimeSkillId,
        IReadOnlyList<int> levels,
        IReadOnlyList<int> selections,
        int firstGrid,
        int lastGrid,
        Func<int, int> resolveEffect)
    {
        var greatest = 0;
        for (var grid = firstGrid; grid <= lastGrid; grid++)
        {
            var selectedKind = selections[grid];
            if (!ZodiacSkillGridSelectionCatalog.IsRuntimeSkillInFamily(
                    selectedKind,
                    runtimeSkillId))
            {
                continue;
            }

            greatest = Math.Max(
                greatest,
                resolveEffect(levels[grid]));
        }

        // Rows were validated before resolution; max keeps the projection
        // deterministic without depending on scan order.
        return greatest;
    }
}
