namespace Godswar.Server.State;

internal enum ZodiacOffensiveSkillProjectionStatus : byte
{
    Unchanged = 0,
    Applied,
    InvalidState
}

internal readonly record struct ZodiacOffensiveSkillProjectionResult(
    ZodiacOffensiveSkillProjectionStatus Status,
    SkillCombatDefinition Skill,
    int FlatGridIndex,
    int FlatLevel,
    int FlatSkillKind,
    int PercentageGridIndex,
    int PercentageLevel,
    int PercentageManaEffectiveLevel,
    int PercentageSkillKind,
    int AdditionalMana)
{
    public bool Applied =>
        Status == ZodiacOffensiveSkillProjectionStatus.Applied;
}

/// <summary>
/// Projects the first two shipped Zodiac rows onto one selected five-rank
/// skill family. Type 1 adds fixed Power2 and MP. Type 2 adds to Power1 and
/// charges an additional percentage of authored MP, rounded up.
/// </summary>
internal static class ZodiacOffensiveSkillProjection
{
    internal const int FlatTrainingFirstGrid = 0;
    internal const int FlatTrainingLastGrid = 3;
    internal const int PercentageTrainingFirstGrid = 4;
    internal const int PercentageTrainingLastGrid = 7;
    internal const int MaximumPercentageManaEffectiveLevel = 45;

    // SkillTrainConfig.lua MP for Type=1, indexed by effective level - 1.
    private static readonly int[] FlatAdditionalMana =
    [
        2, 4, 6, 8, 10, 12, 14, 16, 18, 20,
        24, 28, 32, 36, 40, 44, 48, 52, 56, 60,
        62, 64, 66, 68, 70, 72, 74, 76, 78, 80,
        82, 84, 86, 88, 90, 92, 94, 96, 98, 100,
        102, 104, 106, 108, 110, 112, 114, 116, 118, 120
    ];

    // SkillTrainConfig.lua SkillEff for Type=2. The client displays these as
    // 2%..120%; Power1 is the matching authored combat coefficient field.
    private static readonly decimal[] PercentagePowerAdjustments =
    [
        0.02m, 0.04m, 0.06m, 0.08m, 0.10m,
        0.12m, 0.14m, 0.16m, 0.18m, 0.20m,
        0.23m, 0.26m, 0.29m, 0.32m, 0.35m,
        0.40m, 0.45m, 0.50m, 0.55m, 0.60m,
        0.62m, 0.64m, 0.66m, 0.68m, 0.70m,
        0.72m, 0.74m, 0.76m, 0.78m, 0.80m,
        0.82m, 0.84m, 0.86m, 0.88m, 0.90m,
        0.92m, 0.94m, 0.96m, 0.98m, 1.00m,
        1.02m, 1.04m, 1.06m, 1.08m, 1.10m,
        1.12m, 1.14m, 1.16m, 1.18m, 1.20m
    ];

    // Type=2 MP has only 45 shipped entries. Levels 46-50 keep their authored
    // SkillEff but cap only MP at level 45/300%. Entry 31 is the literal "210"
    // between 200% and 220%, so it is normalized as the obvious 210% typo.
    private static readonly int[] PercentageAdditionalMana =
    [
        5, 10, 15, 20, 25, 30, 35, 40, 45, 50,
        60, 70, 80, 90, 100, 110, 120, 130, 140, 150,
        155, 160, 165, 170, 175, 180, 185, 190, 195, 200,
        210, 220, 230, 240, 250, 255, 260, 265, 270, 275,
        280, 285, 290, 295, 300
    ];

    public static ZodiacOffensiveSkillProjectionResult Resolve(
        GameCharacter character,
        in SkillCombatDefinition authored)
    {
        ArgumentNullException.ThrowIfNull(character);
        lock (character.ZodiacSync)
        {
            var levels = character.ZodiacSkillGridLevels;
            var selections = character.ZodiacSkillGridSkillIds;
            if (levels is null ||
                selections is null ||
                levels.Length != ZodiacSkillGridCatalog.GridCount ||
                selections.Length != ZodiacSkillGridCatalog.GridCount ||
                !TryValidateOffensiveRows(
                    character.Profession,
                    levels,
                    selections))
            {
                return Invalid(authored);
            }

            var flatGrid = -1;
            var flatLevel = 0;
            var flatKind = ZodiacSkillGridCatalog.NoSelectedSkill;
            var percentageGrid = -1;
            var percentageLevel = 0;
            var percentageKind = ZodiacSkillGridCatalog.NoSelectedSkill;
            for (var grid = FlatTrainingFirstGrid;
                 grid <= PercentageTrainingLastGrid;
                 grid++)
            {
                var selectedKind = selections[grid];
                var requestedLevel = levels[grid];
                if (selectedKind == ZodiacSkillGridCatalog.NoSelectedSkill ||
                    requestedLevel == 0 ||
                    !ZodiacSkillGridSelectionCatalog.IsRuntimeSkillInFamily(
                        selectedKind,
                        authored.SkillId))
                {
                    continue;
                }

                if (grid <= FlatTrainingLastGrid)
                {
                    if (flatGrid >= 0)
                    {
                        return Invalid(authored);
                    }
                    flatGrid = grid;
                    flatLevel = requestedLevel;
                    flatKind = selectedKind;
                }
                else
                {
                    if (percentageGrid >= 0)
                    {
                        return Invalid(authored);
                    }
                    percentageGrid = grid;
                    percentageLevel = requestedLevel;
                    percentageKind = selectedKind;
                }
            }

            if (flatGrid < 0 && percentageGrid < 0)
            {
                return Unchanged(authored);
            }

            if (!TryProjectMatchedLevels(
                    authored,
                    flatLevel,
                    percentageLevel,
                    out var projected,
                    out var additionalMana,
                    out var percentageManaEffectiveLevel))
            {
                return Invalid(authored);
            }

            return new ZodiacOffensiveSkillProjectionResult(
                ZodiacOffensiveSkillProjectionStatus.Applied,
                projected,
                flatGrid,
                flatLevel,
                flatKind,
                percentageGrid,
                percentageLevel,
                percentageManaEffectiveLevel,
                percentageKind,
                additionalMana);
        }
    }

    private static bool TryValidateOffensiveRows(
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
                !ZodiacSkillGridSelectionCatalog.IsAllowedForClass(
                    profession,
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

    internal static bool TryProjectMatchedLevels(
        in SkillCombatDefinition authored,
        int flatLevel,
        int percentageLevel,
        out SkillCombatDefinition projected,
        out int additionalMana,
        out int percentageManaEffectiveLevel)
    {
        projected = authored;
        additionalMana = 0;
        percentageManaEffectiveLevel = 0;
        if (authored.Mp < 0 ||
            flatLevel is < 0 or > ZodiacSkillGridCatalog.MaximumGridLevel ||
            percentageLevel is < 0 or >
                ZodiacSkillGridCatalog.MaximumGridLevel)
        {
            return false;
        }

        percentageManaEffectiveLevel = Math.Min(
            percentageLevel,
            MaximumPercentageManaEffectiveLevel);
        try
        {
            var flatMana = flatLevel == 0
                ? 0
                : FlatAdditionalMana[flatLevel - 1];
            var percentageMana = percentageLevel == 0
                ? 0
                : ResolveRoundedUpAdditionalMana(
                    // The percentage is independently additional to the
                    // authored base; it does not compound Type-1 fixed MP.
                    authored.Mp,
                    PercentageAdditionalMana[
                        percentageManaEffectiveLevel - 1]);
            additionalMana = checked(flatMana + percentageMana);
            projected = authored with
            {
                Mp = checked(authored.Mp + additionalMana),
                Power1 = percentageLevel == 0
                    ? authored.Power1
                    : checked(
                        authored.Power1 +
                        PercentagePowerAdjustments[percentageLevel - 1]),
                Power2 = flatLevel == 0
                    ? authored.Power2
                    : checked(authored.Power2 + (flatLevel * 100m))
            };
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static int ResolveRoundedUpAdditionalMana(
        int baseMana,
        int additionalPercentage)
    {
        if (baseMana < 0 || additionalPercentage < 0)
        {
            throw new ArgumentOutOfRangeException(
                baseMana < 0 ? nameof(baseMana) : nameof(additionalPercentage));
        }

        var numerator = checked((long)baseMana * additionalPercentage);
        return checked((int)((numerator + 99L) / 100L));
    }

    private static ZodiacOffensiveSkillProjectionResult Unchanged(
        in SkillCombatDefinition authored) =>
        new(
            ZodiacOffensiveSkillProjectionStatus.Unchanged,
            authored,
            FlatGridIndex: -1,
            FlatLevel: 0,
            FlatSkillKind: ZodiacSkillGridCatalog.NoSelectedSkill,
            PercentageGridIndex: -1,
            PercentageLevel: 0,
            PercentageManaEffectiveLevel: 0,
            PercentageSkillKind: ZodiacSkillGridCatalog.NoSelectedSkill,
            AdditionalMana: 0);

    private static ZodiacOffensiveSkillProjectionResult Invalid(
        in SkillCombatDefinition authored) =>
        new(
            ZodiacOffensiveSkillProjectionStatus.InvalidState,
            authored,
            FlatGridIndex: -1,
            FlatLevel: 0,
            FlatSkillKind: ZodiacSkillGridCatalog.NoSelectedSkill,
            PercentageGridIndex: -1,
            PercentageLevel: 0,
            PercentageManaEffectiveLevel: 0,
            PercentageSkillKind: ZodiacSkillGridCatalog.NoSelectedSkill,
            AdditionalMana: 0);
}
