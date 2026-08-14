using System.Collections.Frozen;

namespace Godswar.Server.State;

internal sealed record PetGrowthBracket(
    PetAptitude Aptitude,
    decimal MinimumTotalGrowth,
    decimal MaximumTotalGrowth,
    decimal MaximumStatDeviationFraction)
{
    public short AptitudeValue => (short)Aptitude;
}

internal sealed record PetGrowthRoll(
    decimal TotalGrowth,
    PetSavvy BaseGrowthRates);

/// <summary>
/// Project-authored pet growth balance. Aptitude selects a total-growth
/// bracket; the rolled total is then distributed across all six growth rates
/// without allowing one stat to drift far from an even share.
/// </summary>
internal static class PetGrowthPolicy
{
    public const string Version = "project-v2";
    public const decimal MaximumStatDeviationFraction = 0.12m;

    private const int TotalRollScale = 100;
    private const int GrowthValueScale = 1_000_000;
    private const int StatCount = 6;

    public static IReadOnlyList<PetGrowthBracket> All { get; } =
        Array.AsReadOnly(
        [
            B(PetAptitude.Weak, 0.01m, 0.10m),
            B(PetAptitude.Fool, 0.10m, 0.25m),
            B(PetAptitude.Cowish, 0.25m, 0.50m),
            B(PetAptitude.Moderate, 0.50m, 1m),
            B(PetAptitude.Rational, 1m, 2m),
            B(PetAptitude.Calm, 2m, 4m),
            B(PetAptitude.Grumpy, 4m, 7m),
            B(PetAptitude.Brave, 7m, 11m),
            B(PetAptitude.Zealous, 11m, 16m),
            B(PetAptitude.Smart, 16m, 23m),
            B(PetAptitude.Overbearing, 23m, 31m),
            B(PetAptitude.Ferocious, 31m, 40m),
            B(PetAptitude.Almighty, 40m, 50m),
            B(PetAptitude.Godly, 50m, 62m),
            B(PetAptitude.Celestial, 62m, 75m),
            B(PetAptitude.Transcendent, 75m, 100m)
        ]);

    private static readonly FrozenDictionary<short, PetGrowthBracket>
        ByAptitude = All.ToFrozenDictionary(
            static bracket => bracket.AptitudeValue);

    static PetGrowthPolicy()
    {
        if (All.Count != PetAptitudeCatalog.Count ||
            !All.Select(static bracket => (int)bracket.AptitudeValue)
                .SequenceEqual(Enumerable.Range(1, PetAptitudeCatalog.Count)))
        {
            throw new InvalidDataException(
                "The pet growth policy must cover every aptitude exactly once.");
        }

        PetGrowthBracket? previous = null;
        foreach (var bracket in All)
        {
            if (bracket.MinimumTotalGrowth <= 0m ||
                bracket.MaximumTotalGrowth < bracket.MinimumTotalGrowth ||
                bracket.MaximumStatDeviationFraction is <= 0m or > 0.25m ||
                !HasHundredthPrecision(bracket.MinimumTotalGrowth) ||
                !HasHundredthPrecision(bracket.MaximumTotalGrowth) ||
                previous is not null &&
                bracket.MinimumTotalGrowth < previous.MaximumTotalGrowth)
            {
                throw new InvalidDataException(
                    $"Pet growth bracket {bracket.Aptitude} is invalid or overlaps its predecessor.");
            }

            previous = bracket;
        }
    }

    public static bool TryGet(
        PetAptitude aptitude,
        out PetGrowthBracket bracket) =>
        ByAptitude.TryGetValue((short)aptitude, out bracket!);

    public static PetGrowthRoll Roll(
        PetAptitude aptitude,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!TryGet(aptitude, out var bracket))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aptitude),
                aptitude,
                "Unknown pet aptitude.");
        }

        var minimum = ToTotalRollUnits(bracket.MinimumTotalGrowth);
        var maximum = ToTotalRollUnits(bracket.MaximumTotalGrowth);
        var total = random.Next(minimum, checked(maximum + 1));
        return DistributeTotal(
            bracket,
            FromTotalRollUnits(total),
            random);
    }

    public static PetGrowthRoll Distribute(
        PetAptitude aptitude,
        decimal totalGrowth,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!TryGet(aptitude, out var bracket))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aptitude),
                aptitude,
                "Unknown pet aptitude.");
        }

        if (!HasHundredthPrecision(totalGrowth) ||
            totalGrowth < bracket.MinimumTotalGrowth ||
            totalGrowth > bracket.MaximumTotalGrowth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalGrowth),
                totalGrowth,
                $"Total growth must be within the {bracket.MinimumTotalGrowth:0.00}-{bracket.MaximumTotalGrowth:0.00} {aptitude} bracket.");
        }

        return DistributeTotal(
            bracket,
            totalGrowth,
            random);
    }

    private static PetGrowthRoll DistributeTotal(
        PetGrowthBracket bracket,
        decimal totalGrowth,
        Random random)
    {
        var total = ToGrowthValueUnits(totalGrowth);
        var statOrder = Enumerable.Range(0, StatCount).ToArray();
        Shuffle(statOrder, random);

        var values = Enumerable.Repeat(total / StatCount, StatCount).ToArray();
        for (var index = 0; index < total % StatCount; index++)
        {
            values[statOrder[index]]++;
        }

        var exactMean = total / (decimal)StatCount;
        var minimumStat = decimal.ToInt32(
            decimal.Ceiling(
                exactMean *
                (1m - bracket.MaximumStatDeviationFraction)));
        var maximumStat = decimal.ToInt32(
            decimal.Floor(
                exactMean *
                (1m + bracket.MaximumStatDeviationFraction)));

        for (var pair = 0; pair < StatCount; pair += 2)
        {
            var left = statOrder[pair];
            var right = statOrder[pair + 1];
            var minimumDelta = Math.Max(
                minimumStat - values[left],
                values[right] - maximumStat);
            var maximumDelta = Math.Min(
                maximumStat - values[left],
                values[right] - minimumStat);
            var delta = random.Next(
                minimumDelta,
                checked(maximumDelta + 1));

            values[left] += delta;
            values[right] -= delta;
        }

        var rates = new PetSavvy(
            FromGrowthValueUnits(values[0]),
            FromGrowthValueUnits(values[1]),
            FromGrowthValueUnits(values[2]),
            FromGrowthValueUnits(values[3]),
            FromGrowthValueUnits(values[4]),
            FromGrowthValueUnits(values[5]));
        return new PetGrowthRoll(totalGrowth, rates);
    }

    private static void Shuffle(int[] values, Random random)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (values[index], values[swapIndex]) =
                (values[swapIndex], values[index]);
        }
    }

    private static PetGrowthBracket B(
        PetAptitude aptitude,
        decimal minimum,
        decimal maximum) =>
        new(
            aptitude,
            minimum,
            maximum,
            MaximumStatDeviationFraction);

    private static bool HasHundredthPrecision(decimal value) =>
        value * TotalRollScale ==
        decimal.Truncate(value * TotalRollScale);

    private static int ToTotalRollUnits(decimal value) =>
        decimal.ToInt32(value * TotalRollScale);

    private static decimal FromTotalRollUnits(int value) =>
        value / (decimal)TotalRollScale;

    private static int ToGrowthValueUnits(decimal value) =>
        decimal.ToInt32(value * GrowthValueScale);

    private static decimal FromGrowthValueUnits(int value) =>
        value / (decimal)GrowthValueScale;
}
