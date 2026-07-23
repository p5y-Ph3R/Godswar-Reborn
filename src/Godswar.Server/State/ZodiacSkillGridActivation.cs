namespace Godswar.Server.State;

internal enum ZodiacSkillGridActivationStatus
{
    Succeeded,
    InvalidGrid,
    AlreadyActive,
    InsufficientGold
}

internal sealed record ZodiacSkillGridActivationResult(
    ZodiacSkillGridActivationStatus Status,
    int GridIndex,
    int GoldCost,
    int CurrentGold,
    byte CurrentLevel,
    int SelectedSkillId)
{
    public bool Committed =>
        Status == ZodiacSkillGridActivationStatus.Succeeded;
}

internal static class ZodiacSkillGridCatalog
{
    public const int GridCount = 16;
    public const int MaximumGridLevel = 50;
    public const int NoSelectedSkill = -1;

    // Shipped SkillTrainConfig.lua UnlockG values, indexed by the zero-based
    // grid number sent by GameAPI:ConsEventRequest(0, 100, index, -1).
    private static readonly int[] ActivationGoldCosts =
    [
        0, 2_300, 7_200, 14_400,
        0, 2_300, 7_200, 14_400,
        0, 0, 920, 920,
        0, 0, 920, 920
    ];

    public static bool IsValidGrid(int gridIndex) =>
        gridIndex is >= 0 and < GridCount;

    public static int GetActivationGoldCost(int gridIndex)
    {
        if (!IsValidGrid(gridIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(gridIndex));
        }

        return ActivationGoldCosts[gridIndex];
    }

    public static byte GetLevel(GameCharacter character, int gridIndex)
    {
        ArgumentNullException.ThrowIfNull(character);
        return !IsValidGrid(gridIndex) ||
            character.ZodiacSkillGridLevels is null ||
            gridIndex >= character.ZodiacSkillGridLevels.Length
                ? (byte)0
                : checked((byte)Math.Clamp(
                    character.ZodiacSkillGridLevels[gridIndex],
                    0,
                    MaximumGridLevel));
    }

    public static int GetSelectedSkillId(
        GameCharacter character,
        int gridIndex)
    {
        ArgumentNullException.ThrowIfNull(character);
        return !IsValidGrid(gridIndex) ||
            character.ZodiacSkillGridSkillIds is null ||
            gridIndex >= character.ZodiacSkillGridSkillIds.Length
                ? NoSelectedSkill
                : character.ZodiacSkillGridSkillIds[gridIndex];
    }

    public static int[] CreateEmptyLevels() => new int[GridCount];

    public static int[] CreateEmptySkillIds() =>
        Enumerable.Repeat(NoSelectedSkill, GridCount).ToArray();

    public static int PackClientLevel(int gridIndex, byte level)
    {
        if (!IsValidGrid(gridIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(gridIndex));
        }

        if (level > MaximumGridLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        // The native record stores its zero-based row in the next byte and
        // the mutable level in the low byte. SID 100 writes only that low byte.
        return ((gridIndex / 4) << 8) | level;
    }
}

internal static class ZodiacSkillGridActivation
{
    public static ZodiacSkillGridActivationResult Apply(
        GameCharacter character,
        int gridIndex)
    {
        ArgumentNullException.ThrowIfNull(character);

        if (!ZodiacSkillGridCatalog.IsValidGrid(gridIndex))
        {
            return new ZodiacSkillGridActivationResult(
                ZodiacSkillGridActivationStatus.InvalidGrid,
                gridIndex,
                0,
                Math.Max(0, character.Gold),
                0,
                ZodiacSkillGridCatalog.NoSelectedSkill);
        }

        var currentLevel = ZodiacSkillGridCatalog.GetLevel(
            character,
            gridIndex);
        var selectedSkillId = ZodiacSkillGridCatalog.GetSelectedSkillId(
            character,
            gridIndex);
        var goldCost = ZodiacSkillGridCatalog.GetActivationGoldCost(gridIndex);
        var currentGold = Math.Max(0, character.Gold);
        if (currentLevel > 0)
        {
            return new ZodiacSkillGridActivationResult(
                ZodiacSkillGridActivationStatus.AlreadyActive,
                gridIndex,
                goldCost,
                currentGold,
                currentLevel,
                selectedSkillId);
        }

        if (currentGold < goldCost)
        {
            return new ZodiacSkillGridActivationResult(
                ZodiacSkillGridActivationStatus.InsufficientGold,
                gridIndex,
                goldCost,
                currentGold,
                currentLevel,
                selectedSkillId);
        }

        character.ZodiacSkillGridLevels =
            NormalizeLevels(character.ZodiacSkillGridLevels);
        character.ZodiacSkillGridSkillIds =
            NormalizeSkillIds(character.ZodiacSkillGridSkillIds);
        character.Gold = currentGold - goldCost;
        character.ZodiacSkillGridLevels[gridIndex] = 1;
        return new ZodiacSkillGridActivationResult(
            ZodiacSkillGridActivationStatus.Succeeded,
            gridIndex,
            goldCost,
            character.Gold,
            1,
            character.ZodiacSkillGridSkillIds[gridIndex]);
    }

    internal static int[] NormalizeLevels(int[]? levels)
    {
        var normalized = ZodiacSkillGridCatalog.CreateEmptyLevels();
        if (levels is null)
        {
            return normalized;
        }

        for (var index = 0;
             index < Math.Min(levels.Length, normalized.Length);
             index++)
        {
            normalized[index] = Math.Clamp(
                levels[index],
                0,
                ZodiacSkillGridCatalog.MaximumGridLevel);
        }

        return normalized;
    }

    internal static int[] NormalizeSkillIds(int[]? skillIds)
    {
        var normalized = ZodiacSkillGridCatalog.CreateEmptySkillIds();
        if (skillIds is null)
        {
            return normalized;
        }

        Array.Copy(
            skillIds,
            normalized,
            Math.Min(skillIds.Length, normalized.Length));
        return normalized;
    }
}
