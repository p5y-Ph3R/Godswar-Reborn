using System.Collections.Frozen;

namespace Godswar.Server.State;

internal sealed record PetInitialSavvyBracket(
    PetAptitude Aptitude,
    int MinimumTotalSavvy,
    int MaximumTotalSavvy,
    decimal MaximumStatDeviationFraction)
{
    public short AptitudeValue => (short)Aptitude;

    public int TotalCount =>
        checked(MaximumTotalSavvy - MinimumTotalSavvy + 1);
}

internal sealed record PetInitialSavvyRoll(
    int TotalSavvy,
    PetSavvy InitialSavvy);

/// <summary>
/// Project-authored initial/basic savvy balance. This is deliberately
/// independent from base growth and rebirth growth acceleration.
///
/// Totals roll in whole points. The six values use hundredth precision so
/// native uint(value * 100) pet fields preserve them without rounding.
/// </summary>
internal static class PetInitialSavvyPolicy
{
    public const string Version = "project-v3";
    public const decimal MaximumStatDeviationFraction = 0.12m;

    private const int SavvyValueScale = 100;
    private const int StatCount = 6;

    public static IReadOnlyList<PetInitialSavvyBracket> All { get; } =
        Array.AsReadOnly(
        [
            B(PetAptitude.Weak, 25, 34),
            B(PetAptitude.Fool, 35, 44),
            B(PetAptitude.Cowish, 45, 54),
            B(PetAptitude.Moderate, 55, 69),
            B(PetAptitude.Rational, 70, 84),
            B(PetAptitude.Calm, 85, 104),
            B(PetAptitude.Grumpy, 105, 124),
            B(PetAptitude.Brave, 125, 149),
            B(PetAptitude.Zealous, 150, 174),
            B(PetAptitude.Smart, 175, 200),
            B(PetAptitude.Overbearing, 2_125, 2_524),
            B(PetAptitude.Ferocious, 2_525, 2_974),
            B(PetAptitude.Almighty, 2_975, 3_474),
            B(PetAptitude.Godly, 3_475, 4_024),
            B(PetAptitude.Celestial, 4_025, 4_624),
            B(PetAptitude.Transcendent, 4_625, 5_324)
        ]);

    private static readonly FrozenDictionary<
        short,
        PetInitialSavvyBracket> ByAptitude =
        All.ToFrozenDictionary(
            static bracket => bracket.AptitudeValue);

    static PetInitialSavvyPolicy()
    {
        if (All.Count != PetAptitudeCatalog.Count ||
            !All.Select(static bracket => (int)bracket.AptitudeValue)
                .SequenceEqual(
                    Enumerable.Range(1, PetAptitudeCatalog.Count)))
        {
            throw new InvalidDataException(
                "Initial-savvy policy must cover every aptitude exactly once.");
        }

        PetInitialSavvyBracket? previous = null;
        foreach (var bracket in All)
        {
            if (bracket.MinimumTotalSavvy <= 0 ||
                bracket.MaximumTotalSavvy <
                bracket.MinimumTotalSavvy ||
                bracket.MaximumStatDeviationFraction is <= 0m or > 0.25m ||
                previous is not null &&
                (bracket.MinimumTotalSavvy <=
                 previous.MaximumTotalSavvy ||
                 bracket.TotalCount < previous.TotalCount))
            {
                throw new InvalidDataException(
                    $"Initial-savvy bracket {bracket.Aptitude} is invalid.");
            }

            previous = bracket;
        }
    }

    public static bool TryGet(
        PetAptitude aptitude,
        out PetInitialSavvyBracket bracket) =>
        ByAptitude.TryGetValue((short)aptitude, out bracket!);

    public static PetInitialSavvyRoll Roll(
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

        var total = random.Next(
            bracket.MinimumTotalSavvy,
            checked(bracket.MaximumTotalSavvy + 1));
        return Distribute(bracket, total, random);
    }

    public static PetInitialSavvyRoll Distribute(
        PetAptitude aptitude,
        int totalSavvy,
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

        if (totalSavvy < bracket.MinimumTotalSavvy ||
            totalSavvy > bracket.MaximumTotalSavvy)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalSavvy),
                totalSavvy,
                $"Total savvy must be within the {bracket.MinimumTotalSavvy}-{bracket.MaximumTotalSavvy} {aptitude} bracket.");
        }

        return Distribute(bracket, totalSavvy, random);
    }

    private static PetInitialSavvyRoll Distribute(
        PetInitialSavvyBracket bracket,
        int totalSavvy,
        Random random)
    {
        var totalUnits = checked(totalSavvy * SavvyValueScale);
        var statOrder = Enumerable.Range(0, StatCount).ToArray();
        Shuffle(statOrder, random);

        var values = Enumerable
            .Repeat(totalUnits / StatCount, StatCount)
            .ToArray();
        for (var index = 0;
             index < totalUnits % StatCount;
             index++)
        {
            values[statOrder[index]]++;
        }

        var exactMean = totalUnits / (decimal)StatCount;
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

        return new PetInitialSavvyRoll(
            totalSavvy,
            new PetSavvy(
                FromUnits(values[0]),
                FromUnits(values[1]),
                FromUnits(values[2]),
                FromUnits(values[3]),
                FromUnits(values[4]),
                FromUnits(values[5])));
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

    private static PetInitialSavvyBracket B(
        PetAptitude aptitude,
        int minimum,
        int maximum) =>
        new(
            aptitude,
            minimum,
            maximum,
            MaximumStatDeviationFraction);

    private static decimal FromUnits(int value) =>
        value / (decimal)SavvyValueScale;
}
