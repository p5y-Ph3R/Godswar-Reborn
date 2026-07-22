namespace Godswar.Server.State;

internal static class ZodiacEnergyCatalog
{
    // Localization/en_us/UI/XML/ConstellationConfig.lua, MaxPower1..MaxPower30.
    // These are storage ceilings, not the energy costs for skill-grid upgrades.
    private static readonly int[] StorageLimits =
    [
        1_000,
        3_000,
        8_000,
        20_000,
        28_000,
        42_000,
        60_000,
        80_000,
        100_000,
        125_000,
        150_000,
        180_000,
        210_000,
        245_000,
        280_000,
        315_000,
        355_000,
        400_000,
        445_000,
        500_000,
        555_000,
        610_000,
        665_000,
        725_000,
        785_000,
        845_000,
        905_000,
        965_000,
        1_025_000,
        1_090_000
    ];

    public static int GetStorageLimit(int zodiacLevel)
    {
        if (zodiacLevel is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zodiacLevel),
                zodiacLevel,
                "Zodiac level must be between 1 and 30.");
        }

        return StorageLimits[zodiacLevel - 1];
    }

    public static int ClampToStorageLimit(int zodiacLevel, int energy)
    {
        return Math.Clamp(energy, 0, GetStorageLimit(zodiacLevel));
    }
}
