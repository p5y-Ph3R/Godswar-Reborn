using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

internal sealed record PetRebirthGrowthRoll(
    int RebirthNumber,
    decimal MinimumIncreasePerStat,
    decimal MaximumIncreasePerStat,
    PetSavvy Increase,
    PetSavvy GrowthAccelerationAfter);

/// <summary>
/// Exact installed-client Pet_Alter.xml 0..5 selection contract. These are
/// native protocol/formula invariants, not mutable game-content declarations.
/// </summary>
internal static class PetRebirthSpiritPolicy
{
    public const int MinimumCount = PetRebirthMaterialContract.MinimumCount;
    public const int MaximumCount = PetRebirthMaterialContract.MaximumCount;
    public const int MaximumRebirthNumber = 100;

    private const int ValueScale = 100;

    public static (decimal Minimum, decimal Maximum) GetIncreaseRange(
        int spiritCount)
    {
        if (spiritCount is < MinimumCount or > MaximumCount)
        {
            throw new ArgumentOutOfRangeException(nameof(spiritCount));
        }
        var minimumUnits = spiritCount switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            3 => 6,
            4 => 8,
            _ => 10
        };
        return (minimumUnits / 100m, 0.20m);
    }

    public static bool IsCanonicalMaterialSelection(
        int materialTemplateId,
        int spiritCount) =>
        PetRebirthMaterialContract.IsCanonicalSelection(
            materialTemplateId,
            spiritCount);

    public static PetRebirthGrowthRoll Roll(
        int rebirthNumber,
        int spiritCount,
        PetSavvy currentGrowthAcceleration,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (rebirthNumber is < 1 or > MaximumRebirthNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(rebirthNumber));
        }
        if (!currentGrowthAcceleration.IsNonNegative)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentGrowthAcceleration));
        }
        var (minimum, maximum) = GetIncreaseRange(spiritCount);
        var minimumUnits = ToUnits(minimum);
        var maximumUnits = ToUnits(maximum);
        decimal Next() => random.Next(
            minimumUnits,
            checked(maximumUnits + 1)) / (decimal)ValueScale;
        var increase = new PetSavvy(
            Next(), Next(), Next(), Next(), Next(), Next());
        return new PetRebirthGrowthRoll(
            rebirthNumber,
            minimum,
            maximum,
            increase,
            currentGrowthAcceleration + increase);
    }

    public static bool IsValidOutcome(
        int rebirthNumber,
        int spiritCount,
        PetSavvy current,
        PetSavvy proposed)
    {
        if (rebirthNumber is < 1 or > MaximumRebirthNumber ||
            !current.IsNonNegative || !proposed.IsNonNegative)
        {
            return false;
        }
        (decimal Minimum, decimal Maximum) range;
        try
        {
            range = GetIncreaseRange(spiritCount);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        return Within(proposed.Agility - current.Agility, range) &&
            Within(proposed.Strength - current.Strength, range) &&
            Within(proposed.Accuracy - current.Accuracy, range) &&
            Within(proposed.Technique - current.Technique, range) &&
            Within(proposed.Wisdom - current.Wisdom, range) &&
            Within(proposed.Luck - current.Luck, range);
    }

    private static int ToUnits(decimal value) =>
        decimal.ToInt32(value * ValueScale);

    private static bool Within(
        decimal increase,
        (decimal Minimum, decimal Maximum) range) =>
        increase >= range.Minimum &&
        increase <= range.Maximum &&
        increase * ValueScale == decimal.Truncate(increase * ValueScale);
}
