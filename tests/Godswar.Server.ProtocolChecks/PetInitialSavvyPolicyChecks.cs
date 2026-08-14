using System.Security.Cryptography;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetInitialSavvyPolicyChecks
{
    private const decimal SavvyValueScale = 100m;

    public static Task RunAsync()
    {
        CheckCatalog();
        CheckDeterministicDistribution();
        CheckEveryTotalAndBoundaryRng();
        CheckRollBoundaries();
        CheckSecurelySeededRandom();
        CheckValidation();
        return Task.CompletedTask;
    }

    private static void CheckCatalog()
    {
        var expected = new (int Minimum, int Maximum)[]
        {
            (25, 34),
            (35, 44),
            (45, 54),
            (55, 69),
            (70, 84),
            (85, 104),
            (105, 124),
            (125, 149),
            (150, 174),
            (175, 200),
            (2_125, 2_524),
            (2_525, 2_974),
            (2_975, 3_474),
            (3_475, 4_024),
            (4_025, 4_624),
            (4_625, 5_324)
        };

        Check.Equal(
            "project-v3",
            PetInitialSavvyPolicy.Version,
            "initial-savvy policy version");
        Check.Equal(
            PetAptitudeCatalog.Count,
            PetInitialSavvyPolicy.All.Count,
            "initial-savvy policy covers every aptitude");

        var previousCount = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            var bracket = PetInitialSavvyPolicy.All[index];
            Check.Equal(
                (short)(index + 1),
                bracket.AptitudeValue,
                $"initial-savvy aptitude {index + 1}");
            Check.Equal(
                expected[index].Minimum,
                bracket.MinimumTotalSavvy,
                $"initial-savvy minimum {index + 1}");
            Check.Equal(
                expected[index].Maximum,
                bracket.MaximumTotalSavvy,
                $"initial-savvy maximum {index + 1}");
            Check.Equal(
                0.12m,
                bracket.MaximumStatDeviationFraction,
                $"initial-savvy spread {index + 1}");
            Check.True(
                bracket.TotalCount >= previousCount,
                $"{bracket.Aptitude} bracket does not narrow");
            previousCount = bracket.TotalCount;
        }

        Check.Equal(
            3_376,
            PetInitialSavvyPolicy.All.Sum(
                static bracket => bracket.TotalCount),
            "initial-savvy ranges contain the intended allowed totals");
        Check.True(
            PetInitialSavvyPolicy.All[^1].MinimumTotalSavvy >
            PetInitialSavvyPolicy.All[0].MaximumTotalSavvy * 20,
            "top aptitude materially exceeds Weak");
    }

    private static void CheckDeterministicDistribution()
    {
        var first = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_750,
            new Random(3_750));
        var second = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_750,
            new Random(3_750));

        Check.Equal(
            first,
            second,
            "equal seeds produce equal initial-savvy distributions");
        CheckDistribution(
            first,
            PetInitialSavvyPolicy.All[13],
            "Godly deterministic distribution");
    }

    private static void CheckEveryTotalAndBoundaryRng()
    {
        var cases = 0;
        foreach (var bracket in PetInitialSavvyPolicy.All)
        {
            for (var total = bracket.MinimumTotalSavvy;
                 total <= bracket.MaximumTotalSavvy;
                 total++)
            {
                cases++;
                CheckDistribution(
                    PetInitialSavvyPolicy.Distribute(
                        bracket.Aptitude,
                        total,
                        new Random(
                            HashCode.Combine(
                                bracket.AptitudeValue,
                                total))),
                    bracket,
                    $"{bracket.Aptitude} {total} seeded");
                CheckDistribution(
                    PetInitialSavvyPolicy.Distribute(
                        bracket.Aptitude,
                        total,
                        new MinimumRandom()),
                    bracket,
                    $"{bracket.Aptitude} {total} lower RNG");
                CheckDistribution(
                    PetInitialSavvyPolicy.Distribute(
                        bracket.Aptitude,
                        total,
                        new MaximumRandom()),
                    bracket,
                    $"{bracket.Aptitude} {total} upper RNG");
            }
        }

        Check.Equal(
            3_376,
            cases,
            "every allowed whole-point total was tested");
    }

    private static void CheckRollBoundaries()
    {
        foreach (var bracket in PetInitialSavvyPolicy.All)
        {
            var minimum = PetInitialSavvyPolicy.Roll(
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

            var maximum = PetInitialSavvyPolicy.Roll(
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

    private static void CheckSecurelySeededRandom()
    {
        var random = new Random(
            RandomNumberGenerator.GetInt32(int.MaxValue));
        foreach (var bracket in PetInitialSavvyPolicy.All)
        {
            var roll = PetInitialSavvyPolicy.Roll(
                bracket.Aptitude,
                random);
            Check.True(
                roll.TotalSavvy >= bracket.MinimumTotalSavvy &&
                roll.TotalSavvy <= bracket.MaximumTotalSavvy,
                $"{bracket.Aptitude} securely seeded total is bounded");
            CheckDistribution(
                roll,
                bracket,
                $"{bracket.Aptitude} securely seeded roll");
        }
    }

    private static void CheckValidation()
    {
        Check.True(
            !PetInitialSavvyPolicy.TryGet(
                (PetAptitude)0,
                out _),
            "unknown aptitude lookup fails closed");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetInitialSavvyPolicy.Roll(
                (PetAptitude)0,
                new Random(1)),
            "unknown aptitude cannot roll initial savvy");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetInitialSavvyPolicy.Distribute(
                PetAptitude.Weak,
                24,
                new Random(1)),
            "total below its bracket is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetInitialSavvyPolicy.Distribute(
                PetAptitude.Transcendent,
                5_325,
                new Random(1)),
            "total above its bracket is rejected");
        Check.Throws<ArgumentNullException>(
            () => PetInitialSavvyPolicy.Roll(
                PetAptitude.Weak,
                null!),
            "roll requires a caller-owned Random");
        Check.Throws<ArgumentNullException>(
            () => PetInitialSavvyPolicy.Distribute(
                PetAptitude.Weak,
                25,
                null!),
            "distribution requires a caller-owned Random");
    }

    private static void CheckDistribution(
        PetInitialSavvyRoll roll,
        PetInitialSavvyBracket bracket,
        string context)
    {
        var values = Values(roll.InitialSavvy);
        Check.Equal(6, values.Length, $"{context} has six values");
        Check.Equal(
            roll.TotalSavvy,
            values.Sum(),
            $"{context} preserves its exact total");

        var mean = roll.TotalSavvy / (decimal)values.Length;
        var minimum = decimal.Ceiling(
            mean *
            (1m - bracket.MaximumStatDeviationFraction) *
            SavvyValueScale) / SavvyValueScale;
        var maximum = decimal.Floor(
            mean *
            (1m + bracket.MaximumStatDeviationFraction) *
            SavvyValueScale) / SavvyValueScale;
        foreach (var value in values)
        {
            Check.True(
                value > 0m,
                $"{context} value {value:0.00} remains positive");
            Check.True(
                value >= minimum && value <= maximum,
                $"{context} value {value:0.00} remains within {minimum:0.00}-{maximum:0.00}");
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
