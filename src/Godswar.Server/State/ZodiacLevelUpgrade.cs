namespace Godswar.Server.State;

internal readonly record struct ZodiacLevelUpgradeRequirement(
    byte CurrentLevel,
    byte NextLevel,
    int RequiredCharacterLevel,
    int EnergyCost);

internal enum ZodiacLevelUpgradeStatus
{
    Succeeded,
    CharacterLevelTooLow,
    InsufficientEnergy,
    MaximumLevelReached
}

internal sealed record ZodiacLevelUpgradeResult(
    ZodiacLevelUpgradeStatus Status,
    byte PreviousLevel,
    byte CurrentLevel,
    int RequiredCharacterLevel,
    int EnergyCost,
    int CurrentEnergy,
    int CurrentEnergyRemainderX100)
{
    public bool Committed => Status == ZodiacLevelUpgradeStatus.Succeeded;
}

internal static class ZodiacLevelUpgradeCatalog
{
    // Shipped-client UI policy from ConstellationConfig.lua:
    // Constellationlevup_Level1..29 and Player_Level1..29. The server owns
    // these values and treats every SID 3 request only as an upgrade intent.
    private static readonly int[] EnergyCosts =
    [
        500,
        2_000,
        4_000,
        8_000,
        20_000,
        30_000,
        44_000,
        65_000,
        85_000,
        105_000,
        130_000,
        155_000,
        185_000,
        215_000,
        250_000,
        285_000,
        325_000,
        365_000,
        420_000,
        475_000,
        530_000,
        585_000,
        640_000,
        700_000,
        760_000,
        820_000,
        880_000,
        940_000,
        1_000_000
    ];

    private static readonly int[] RequiredCharacterLevels =
    [
        10,
        25,
        40,
        60,
        82,
        94,
        103,
        108,
        113,
        116,
        119,
        122,
        125,
        128,
        131,
        134,
        136,
        138,
        140,
        142,
        144,
        146,
        148,
        150,
        152,
        154,
        156,
        158,
        160
    ];

    public static bool TryGetRequirement(
        int currentZodiacLevel,
        out ZodiacLevelUpgradeRequirement requirement)
    {
        if (currentZodiacLevel is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentZodiacLevel),
                currentZodiacLevel,
                "Zodiac level must be between 1 and 30.");
        }

        if (currentZodiacLevel == 30)
        {
            requirement = default;
            return false;
        }

        var index = currentZodiacLevel - 1;
        requirement = new ZodiacLevelUpgradeRequirement(
            checked((byte)currentZodiacLevel),
            checked((byte)(currentZodiacLevel + 1)),
            RequiredCharacterLevels[index],
            EnergyCosts[index]);
        return true;
    }
}

internal static class ZodiacLevelUpgrade
{
    public static ZodiacLevelUpgradeResult Apply(GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);

        var previousLevel = checked((byte)Math.Clamp((int)character.ZodiacLevel, 1, 30));
        var currentEnergyX100 = Math.Min(
            checked((long)ZodiacEnergyCatalog.GetStorageLimit(previousLevel) * 100L),
            checked(
                Math.Max(0L, character.ZodiacEnergy) * 100L +
                Math.Clamp(character.ZodiacEnergyRemainderX100, 0, 99)));

        if (!ZodiacLevelUpgradeCatalog.TryGetRequirement(
                previousLevel,
                out var requirement))
        {
            return CreateResult(
                ZodiacLevelUpgradeStatus.MaximumLevelReached,
                previousLevel,
                previousLevel,
                requiredCharacterLevel: 0,
                energyCost: 0,
                currentEnergyX100);
        }

        if (character.Level < requirement.RequiredCharacterLevel)
        {
            return CreateResult(
                ZodiacLevelUpgradeStatus.CharacterLevelTooLow,
                previousLevel,
                previousLevel,
                requirement.RequiredCharacterLevel,
                requirement.EnergyCost,
                currentEnergyX100);
        }

        var energyCostX100 = checked((long)requirement.EnergyCost * 100L);
        if (currentEnergyX100 < energyCostX100)
        {
            return CreateResult(
                ZodiacLevelUpgradeStatus.InsufficientEnergy,
                previousLevel,
                previousLevel,
                requirement.RequiredCharacterLevel,
                requirement.EnergyCost,
                currentEnergyX100);
        }

        var remainingEnergyX100 = currentEnergyX100 - energyCostX100;
        character.ZodiacLevel = requirement.NextLevel;
        character.ZodiacEnergy = checked((int)(remainingEnergyX100 / 100L));
        character.ZodiacEnergyRemainderX100 =
            checked((int)(remainingEnergyX100 % 100L));
        return CreateResult(
            ZodiacLevelUpgradeStatus.Succeeded,
            previousLevel,
            requirement.NextLevel,
            requirement.RequiredCharacterLevel,
            requirement.EnergyCost,
            remainingEnergyX100);
    }

    private static ZodiacLevelUpgradeResult CreateResult(
        ZodiacLevelUpgradeStatus status,
        byte previousLevel,
        byte currentLevel,
        int requiredCharacterLevel,
        int energyCost,
        long currentEnergyX100) =>
        new(
            status,
            previousLevel,
            currentLevel,
            requiredCharacterLevel,
            energyCost,
            checked((int)(currentEnergyX100 / 100L)),
            checked((int)(currentEnergyX100 % 100L)));
}
