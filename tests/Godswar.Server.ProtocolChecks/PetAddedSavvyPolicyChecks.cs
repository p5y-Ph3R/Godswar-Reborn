using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetAddedSavvyPolicyChecks
{
    private const decimal SavvyValueScale = 100m;
    private const decimal WeightTotal = 600m;

    public static Task RunAsync()
    {
        CheckCatalog();
        CheckDeterministicDistribution();
        CheckEveryTotalAndBoundaryRng();
        CheckRollBoundaries();
        CheckSeedVariation();
        CheckValidation();
        return Task.CompletedTask;
    }

    private static void CheckCatalog()
    {
        var expected = new (int Minimum, int Maximum)[]
        {
            (250, 349),
            (350, 449),
            (450, 574),
            (575, 699),
            (700, 849),
            (850, 1_024),
            (1_025, 1_224),
            (1_225, 1_474),
            (1_475, 1_774),
            (1_775, 2_124),
            (2_125, 2_524),
            (2_525, 2_974),
            (2_975, 3_474),
            (3_475, 4_024),
            (4_025, 4_624),
            (4_625, 5_324)
        };
        var expectedWeights = new[] { 80, 88, 96, 104, 112, 120 };

        Check.Equal(
            "project-v2",
            PetAddedSavvyPolicy.Version,
            "added-savvy policy version");
        Check.True(
            expectedWeights.SequenceEqual(
                PetAddedSavvyPolicy.AllocationWeights),
            "added-savvy policy exposes the intended bounded weights");
        Check.Equal(
            PetAptitudeCatalog.Count,
            PetAddedSavvyPolicy.All.Count,
            "added-savvy policy covers every aptitude");

        var previousCount = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            var bracket = PetAddedSavvyPolicy.All[index];
            Check.Equal(
                (short)(index + 1),
                bracket.AptitudeValue,
                $"added-savvy aptitude {index + 1}");
            Check.Equal(
                expected[index].Minimum,
                bracket.MinimumTotalSavvy,
                $"added-savvy minimum {index + 1}");
            Check.Equal(
                expected[index].Maximum,
                bracket.MaximumTotalSavvy,
                $"added-savvy maximum {index + 1}");
            Check.True(
                bracket.TotalCount >= previousCount,
                $"{bracket.Aptitude} bracket does not narrow");
            previousCount = bracket.TotalCount;
        }

        Check.Equal(
            5_075,
            PetAddedSavvyPolicy.All.Sum(
                static bracket => bracket.TotalCount),
            "added-savvy ranges are contiguous and exhaustive");
    }

    private static void CheckDeterministicDistribution()
    {
        var first = PetAddedSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_750,
            new Random(3_750));
        var second = PetAddedSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_750,
            new Random(3_750));

        Check.Equal(
            first,
            second,
            "equal seeds produce equal added-savvy distributions");
        CheckDistribution(
            first,
            PetAddedSavvyPolicy.All[13],
            "Godly deterministic distribution");
    }

    private static void CheckEveryTotalAndBoundaryRng()
    {
        var cases = 0;
        foreach (var bracket in PetAddedSavvyPolicy.All)
        {
            for (var total = bracket.MinimumTotalSavvy;
                 total <= bracket.MaximumTotalSavvy;
                 total++)
            {
                cases++;
                CheckDistribution(
                    PetAddedSavvyPolicy.Distribute(
                        bracket.Aptitude,
                        total,
                        new Random(
                            HashCode.Combine(
                                bracket.AptitudeValue,
                                total))),
                    bracket,
                    $"{bracket.Aptitude} {total} seeded");
                CheckDistribution(
                    PetAddedSavvyPolicy.Distribute(
                        bracket.Aptitude,
                        total,
                        new MinimumRandom()),
                    bracket,
                    $"{bracket.Aptitude} {total} lower RNG");
                CheckDistribution(
                    PetAddedSavvyPolicy.Distribute(
                        bracket.Aptitude,
                        total,
                        new MaximumRandom()),
                    bracket,
                    $"{bracket.Aptitude} {total} upper RNG");
            }
        }

        Check.Equal(
            5_075,
            cases,
            "every allowed whole-point total was tested");
    }

    private static void CheckRollBoundaries()
    {
        foreach (var bracket in PetAddedSavvyPolicy.All)
        {
            var minimum = PetAddedSavvyPolicy.Roll(
                bracket.Aptitude,
                new MinimumRandom());
            Check.Equal(
                bracket.MinimumTotalSavvy,
                minimum.TotalSavvy,
                $"{bracket.Aptitude} minimum roll");
            CheckDistribution(
                minimum,
                bracket,
                $"{bracket.Aptitude} minimum roll");

            var maximum = PetAddedSavvyPolicy.Roll(
                bracket.Aptitude,
                new MaximumRandom());
            Check.Equal(
                bracket.MaximumTotalSavvy,
                maximum.TotalSavvy,
                $"{bracket.Aptitude} maximum roll");
            CheckDistribution(
                maximum,
                bracket,
                $"{bracket.Aptitude} maximum roll");
        }
    }

    private static void CheckSeedVariation()
    {
        var minimumPath = PetAddedSavvyPolicy.Distribute(
            PetAptitude.Transcendent,
            4_975,
            new MinimumRandom());
        var maximumPath = PetAddedSavvyPolicy.Distribute(
            PetAptitude.Transcendent,
            4_975,
            new MaximumRandom());

        Check.True(
            minimumPath.AddedSavvy != maximumPath.AddedSavvy,
            "different injected random paths change the stat allocation");
        Check.Equal(
            minimumPath.TotalSavvy,
            maximumPath.TotalSavvy,
            "random allocation does not change the rarity total");
    }

    private static void CheckValidation()
    {
        Check.True(
            !PetAddedSavvyPolicy.TryGet(
                (PetAptitude)0,
                out _),
            "unknown aptitude lookup fails closed");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetAddedSavvyPolicy.Roll(
                (PetAptitude)0,
                new Random(1)),
            "unknown aptitude cannot roll added savvy");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetAddedSavvyPolicy.Distribute(
                PetAptitude.Weak,
                249,
                new Random(1)),
            "total below its bracket is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetAddedSavvyPolicy.Distribute(
                PetAptitude.Transcendent,
                5_325,
                new Random(1)),
            "total above its bracket is rejected");
        Check.Throws<ArgumentNullException>(
            () => PetAddedSavvyPolicy.Roll(
                PetAptitude.Weak,
                null!),
            "roll requires a caller-owned Random");
        Check.Throws<ArgumentNullException>(
            () => PetAddedSavvyPolicy.Distribute(
                PetAptitude.Weak,
                250,
                null!),
            "distribution requires a caller-owned Random");
    }

    private static void CheckDistribution(
        PetAddedSavvyRoll roll,
        PetAddedSavvyBracket bracket,
        string context)
    {
        var values = Values(roll.AddedSavvy);
        Check.Equal(6, values.Length, $"{context} has six values");
        Check.Equal(
            roll.TotalSavvy,
            values.Sum(),
            $"{context} preserves its exact total");
        Check.True(
            values.Distinct().Count() > 1,
            $"{context} does not assign the same value to every stat");

        var sortedValues = values.Order().ToArray();
        var weights = PetAddedSavvyPolicy.AllocationWeights;
        for (var index = 0; index < sortedValues.Length; index++)
        {
            var exactWeightedValue =
                roll.TotalSavvy * weights[index] / WeightTotal;
            var minimum = decimal.Floor(
                exactWeightedValue * SavvyValueScale) /
                SavvyValueScale;
            var maximum = decimal.Ceiling(
                exactWeightedValue * SavvyValueScale) /
                SavvyValueScale;
            Check.True(
                sortedValues[index] >= minimum &&
                sortedValues[index] <= maximum,
                $"{context} weight {weights[index]} allocation remains within {minimum:0.00}-{maximum:0.00}");
        }

        foreach (var value in values)
        {
            Check.True(
                value > 0m,
                $"{context} value {value:0.00} remains positive");
            Check.Equal(
                decimal.Truncate(value * SavvyValueScale),
                value * SavvyValueScale,
                $"{context} value is native hundredth precision");
            Check.True(
                value * SavvyValueScale <= uint.MaxValue,
                $"{context} value fits the native uint field");
        }
    }

    private static decimal[] Values(PetSavvy savvy) =>
    [
        savvy.Agility,
        savvy.Strength,
        savvy.Accuracy,
        savvy.Technique,
        savvy.Wisdom,
        savvy.Luck
    ];

    private sealed class MinimumRandom : Random
    {
        public override int Next(int maxValue) => 0;

        public override int Next(int minValue, int maxValue) =>
            minValue;
    }

    private sealed class MaximumRandom : Random
    {
        public override int Next(int maxValue) =>
            maxValue - 1;

        public override int Next(int minValue, int maxValue) =>
            maxValue - 1;
    }
}
