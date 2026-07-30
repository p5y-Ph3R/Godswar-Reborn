namespace Godswar.Server.State;

internal enum ZodiacSkillGridSelectionStatus : byte
{
    Succeeded = 1,
    InactiveGrid,
    SkillKindNotAllowedForGrid,
    SkillKindNotAllowedForClass,
    SkillNotLearned,
    DuplicateSkillInRow,
    AlreadySelected
}

internal sealed record ZodiacSkillGridSelectionResult(
    ZodiacSkillGridSelectionStatus Status,
    int GridIndex,
    byte CurrentLevel,
    int PreviousSkillKind,
    int SelectedSkillKind)
{
    public bool Committed =>
        Status == ZodiacSkillGridSelectionStatus.Succeeded;
}

internal static class ZodiacSkillGridSelectionCatalog
{
    public const int ClearSelection = -1;
    public const int MinimumSkillKind = 10_000;
    public const int MaximumSkillKind = 29_999;
    public const int GridsPerRow = 4;

    // Exact Kind/Job pairs shipped in MagicMini.xml. Kind identifies a
    // five-rank Magic.ini family; it is not the runtime skill ID.
    private static readonly IReadOnlyDictionary<int, byte> SkillClasses =
        new Dictionary<int, byte>
        {
            [20_010] = 0, [20_001] = 0, [20_002] = 0,
            [10_003] = 0, [10_004] = 0, [10_005] = 0,
            [10_006] = 0,
            [10_025] = 1, [10_026] = 1, [10_027] = 1,
            [20_028] = 1, [20_029] = 1, [10_030] = 1,
            [10_031] = 1, [20_032] = 1, [20_033] = 1,
            [10_080] = 2, [10_081] = 2, [20_082] = 2,
            [20_083] = 2, [20_084] = 2, [10_085] = 2,
            [10_086] = 2, [10_075] = 2, [10_076] = 2,
            [10_050] = 3, [10_051] = 3, [10_052] = 3,
            [20_053] = 3, [20_054] = 3, [20_055] = 3,
            [20_056] = 3, [10_057] = 3, [20_058] = 3
        };

    public static bool IsValidIntentSkillKind(int skillKind) =>
        skillKind == ClearSelection ||
        skillKind is >= MinimumSkillKind and <= MaximumSkillKind;

    public static bool IsAllowedForGrid(int gridIndex, int skillKind)
    {
        if (!ZodiacSkillGridCatalog.IsValidGrid(gridIndex) ||
            skillKind == ClearSelection)
        {
            return ZodiacSkillGridCatalog.IsValidGrid(gridIndex);
        }

        var kindGroup = skillKind / 10_000;
        var expectedGroup = gridIndex % 8 < GridsPerRow ? 1 : 2;
        return kindGroup == expectedGroup;
    }

    public static bool IsAllowedForClass(
        byte profession,
        int skillKind) =>
        skillKind == ClearSelection ||
        SkillClasses.TryGetValue(skillKind, out var requiredClass) &&
        requiredClass == profession;

    public static int SkillFamilyFirstRuntimeId(int skillKind)
    {
        if (skillKind == ClearSelection ||
            !SkillClasses.ContainsKey(skillKind))
        {
            throw new ArgumentOutOfRangeException(nameof(skillKind));
        }

        return checked((skillKind % 10_000) * 10);
    }

    public static bool IsRuntimeSkillInFamily(
        int skillKind,
        int runtimeSkillId)
    {
        if (skillKind == ClearSelection ||
            !SkillClasses.ContainsKey(skillKind))
        {
            return false;
        }

        var first = SkillFamilyFirstRuntimeId(skillKind);
        return runtimeSkillId >= first && runtimeSkillId <= first + 4;
    }

    public static int RowStart(int gridIndex)
    {
        if (!ZodiacSkillGridCatalog.IsValidGrid(gridIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(gridIndex));
        }

        return (gridIndex / GridsPerRow) * GridsPerRow;
    }
}

internal static class ZodiacSkillGridSelection
{
    public static ZodiacSkillGridSelectionResult Apply(
        GameCharacter character,
        int gridIndex,
        int selectedSkillKind,
        bool skillLearned)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (!ZodiacSkillGridCatalog.IsValidGrid(gridIndex) ||
            !ZodiacSkillGridSelectionCatalog.IsValidIntentSkillKind(
                selectedSkillKind))
        {
            throw new ArgumentOutOfRangeException(
                !ZodiacSkillGridCatalog.IsValidGrid(gridIndex)
                    ? nameof(gridIndex)
                    : nameof(selectedSkillKind));
        }

        var level = ZodiacSkillGridCatalog.GetLevel(character, gridIndex);
        var previous = ZodiacSkillGridCatalog.GetSelectedSkillId(
            character,
            gridIndex);
        if (level == 0)
        {
            return Result(
                ZodiacSkillGridSelectionStatus.InactiveGrid);
        }

        if (!ZodiacSkillGridSelectionCatalog.IsAllowedForGrid(
                gridIndex,
                selectedSkillKind))
        {
            return Result(
                ZodiacSkillGridSelectionStatus
                    .SkillKindNotAllowedForGrid);
        }

        if (!ZodiacSkillGridSelectionCatalog.IsAllowedForClass(
                character.Profession,
                selectedSkillKind))
        {
            return Result(
                ZodiacSkillGridSelectionStatus
                    .SkillKindNotAllowedForClass);
        }

        if (selectedSkillKind !=
                ZodiacSkillGridSelectionCatalog.ClearSelection &&
            !skillLearned)
        {
            return Result(
                ZodiacSkillGridSelectionStatus.SkillNotLearned);
        }

        if (previous == selectedSkillKind)
        {
            return Result(
                ZodiacSkillGridSelectionStatus.AlreadySelected);
        }

        if (selectedSkillKind !=
            ZodiacSkillGridSelectionCatalog.ClearSelection)
        {
            var rowStart =
                ZodiacSkillGridSelectionCatalog.RowStart(gridIndex);
            for (var candidate = rowStart;
                 candidate <
                    rowStart +
                    ZodiacSkillGridSelectionCatalog.GridsPerRow;
                 candidate++)
            {
                if (candidate != gridIndex &&
                    ZodiacSkillGridCatalog.GetSelectedSkillId(
                        character,
                        candidate) == selectedSkillKind)
                {
                    return Result(
                        ZodiacSkillGridSelectionStatus
                            .DuplicateSkillInRow);
                }
            }
        }

        character.ZodiacSkillGridLevels =
            ZodiacSkillGridActivation.NormalizeLevels(
                character.ZodiacSkillGridLevels);
        character.ZodiacSkillGridSkillIds =
            ZodiacSkillGridActivation.NormalizeSkillIds(
                character.ZodiacSkillGridSkillIds);
        character.ZodiacSkillGridSkillIds[gridIndex] =
            selectedSkillKind;
        return Result(ZodiacSkillGridSelectionStatus.Succeeded);

        ZodiacSkillGridSelectionResult Result(
            ZodiacSkillGridSelectionStatus status) =>
            new(
                status,
                gridIndex,
                level,
                previous,
                status == ZodiacSkillGridSelectionStatus.Succeeded
                    ? selectedSkillKind
                    : previous);
    }
}
