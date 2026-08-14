using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetGrowthPolicyChecks
{
    private const decimal GrowthValueScale = 1_000_000m;

    public static Task RunAsync()
    {
        CheckCatalog();
        CheckRepresentativeDistributions();
        CheckEveryHundredthTotal();
        CheckAllBracketsAndSeeds();
        CheckValidation();
        return Task.CompletedTask;
    }

    private static void CheckCatalog()
    {
        var expected = new (decimal Minimum, decimal Maximum)[]
        {
            (0.01m, 0.10m),
            (0.10m, 0.25m),
            (0.25m, 0.50m),
            (0.50m, 1m),
            (1m, 2m),
            (2m, 4m),
            (4m, 7m),
            (7m, 11m),
            (11m, 16m),
            (16m, 23m),
            (23m, 31m),
            (31m, 40m),
            (40m, 50m),
            (50m, 62m),
            (62m, 75m),
            (75m, 100m)
        };

        Check.Equal("project-v2", PetGrowthPolicy.Version, "growth policy version");
        Check.Equal(
            PetAptitudeCatalog.Count,
            PetGrowthPolicy.All.Count,
            "growth policy covers every pet aptitude");
        for (var index = 0; index < expected.Length; index++)
        {
            var bracket = PetGrowthPolicy.All[index];
            Check.Equal(
                (short)(index + 1),
                bracket.AptitudeValue,
                $"growth aptitude {index + 1}");
            Check.Equal(
                expected[index].Minimum,
                bracket.MinimumTotalGrowth,
                $"growth minimum {index + 1}");
            Check.Equal(
                expected[index].Maximum,
                bracket.MaximumTotalGrowth,
                $"growth maximum {index + 1}");
            Check.Equal(
                0.12m,
                bracket.MaximumStatDeviationFraction,
                $"growth spread {index + 1}");
        }

        var previousWidth = 0m;
        var previousCenter = 0m;
        var previousCenterIncrease = 0m;
        foreach (var bracket in PetGrowthPolicy.All)
        {
            var width =
                bracket.MaximumTotalGrowth -
                bracket.MinimumTotalGrowth;
            var center =
                (bracket.MinimumTotalGrowth +
                 bracket.MaximumTotalGrowth) / 2m;
            Check.True(
                width >= previousWidth,
                $"{bracket.Aptitude} growth bracket does not narrow");
            if (previousCenter > 0m)
            {
                var centerIncrease = center - previousCenter;
                Check.True(
                    centerIncrease >= previousCenterIncrease,
                    $"{bracket.Aptitude} expected growth increase does not flatten");
                previousCenterIncrease = centerIncrease;
            }

            previousWidth = width;
            previousCenter = center;
        }

        Check.True(
            PetGrowthPolicy.All[^1].MaximumTotalGrowth >=
            PetGrowthPolicy.All[0].MaximumTotalGrowth * 1_000m,
            "top aptitude has considerably greater growth than Weak");
    }

    private static void CheckRepresentativeDistributions()
    {
        var smallest = PetGrowthPolicy.Distribute(
            PetAptitude.Weak,
            0.01m,
            new Random(1));
        CheckDistribution(
            smallest,
            PetGrowthPolicy.All[0],
            "Weak 0.01");

        var first = PetGrowthPolicy.Distribute(
            PetAptitude.Zealous,
            15m,
            new Random(15));
        var second = PetGrowthPolicy.Distribute(
            PetAptitude.Zealous,
            15m,
            new Random(15));

        Check.Equal(15m, first.TotalGrowth, "15-point example total growth");
        Check.Equal(
            first,
            second,
            "seeded growth distribution is deterministic");
        CheckDistribution(first, PetGrowthPolicy.All[8], "Zealous 15.00");

        var largest = PetGrowthPolicy.Distribute(
            PetAptitude.Transcendent,
            100m,
            new Random(100));
        CheckDistribution(
            largest,
            PetGrowthPolicy.All[^1],
            "Transcendent 100.00");
    }

    private static void CheckEveryHundredthTotal()
    {
        var cases = 0;
        foreach (var bracket in PetGrowthPolicy.All)
        {
            var minimum =
                decimal.ToInt32(bracket.MinimumTotalGrowth * 100m);
            var maximum =
                decimal.ToInt32(bracket.MaximumTotalGrowth * 100m);
            for (var totalUnits = minimum;
                 totalUnits <= maximum;
                 totalUnits++)
            {
                cases++;
                var total = totalUnits / 100m;
                CheckDistribution(
                    PetGrowthPolicy.Distribute(
                        bracket.Aptitude,
                        total,
                        new Random(
                            HashCode.Combine(
                                bracket.AptitudeValue,
                                totalUnits))),
                    bracket,
                    $"{bracket.Aptitude} {total:0.00} seeded");
                CheckDistribution(
                    PetGrowthPolicy.Distribute(
                        bracket.Aptitude,
                        total,
                        new MinimumRandom()),
                    bracket,
                    $"{bracket.Aptitude} {total:0.00} lower RNG");
                CheckDistribution(
                    PetGrowthPolicy.Distribute(
                        bracket.Aptitude,
                        total,
                        new MaximumRandom()),
                    bracket,
                    $"{bracket.Aptitude} {total:0.00} upper RNG");
            }
        }

        Check.Equal(
            10_015,
            cases,
            "every bracket-inclusive hundredth total was tested");
    }

    private static void CheckAllBracketsAndSeeds()
    {
        foreach (var bracket in PetGrowthPolicy.All)
        {
            for (var seed = 0; seed < 100; seed++)
            {
                var roll = PetGrowthPolicy.Roll(
                    bracket.Aptitude,
                    new Random(seed));
                Check.True(
                    roll.TotalGrowth >= bracket.MinimumTotalGrowth &&
                    roll.TotalGrowth <= bracket.MaximumTotalGrowth,
                    $"{bracket.Aptitude} total remains in its bracket");
                Check.Equal(
                    decimal.Truncate(roll.TotalGrowth * 100m),
                    roll.TotalGrowth * 100m,
                    $"{bracket.Aptitude} total rolls at hundredth precision");
                CheckDistribution(
                    roll,
                    bracket,
                    $"{bracket.Aptitude} seed {seed}");
            }
        }
    }

    private static void CheckDistribution(
        PetGrowthRoll roll,
        PetGrowthBracket bracket,
        string context)
    {
        var values = Values(roll.BaseGrowthRates);
        Check.Equal(6, values.Length, $"{context} has six growth rates");
        Check.Equal(
            roll.TotalGrowth,
            values.Sum(),
            $"{context} growth rates preserve the exact total");

        var mean = roll.TotalGrowth / values.Length;
        var minimum = decimal.Ceiling(
            mean *
            (1m - bracket.MaximumStatDeviationFraction) *
            GrowthValueScale) / GrowthValueScale;
        var maximum = decimal.Floor(
            mean *
            (1m + bracket.MaximumStatDeviationFraction) *
            GrowthValueScale) / GrowthValueScale;
        foreach (var value in values)
        {
            Check.True(
                value > 0m,
                $"{context} stat {value:0.000000} remains positive");
            Check.True(
                value >= minimum && value <= maximum,
                $"{context} stat {value:0.000000} remains within {minimum:0.000000}-{maximum:0.000000}");
            Check.Equal(
                decimal.Truncate(value * GrowthValueScale),
                value * GrowthValueScale,
                $"{context} stat uses at most six decimal places");
        }
    }

    private static void CheckValidation()
    {
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetGrowthPolicy.Roll((PetAptitude)0, new Random(1)),
            "unknown quality cannot roll growth");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetGrowthPolicy.Distribute(
                PetAptitude.Godly,
                49.99m,
                new Random(1)),
            "growth below a quality bracket is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetGrowthPolicy.Distribute(
                PetAptitude.Godly,
                50.001m,
                new Random(1)),
            "growth finer than hundredth precision is rejected");
    }

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

    private static decimal[] Values(PetSavvy savvy) =>
    [
        savvy.Agility,
        savvy.Strength,
        savvy.Accuracy,
        savvy.Technique,
        savvy.Wisdom,
        savvy.Luck
    ];
}
