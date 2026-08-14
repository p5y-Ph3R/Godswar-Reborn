using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetBasicSavvyRedistributionPolicyChecks
{
    private static readonly (int Percentile,
        PetBasicSavvyRedistributionTier Tier, int Count)[] V4Tiers =
    [
        (0, PetBasicSavvyRedistributionTier.ExtremeSingleFocus, 1),
        (1, PetBasicSavvyRedistributionTier.StrongSingleFocus, 4),
        (5, PetBasicSavvyRedistributionTier.DualExtremeFocus, 5),
        (10, PetBasicSavvyRedistributionTier.DualMediumFocus, 5),
        (15, PetBasicSavvyRedistributionTier.DualFocus, 10),
        (25, PetBasicSavvyRedistributionTier.TrioFocus, 25),
        (50, PetBasicSavvyRedistributionTier.QuadFocus, 30),
        (80, PetBasicSavvyRedistributionTier.OrdinaryRandom, 20)
    ];

    private static void CheckV4ProbabilityBoundaries()
    {
        Check.Equal(
            "fairy-basic-savvy-v4",
            PetBasicSavvyRedistributionPolicy.Version,
            "Fairy's Feather policy version");
        var counts = Enumerable.Range(0, 100)
            .Select(PetBasicSavvyRedistributionPolicy.ResolveTier)
            .GroupBy(static tier => tier)
            .ToDictionary(static group => group.Key,
                static group => group.Count());
        foreach (var expected in V4Tiers)
        {
            Check.Equal(expected.Count, counts[expected.Tier],
                $"{expected.Tier} exact percentile count");
            CheckTier(expected.Tier,
                PetBasicSavvyRedistributionPolicy.ResolveTier(
                    expected.Percentile),
                $"{expected.Tier} lower boundary");
        }
    }

    private static void CheckV4TierShapes()
    {
        const int totalUnits = 100_000;
        foreach (var expected in V4Tiers)
        {
            for (var seed = 0; seed < 64; seed++)
            {
                var roll = PetBasicSavvyRedistributionPolicy.Redistribute(
                    CurrentSavvy(totalUnits),
                    new HeadRandom([expected.Percentile],
                        HashCode.Combine(expected.Percentile, seed)));
                CheckV4Shape(roll, totalUnits,
                    $"{expected.Tier} shape seed {seed}");
            }
        }
    }

    private static void CheckV4FocusSelection()
    {
        const int totalUnits = 100_000;
        foreach (var expected in V4Tiers)
        {
            var seen = new HashSet<PetSavvyStat>();
            for (var seed = 0; seed < 512; seed++)
            {
                var roll = PetBasicSavvyRedistributionPolicy.Redistribute(
                    CurrentSavvy(totalUnits),
                    new HeadRandom([expected.Percentile], seed));
                foreach (var focus in Focuses(roll))
                {
                    if (focus != PetSavvyStat.None)
                    {
                        seen.Add(focus);
                    }
                }
            }
            if (ExpectedFocusCount(expected.Tier) > 0)
            {
                Check.Equal(6, seen.Count,
                    $"{expected.Tier} can focus every attribute");
            }
        }
    }

    private static void CheckV4DeterministicRandom()
    {
        var current = new PetContentStatVector(
            12.34m, 23.45m, 34.56m, 45.67m, 56.78m, 67.89m);
        var first = PetBasicSavvyRedistributionPolicy.Redistribute(
            current, new Random(842_113));
        var second = PetBasicSavvyRedistributionPolicy.Redistribute(
            current, new Random(842_113));
        Check.Equal(first, second,
            "equal seeds produce the same v4 Basic-Savvy roll");
        Check.Equal(Values(current).Sum(), first.TotalSavvy,
            "v4 deterministic roll preserves the exact total");
    }

    private static void CheckV4EveryHundredthTotal()
    {
        var cases = 0;
        for (var totalUnits = 100; totalUnits <= 5_000; totalUnits++)
        {
            foreach (var expected in V4Tiers)
            {
                cases++;
                var roll = PetBasicSavvyRedistributionPolicy.Redistribute(
                    CurrentSavvy(totalUnits),
                    new HeadRandom([expected.Percentile],
                        HashCode.Combine(totalUnits, expected.Percentile)));
                CheckV4Shape(roll, totalUnits,
                    $"{expected.Tier} total {totalUnits}");
            }
        }
        Check.Equal(39_208, cases,
            "all v4 tiers cover every focused hundredth total");
    }

    private static void CheckV4MaximumSupportedTotal()
    {
        foreach (var expected in V4Tiers)
        {
            var roll = PetBasicSavvyRedistributionPolicy.Redistribute(
                CurrentSavvy(int.MaxValue),
                new HeadRandom([expected.Percentile],
                    expected.Percentile + 7_000));
            CheckV4Shape(roll, int.MaxValue,
                $"{expected.Tier} maximum native total");
        }
    }

    private static void CheckV4Shape(
        PetBasicSavvyRedistributionRoll roll,
        int totalUnits,
        string context)
    {
        var values = Values(roll.BasicSavvy).Select(ToUnits).ToArray();
        Check.Equal(totalUnits, values.Sum(), $"{context} exact total");
        Check.True(values.All(static value => value > 0),
            $"{context} keeps all six attributes positive");

        var focuses = Focuses(roll)
            .Where(static focus => focus != PetSavvyStat.None)
            .ToArray();
        Check.Equal(ExpectedFocusCount(roll.Tier), focuses.Length,
            $"{context} focus count");
        Check.Equal(focuses.Length, focuses.Distinct().Count(),
            $"{context} focused attributes are distinct");

        var focusValues = focuses
            .Select(focus => values[(int)focus - 1])
            .ToArray();
        switch (roll.Tier)
        {
            case PetBasicSavvyRedistributionTier.ExtremeSingleFocus:
                CheckRange(focusValues[0], totalUnits, 9_000, 9_200,
                    context);
                CheckBalancedRemainder(values, focuses, totalUnits, context);
                break;
            case PetBasicSavvyRedistributionTier.StrongSingleFocus:
                CheckRange(focusValues[0], totalUnits, 8_200, 8_600,
                    context);
                CheckBalancedRemainder(values, focuses, totalUnits, context);
                break;
            case PetBasicSavvyRedistributionTier.DualExtremeFocus:
                CheckRange(focusValues[0], totalUnits, 5_300, 5_700,
                    context);
                CheckRange(focusValues[1], totalUnits, 3_300, 3_700,
                    context, toleranceUnits: 1);
                CheckBalancedRemainder(values, focuses, totalUnits, context);
                break;
            case PetBasicSavvyRedistributionTier.DualMediumFocus:
                CheckRange(focusValues[0], totalUnits, 4_200, 4_700,
                    context);
                CheckRange(focusValues[1], totalUnits, 2_800, 3_200,
                    context);
                CheckRange(focusValues[2], totalUnits, 1_700, 2_100,
                    context);
                CheckBalancedRemainder(values, focuses, totalUnits, context);
                break;
            case PetBasicSavvyRedistributionTier.DualFocus:
                foreach (var value in focusValues)
                    CheckRange(value, totalUnits, 4_100, 4_500, context);
                CheckBalancedRemainder(values, focuses, totalUnits, context);
                break;
            case PetBasicSavvyRedistributionTier.TrioFocus:
                foreach (var value in focusValues)
                    CheckRange(value, totalUnits, 2_700, 3_100, context);
                CheckBalancedRemainder(values, focuses, totalUnits, context);
                break;
            case PetBasicSavvyRedistributionTier.QuadFocus:
                foreach (var value in focusValues)
                    CheckRange(value, totalUnits, 1_900, 2_200, context);
                CheckBalancedRemainder(values, focuses, totalUnits, context);
                break;
            case PetBasicSavvyRedistributionTier.OrdinaryRandom:
                foreach (var value in values)
                    CheckRange(value, totalUnits, 500, 3_000, context);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unexpected v4 tier {roll.Tier}.");
        }
    }

    private static void CheckBalancedRemainder(
        IReadOnlyList<int> values,
        IReadOnlyCollection<PetSavvyStat> focuses,
        int totalUnits,
        string context)
    {
        var remainder = values.Where((_, index) =>
            !focuses.Contains((PetSavvyStat)(index + 1))).ToArray();
        var maximumSpread = checked((int)Math.Ceiling(totalUnits * 0.005m)) + 1;
        Check.True(remainder.Max() - remainder.Min() <= maximumSpread,
            $"{context} remainder attributes stay closely balanced");
    }

    private static void CheckRange(
        int value,
        int totalUnits,
        int minimumBasisPoints,
        int maximumBasisPoints,
        string context,
        int toleranceUnits = 0)
    {
        var minimum = checked((int)(
            ((long)totalUnits * minimumBasisPoints + 9_999) / 10_000));
        var maximum = checked((int)(
            (long)totalUnits * maximumBasisPoints / 10_000));
        Check.True(
            value >= minimum - toleranceUnits &&
            value <= maximum + toleranceUnits,
            $"{context} value {value} is within {minimumBasisPoints / 100m:F2}-" +
            $"{maximumBasisPoints / 100m:F2}%");
    }

    private static PetSavvyStat[] Focuses(
        PetBasicSavvyRedistributionRoll roll) =>
    [
        roll.PrimaryFocus,
        roll.SecondaryFocus,
        roll.TertiaryFocus,
        roll.QuaternaryFocus
    ];

    private static int ExpectedFocusCount(
        PetBasicSavvyRedistributionTier tier) => tier switch
    {
        PetBasicSavvyRedistributionTier.ExtremeSingleFocus => 1,
        PetBasicSavvyRedistributionTier.StrongSingleFocus => 1,
        PetBasicSavvyRedistributionTier.DualExtremeFocus => 2,
        PetBasicSavvyRedistributionTier.DualMediumFocus => 3,
        PetBasicSavvyRedistributionTier.DualFocus => 2,
        PetBasicSavvyRedistributionTier.TrioFocus => 3,
        PetBasicSavvyRedistributionTier.QuadFocus => 4,
        PetBasicSavvyRedistributionTier.OrdinaryRandom => 0,
        _ => throw new InvalidOperationException($"Unexpected v4 tier {tier}.")
    };
}
