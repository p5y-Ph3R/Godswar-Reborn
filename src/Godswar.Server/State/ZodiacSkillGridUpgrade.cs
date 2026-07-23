namespace Godswar.Server.State;

internal readonly record struct ZodiacSkillGridUpgradeRequirement(
    byte CurrentLevel,
    byte NextLevel,
    byte RequiredZodiacLevel,
    int EnergyCost,
    int TalentPointCost);

internal enum ZodiacSkillGridUpgradeStatus
{
    Succeeded,
    InvalidGrid,
    InactiveGrid,
    MaximumLevelReached,
    ZodiacLevelTooLow,
    InsufficientEnergy,
    InsufficientTalentPoints
}

internal sealed record ZodiacSkillGridUpgradeResult(
    ZodiacSkillGridUpgradeStatus Status,
    int GridIndex,
    byte PreviousLevel,
    byte CurrentLevel,
    byte RequiredZodiacLevel,
    int EnergyCost,
    int TalentPointCost,
    int CurrentEnergy,
    int CurrentEnergyRemainderX100,
    int CurrentTalentPoints,
    int SelectedSkillId)
{
    public bool Committed =>
        Status == ZodiacSkillGridUpgradeStatus.Succeeded;
}

internal static class ZodiacSkillGridUpgradeCatalog
{
    // SkillTrainConfig.lua UpdateE[1..49], used for level 1 -> 2 through
    // level 49 -> 50. The table's final display value is not an upgrade.
    private static readonly int[] EnergyCosts =
    [
        5, 12, 17, 25, 30, 60, 119, 179, 238, 298,
        595, 893, 1_191, 1_489, 1_786, 2_382, 2_977, 3_575,
        4_170, 5_366, 5_996, 6_666, 7_386, 8_166, 9_016,
        9_946, 10_966, 12_086, 13_316, 14_546, 15_876,
        17_316, 18_876, 20_566, 22_496, 24_476, 26_616,
        28_926, 31_416, 34_096, 36_976, 40_066, 43_376,
        46_916, 50_696, 54_726, 59_016, 63_576, 68_416
    ];

    // SkillTrainConfig.lua UpdateS[1..49]. "S" is the spendable Talent
    // Point balance stored in character_base."SkillPoint".
    private static readonly int[] TalentPointCosts =
    [
        7, 15, 25, 32, 40, 263, 362, 523, 682, 920,
        955, 1_196, 1_434, 1_672, 1_786, 2_186, 2_583,
        2_982, 3_381, 4_040, 4_470, 4_950, 5_510, 6_190,
        7_040, 8_120, 9_500, 11_300, 13_060, 14_820,
        16_580, 18_340, 20_100, 21_860, 23_620, 25_380,
        27_140, 28_900, 30_660, 32_420, 34_180, 35_940,
        37_700, 39_460, 41_220, 42_980, 44_740, 46_500,
        48_260
    ];

    // SkillTrainConfig.lua Starlv[1..49]. This gates the current-grid
    // upgrade by authoritative Zodiac level, not character level.
    private static readonly byte[] RequiredZodiacLevels =
    [
        1, 2, 2, 3, 3, 4, 4, 5, 5, 6,
        6, 7, 7, 8, 8, 9, 9, 10, 10, 11,
        12, 13, 14, 15, 16, 17, 18, 19, 20, 20,
        21, 21, 22, 22, 23, 23, 24, 24, 25, 25,
        26, 26, 27, 27, 28, 28, 29, 29, 30
    ];

    public static bool TryGetRequirement(
        int currentGridLevel,
        out ZodiacSkillGridUpgradeRequirement requirement)
    {
        if (currentGridLevel is < 1 or > ZodiacSkillGridCatalog.MaximumGridLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentGridLevel),
                currentGridLevel,
                "An active Zodiac skill grid must be level 1 through 50.");
        }

        if (currentGridLevel == ZodiacSkillGridCatalog.MaximumGridLevel)
        {
            requirement = default;
            return false;
        }

        var index = currentGridLevel - 1;
        requirement = new ZodiacSkillGridUpgradeRequirement(
            checked((byte)currentGridLevel),
            checked((byte)(currentGridLevel + 1)),
            RequiredZodiacLevels[index],
            EnergyCosts[index],
            TalentPointCosts[index]);
        return true;
    }
}

internal static class ZodiacSkillGridUpgrade
{
    public static ZodiacSkillGridUpgradeResult Apply(
        GameCharacter character,
        int gridIndex)
    {
        ArgumentNullException.ThrowIfNull(character);

        var currentEnergyX100 = checked(
            Math.Max(0L, character.ZodiacEnergy) * 100L +
            Math.Clamp(character.ZodiacEnergyRemainderX100, 0, 99));
        var currentTalentPoints = Math.Max(0, character.TalentPoints);
        if (!ZodiacSkillGridCatalog.IsValidGrid(gridIndex))
        {
            return CreateResult(
                ZodiacSkillGridUpgradeStatus.InvalidGrid,
                gridIndex,
                previousLevel: 0,
                currentLevel: 0,
                requiredZodiacLevel: 0,
                energyCost: 0,
                talentPointCost: 0,
                currentEnergyX100,
                currentTalentPoints,
                ZodiacSkillGridCatalog.NoSelectedSkill);
        }

        var currentLevel = ZodiacSkillGridCatalog.GetLevel(
            character,
            gridIndex);
        var selectedSkillId = ZodiacSkillGridCatalog.GetSelectedSkillId(
            character,
            gridIndex);
        if (currentLevel == 0)
        {
            return CreateResult(
                ZodiacSkillGridUpgradeStatus.InactiveGrid,
                gridIndex,
                currentLevel,
                currentLevel,
                requiredZodiacLevel: 0,
                energyCost: 0,
                talentPointCost: 0,
                currentEnergyX100,
                currentTalentPoints,
                selectedSkillId);
        }

        if (!ZodiacSkillGridUpgradeCatalog.TryGetRequirement(
                currentLevel,
                out var requirement))
        {
            return CreateResult(
                ZodiacSkillGridUpgradeStatus.MaximumLevelReached,
                gridIndex,
                currentLevel,
                currentLevel,
                requiredZodiacLevel: 0,
                energyCost: 0,
                talentPointCost: 0,
                currentEnergyX100,
                currentTalentPoints,
                selectedSkillId);
        }

        var currentZodiacLevel =
            checked((byte)Math.Clamp((int)character.ZodiacLevel, 1, 30));
        if (currentZodiacLevel < requirement.RequiredZodiacLevel)
        {
            return CreateResult(
                ZodiacSkillGridUpgradeStatus.ZodiacLevelTooLow,
                gridIndex,
                currentLevel,
                currentLevel,
                requirement.RequiredZodiacLevel,
                requirement.EnergyCost,
                requirement.TalentPointCost,
                currentEnergyX100,
                currentTalentPoints,
                selectedSkillId);
        }

        var energyCostX100 = checked((long)requirement.EnergyCost * 100L);
        if (currentEnergyX100 < energyCostX100)
        {
            return CreateResult(
                ZodiacSkillGridUpgradeStatus.InsufficientEnergy,
                gridIndex,
                currentLevel,
                currentLevel,
                requirement.RequiredZodiacLevel,
                requirement.EnergyCost,
                requirement.TalentPointCost,
                currentEnergyX100,
                currentTalentPoints,
                selectedSkillId);
        }

        if (currentTalentPoints < requirement.TalentPointCost)
        {
            return CreateResult(
                ZodiacSkillGridUpgradeStatus.InsufficientTalentPoints,
                gridIndex,
                currentLevel,
                currentLevel,
                requirement.RequiredZodiacLevel,
                requirement.EnergyCost,
                requirement.TalentPointCost,
                currentEnergyX100,
                currentTalentPoints,
                selectedSkillId);
        }

        var remainingEnergyX100 = currentEnergyX100 - energyCostX100;
        var remainingTalentPoints =
            currentTalentPoints - requirement.TalentPointCost;
        character.ZodiacSkillGridLevels =
            ZodiacSkillGridActivation.NormalizeLevels(
                character.ZodiacSkillGridLevels);
        character.ZodiacSkillGridSkillIds =
            ZodiacSkillGridActivation.NormalizeSkillIds(
                character.ZodiacSkillGridSkillIds);
        character.ZodiacSkillGridLevels[gridIndex] =
            requirement.NextLevel;
        character.ZodiacEnergy =
            checked((int)(remainingEnergyX100 / 100L));
        character.ZodiacEnergyRemainderX100 =
            checked((int)(remainingEnergyX100 % 100L));
        character.TalentPoints = remainingTalentPoints;

        return CreateResult(
            ZodiacSkillGridUpgradeStatus.Succeeded,
            gridIndex,
            currentLevel,
            requirement.NextLevel,
            requirement.RequiredZodiacLevel,
            requirement.EnergyCost,
            requirement.TalentPointCost,
            remainingEnergyX100,
            remainingTalentPoints,
            character.ZodiacSkillGridSkillIds[gridIndex]);
    }

    private static ZodiacSkillGridUpgradeResult CreateResult(
        ZodiacSkillGridUpgradeStatus status,
        int gridIndex,
        byte previousLevel,
        byte currentLevel,
        byte requiredZodiacLevel,
        int energyCost,
        int talentPointCost,
        long currentEnergyX100,
        int currentTalentPoints,
        int selectedSkillId) =>
        new(
            status,
            gridIndex,
            previousLevel,
            currentLevel,
            requiredZodiacLevel,
            energyCost,
            talentPointCost,
            checked((int)(currentEnergyX100 / 100L)),
            checked((int)(currentEnergyX100 % 100L)),
            currentTalentPoints,
            selectedSkillId);
}
