namespace Godswar.Server.State;

internal sealed record PetRebirthTier(
    int FirstRebirth,
    int LastRebirth,
    uint ChanceItemId,
    string ChanceItemName);

internal sealed record PetRebirthGrowthRoll(
    int RebirthNumber,
    decimal MinimumIncreasePerStat,
    decimal MaximumIncreasePerStat,
    PetSavvy Increase,
    PetSavvy GrowthAccelerationAfter);

/// <summary>
/// Project-authored rebirth balance. The five spirits improve all six growth
/// rates independently; the tier item grants the rebirth chance separately.
/// </summary>
internal static class PetRebirthGrowthPolicy
{
    public const string Version = "project-v1";
    public const int MaximumRebirthCount = 100;
    public const int RequiredSpiritCount = 5;
    public const uint AmbrosiaOfRebirthItemId =
        PetItemCatalog.AmbrosiaOfRebirth;

    private const int ValueScale = 100;

    public static IReadOnlyList<PetRebirthTier> Tiers { get; } =
        Array.AsReadOnly(
        new PetRebirthTier[]
        {
            new(
                1,
                30,
                PetItemCatalog.SpringWater,
                "Spring Water"),
            new(
                31,
                60,
                PetItemCatalog.JuiceOfRebirth,
                "Juice of Rebirth"),
            new(
                61,
                100,
                AmbrosiaOfRebirthItemId,
                "Ambrosia of Rebirth")
        });

    public static bool TryGetTier(
        int rebirthNumber,
        out PetRebirthTier tier)
    {
        tier = Tiers.FirstOrDefault(
            candidate =>
                rebirthNumber >= candidate.FirstRebirth &&
                rebirthNumber <= candidate.LastRebirth)!;
        return tier is not null;
    }

    public static (decimal Minimum, decimal Maximum) GetIncreaseRange(
        int rebirthNumber)
    {
        if (!TryGetTier(rebirthNumber, out _))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rebirthNumber),
                rebirthNumber,
                $"Rebirth number must be between 1 and {MaximumRebirthCount}.");
        }

        var minimumUnits = rebirthNumber switch
        {
            <= 30 => 10,
            <= 60 => InterpolateUnits(
                start: 10,
                end: 30,
                position: rebirthNumber - 31,
                steps: 29),
            _ => InterpolateUnits(
                start: 30,
                end: 50,
                position: rebirthNumber - 61,
                steps: 39)
        };

        return (
            minimumUnits / (decimal)ValueScale,
            (minimumUnits + 10) / (decimal)ValueScale);
    }

    public static PetRebirthGrowthRoll Roll(
        int rebirthNumber,
        int spiritCount,
        PetSavvy currentGrowthAcceleration,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (spiritCount != RequiredSpiritCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spiritCount),
                spiritCount,
                $"Exactly {RequiredSpiritCount} rebirth spirits are required.");
        }

        if (!currentGrowthAcceleration.IsNonNegative)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentGrowthAcceleration),
                "Growth acceleration cannot be negative.");
        }

        var (minimum, maximum) = GetIncreaseRange(rebirthNumber);
        var minimumUnits = ToUnits(minimum);
        var maximumUnits = ToUnits(maximum);
        decimal Next() =>
            random.Next(minimumUnits, checked(maximumUnits + 1)) /
            (decimal)ValueScale;

        var increase = new PetSavvy(
            Next(),
            Next(),
            Next(),
            Next(),
            Next(),
            Next());
        return new PetRebirthGrowthRoll(
            rebirthNumber,
            minimum,
            maximum,
            increase,
            currentGrowthAcceleration + increase);
    }

    public static bool IsValidOutcome(
        int rebirthNumber,
        PetSavvy currentGrowthAcceleration,
        PetSavvy proposedGrowthAcceleration)
    {
        if (!currentGrowthAcceleration.IsNonNegative ||
            !proposedGrowthAcceleration.IsNonNegative)
        {
            return false;
        }

        (decimal Minimum, decimal Maximum) range;
        try
        {
            range = GetIncreaseRange(rebirthNumber);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return IsWithinRange(
                proposedGrowthAcceleration.Agility -
                currentGrowthAcceleration.Agility,
                range) &&
            IsWithinRange(
                proposedGrowthAcceleration.Strength -
                currentGrowthAcceleration.Strength,
                range) &&
            IsWithinRange(
                proposedGrowthAcceleration.Accuracy -
                currentGrowthAcceleration.Accuracy,
                range) &&
            IsWithinRange(
                proposedGrowthAcceleration.Technique -
                currentGrowthAcceleration.Technique,
                range) &&
            IsWithinRange(
                proposedGrowthAcceleration.Wisdom -
                currentGrowthAcceleration.Wisdom,
                range) &&
            IsWithinRange(
                proposedGrowthAcceleration.Luck -
                currentGrowthAcceleration.Luck,
                range);
    }

    private static int InterpolateUnits(
        int start,
        int end,
        int position,
        int steps) =>
        start +
        checked(((end - start) * position + steps / 2) / steps);

    private static int ToUnits(decimal value) =>
        decimal.ToInt32(value * ValueScale);

    private static bool IsWithinRange(
        decimal increase,
        (decimal Minimum, decimal Maximum) range) =>
        increase >= range.Minimum &&
        increase <= range.Maximum &&
        increase * ValueScale ==
        decimal.Truncate(increase * ValueScale);
}
