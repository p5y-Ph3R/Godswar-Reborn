using System.Collections.Frozen;

namespace Godswar.Server.State;

internal sealed record PetAddedSavvyBracket(
    PetAptitude Aptitude,
    int MinimumTotalSavvy,
    int MaximumTotalSavvy)
{
    public short AptitudeValue => (short)Aptitude;

    public int TotalCount =>
        checked(MaximumTotalSavvy - MinimumTotalSavvy + 1);
}

internal sealed record PetAddedSavvyRoll(
    int TotalSavvy,
    PetSavvy AddedSavvy);

/// <summary>
/// Project-authored birth-time added-savvy balance. Aptitude selects a
/// whole-point total, then a shuffled set of bounded weights distributes that
/// total across the six stats at native hundredth precision.
///
/// Basic/initial savvy is intentionally not owned by this policy. It is
/// derived independently from the corresponding base-growth rate.
/// </summary>
internal static class PetAddedSavvyPolicy
{
    public const string Version = "project-v2";

    private const int SavvyValueScale = 100;
    private const int StatCount = 6;
    private const int WeightTotal = 600;

    private static readonly int[] WeightTemplate =
        [80, 88, 96, 104, 112, 120];

    public static IReadOnlyList<int> AllocationWeights { get; } =
        Array.AsReadOnly(WeightTemplate);

    public static IReadOnlyList<PetAddedSavvyBracket> All { get; } =
        Array.AsReadOnly(
        [
            B(PetAptitude.Weak, 250, 349),
            B(PetAptitude.Fool, 350, 449),
            B(PetAptitude.Cowish, 450, 574),
            B(PetAptitude.Moderate, 575, 699),
            B(PetAptitude.Rational, 700, 849),
            B(PetAptitude.Calm, 850, 1_024),
            B(PetAptitude.Grumpy, 1_025, 1_224),
            B(PetAptitude.Brave, 1_225, 1_474),
            B(PetAptitude.Zealous, 1_475, 1_774),
            B(PetAptitude.Smart, 1_775, 2_124),
            B(PetAptitude.Overbearing, 2_125, 2_524),
            B(PetAptitude.Ferocious, 2_525, 2_974),
            B(PetAptitude.Almighty, 2_975, 3_474),
            B(PetAptitude.Godly, 3_475, 4_024),
            B(PetAptitude.Celestial, 4_025, 4_624),
            B(PetAptitude.Transcendent, 4_625, 5_324)
        ]);

    private static readonly FrozenDictionary<
        short,
        PetAddedSavvyBracket> ByAptitude =
        All.ToFrozenDictionary(
            static bracket => bracket.AptitudeValue);

    static PetAddedSavvyPolicy()
    {
        if (WeightTemplate.Length != StatCount ||
            WeightTemplate.Any(static weight => weight <= 0) ||
            WeightTemplate.Distinct().Count() != StatCount ||
            WeightTemplate.Sum() != WeightTotal ||
            !WeightTemplate.SequenceEqual(
                WeightTemplate.OrderBy(static weight => weight)))
        {
            throw new InvalidDataException(
                "Added-savvy allocation weights are invalid.");
        }

        if (All.Count != PetAptitudeCatalog.Count ||
            !All.Select(static bracket => (int)bracket.AptitudeValue)
                .SequenceEqual(
                    Enumerable.Range(1, PetAptitudeCatalog.Count)))
        {
            throw new InvalidDataException(
                "Added-savvy policy must cover every aptitude exactly once.");
        }

        PetAddedSavvyBracket? previous = null;
        foreach (var bracket in All)
        {
            if (bracket.MinimumTotalSavvy <= 0 ||
                bracket.MaximumTotalSavvy <
                bracket.MinimumTotalSavvy ||
                previous is not null &&
                (bracket.MinimumTotalSavvy !=
                 previous.MaximumTotalSavvy + 1 ||
                 bracket.TotalCount < previous.TotalCount))
            {
                throw new InvalidDataException(
                    $"Added-savvy bracket {bracket.Aptitude} is invalid.");
            }

            previous = bracket;
        }
    }

    public static bool TryGet(
        PetAptitude aptitude,
        out PetAddedSavvyBracket bracket) =>
        ByAptitude.TryGetValue((short)aptitude, out bracket!);

    public static PetAddedSavvyRoll Roll(
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

    public static PetAddedSavvyRoll Distribute(
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

    private static PetAddedSavvyRoll Distribute(
        PetAddedSavvyBracket bracket,
        int totalSavvy,
        Random random)
    {
        var weights = WeightTemplate.ToArray();
        Shuffle(weights, random);

        var totalUnits = checked(totalSavvy * SavvyValueScale);
        var values = new int[StatCount];
        var remainders = new int[StatCount];
        for (var index = 0; index < StatCount; index++)
        {
            var weightedUnits =
                checked((long)totalUnits * weights[index]);
            values[index] = checked(
                (int)(weightedUnits / WeightTotal));
            remainders[index] = checked(
                (int)(weightedUnits % WeightTotal));
        }

        var unallocatedUnits = checked(
            totalUnits - values.Sum());
        var remainderOrder = Enumerable
            .Range(0, StatCount)
            .OrderByDescending(index => remainders[index])
            .ThenBy(static index => index)
            .ToArray();
        for (var index = 0; index < unallocatedUnits; index++)
        {
            values[remainderOrder[index]]++;
        }

        var addedSavvy = new PetSavvy(
            FromUnits(values[0]),
            FromUnits(values[1]),
            FromUnits(values[2]),
            FromUnits(values[3]),
            FromUnits(values[4]),
            FromUnits(values[5]));
        if (!addedSavvy.IsNonNegative ||
            values.Any(static value => value <= 0) ||
            values.Distinct().Count() < 2)
        {
            throw new InvalidDataException(
                $"Added-savvy bracket {bracket.Aptitude} produced an invalid allocation.");
        }

        return new PetAddedSavvyRoll(totalSavvy, addedSavvy);
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

    private static PetAddedSavvyBracket B(
        PetAptitude aptitude,
        int minimum,
        int maximum) =>
        new(aptitude, minimum, maximum);

    private static decimal FromUnits(int value) =>
        value / (decimal)SavvyValueScale;
}
