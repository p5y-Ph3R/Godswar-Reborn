namespace Godswar.Server.Application.Pets;

internal sealed partial class PinnedPetContentCatalog
{
    private const int StatCount = 6;
    private const int GrowthRollScale = 100;
    private const int GrowthValueScale = 1_000_000;
    private const int SavvyValueScale = 100;

    public PetGrowthContentRoll RollGrowth(
        short aptitude,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!TryGetAptitude(aptitude, out var bracket))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aptitude),
                aptitude,
                "Unknown pet aptitude.");
        }

        var minimum = decimal.ToInt32(
            bracket.MinimumTotalGrowth * GrowthRollScale);
        var maximum = decimal.ToInt32(
            bracket.MaximumTotalGrowth * GrowthRollScale);
        var total = random.Next(minimum, checked(maximum + 1)) /
            (decimal)GrowthRollScale;
        return DistributeGrowth(bracket, total, random);
    }

    public PetSavvyContentRoll RollInitialSavvy(
        short aptitude,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!TryGetAptitude(aptitude, out var bracket))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aptitude),
                aptitude,
                "Unknown pet aptitude.");
        }

        var total = random.Next(
            bracket.MinimumInitialSavvy,
            checked(bracket.MaximumInitialSavvy + 1));
        return DistributeInitialSavvy(bracket, total, random);
    }

    public PetSavvyContentRoll RollAddedSavvy(
        short aptitude,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!TryGetAptitude(aptitude, out var bracket))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aptitude),
                aptitude,
                "Unknown pet aptitude.");
        }

        var total = random.Next(
            bracket.MinimumAddedSavvy,
            checked(bracket.MaximumAddedSavvy + 1));
        return DistributeAddedSavvy(bracket, total, random);
    }

    public bool IsValidRebirthIncrease(
        int rebirthNumber,
        PetContentStatVector current,
        PetContentStatVector proposed)
    {
        if (!current.IsNonNegative ||
            !proposed.IsNonNegative ||
            !TryGetRebirthStep(rebirthNumber, out var step))
        {
            return false;
        }

        return IsWithin(
                proposed.Agility - current.Agility, step) &&
            IsWithin(
                proposed.Strength - current.Strength, step) &&
            IsWithin(
                proposed.Accuracy - current.Accuracy, step) &&
            IsWithin(
                proposed.Technique - current.Technique, step) &&
            IsWithin(
                proposed.Wisdom - current.Wisdom, step) &&
            IsWithin(
                proposed.Luck - current.Luck, step);
    }

    private static PetGrowthContentRoll DistributeGrowth(
        PetAptitudeContentDefinition bracket,
        decimal totalGrowth,
        Random random)
    {
        if (totalGrowth * GrowthRollScale !=
                decimal.Truncate(totalGrowth * GrowthRollScale) ||
            totalGrowth < bracket.MinimumTotalGrowth ||
            totalGrowth > bracket.MaximumTotalGrowth)
        {
            throw new ArgumentOutOfRangeException(nameof(totalGrowth));
        }

        var total = decimal.ToInt32(totalGrowth * GrowthValueScale);
        var order = Enumerable.Range(0, StatCount).ToArray();
        Shuffle(order, random);
        var values = Enumerable.Repeat(total / StatCount, StatCount).ToArray();
        for (var index = 0; index < total % StatCount; index++)
        {
            values[order[index]]++;
        }

        RedistributePairs(
            values,
            order,
            bracket.MaximumGrowthStatDeviation,
            random);
        return new PetGrowthContentRoll(
            totalGrowth,
            ToVector(values, GrowthValueScale));
    }

    private static PetSavvyContentRoll DistributeInitialSavvy(
        PetAptitudeContentDefinition bracket,
        int totalSavvy,
        Random random)
    {
        if (totalSavvy < bracket.MinimumInitialSavvy ||
            totalSavvy > bracket.MaximumInitialSavvy)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSavvy));
        }

        var total = checked(totalSavvy * SavvyValueScale);
        var order = Enumerable.Range(0, StatCount).ToArray();
        Shuffle(order, random);
        var values = Enumerable.Repeat(total / StatCount, StatCount).ToArray();
        for (var index = 0; index < total % StatCount; index++)
        {
            values[order[index]]++;
        }

        RedistributePairs(
            values,
            order,
            bracket.MaximumInitialSavvyStatDeviation,
            random);
        return new PetSavvyContentRoll(
            totalSavvy,
            ToVector(values, SavvyValueScale));
    }

    private PetSavvyContentRoll DistributeAddedSavvy(
        PetAptitudeContentDefinition bracket,
        int totalSavvy,
        Random random)
    {
        if (totalSavvy < bracket.MinimumAddedSavvy ||
            totalSavvy > bracket.MaximumAddedSavvy)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSavvy));
        }

        var weights = Settings.AddedSavvyWeights
            .Select(static value => (int)value)
            .ToArray();
        Shuffle(weights, random);
        var weightTotal = weights.Sum();
        var total = checked(totalSavvy * SavvyValueScale);
        var values = new int[StatCount];
        var remainders = new int[StatCount];
        for (var index = 0; index < StatCount; index++)
        {
            var weighted = checked((long)total * weights[index]);
            values[index] = checked((int)(weighted / weightTotal));
            remainders[index] = checked((int)(weighted % weightTotal));
        }

        var remainderOrder = Enumerable.Range(0, StatCount)
            .OrderByDescending(index => remainders[index])
            .ThenBy(static index => index)
            .ToArray();
        var unallocated = checked(total - values.Sum());
        for (var index = 0; index < unallocated; index++)
        {
            values[remainderOrder[index]]++;
        }

        var result = ToVector(values, SavvyValueScale);
        if (!result.IsNonNegative ||
            values.Any(static value => value <= 0) ||
            values.Distinct().Count() < 2)
        {
            throw new InvalidOperationException(
                $"Pet aptitude {bracket.Aptitude} produced invalid added savvy.");
        }

        return new PetSavvyContentRoll(totalSavvy, result);
    }

    private static void RedistributePairs(
        int[] values,
        int[] order,
        decimal maximumDeviation,
        Random random)
    {
        var exactMean = values.Sum() / (decimal)StatCount;
        var minimum = decimal.ToInt32(
            decimal.Ceiling(exactMean * (1m - maximumDeviation)));
        var maximum = decimal.ToInt32(
            decimal.Floor(exactMean * (1m + maximumDeviation)));
        for (var pair = 0; pair < StatCount; pair += 2)
        {
            var left = order[pair];
            var right = order[pair + 1];
            var minimumDelta = Math.Max(
                minimum - values[left],
                values[right] - maximum);
            var maximumDelta = Math.Min(
                maximum - values[left],
                values[right] - minimum);
            var delta = random.Next(
                minimumDelta,
                checked(maximumDelta + 1));
            values[left] += delta;
            values[right] -= delta;
        }
    }

    private static PetContentStatVector ToVector(
        int[] values,
        int scale) =>
        new(
            values[0] / (decimal)scale,
            values[1] / (decimal)scale,
            values[2] / (decimal)scale,
            values[3] / (decimal)scale,
            values[4] / (decimal)scale,
            values[5] / (decimal)scale);

    private static bool IsWithin(
        decimal increase,
        PetRebirthStepContentDefinition step) =>
        increase >= step.MinimumIncreasePerStat &&
        increase <= step.MaximumIncreasePerStat &&
        increase * SavvyValueScale ==
            decimal.Truncate(increase * SavvyValueScale);

    private static void Shuffle(int[] values, Random random)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (values[index], values[swapIndex]) =
                (values[swapIndex], values[index]);
        }
    }
}
