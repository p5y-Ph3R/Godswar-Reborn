using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetBasicSavvyRedistributionPolicyChecks
{
    public const string CheckName =
        "Fairy's Feather Basic-Savvy redistribution policy";

    public static Task RunAsync()
    {
        CheckV4ProbabilityBoundaries();
        CheckV4TierShapes();
        CheckV4FocusSelection();
        CheckV4DeterministicRandom();
        CheckV4EveryHundredthTotal();
        CheckV4MaximumSupportedTotal();
        CheckValidation();
        return Task.CompletedTask;
    }

    private static void CheckValidation()
    {
        Check.Throws<ArgumentNullException>(
            () => PetBasicSavvyRedistributionPolicy.Redistribute(
                CurrentSavvy(100), null!),
            "a caller-owned random source is required");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetBasicSavvyRedistributionPolicy.Redistribute(
                new PetContentStatVector(
                    -0.01m, 1.01m, 0m, 0m, 0m, 0m),
                new Random(1)),
            "negative Basic Savvy is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetBasicSavvyRedistributionPolicy.Redistribute(
                new PetContentStatVector(
                    1.001m, 1m, 1m, 1m, 1m, 1m),
                new Random(1)),
            "sub-hundredth Basic Savvy is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetBasicSavvyRedistributionPolicy.Redistribute(
                CurrentSavvy(99), new Random(1)),
            "a total too small for six positive allocations is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetBasicSavvyRedistributionPolicy.Redistribute(
                new PetContentStatVector(
                    21_474_836.48m, 0m, 0m, 0m, 0m, 0m),
                new Random(1)),
            "a total above the bounded native range is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetBasicSavvyRedistributionPolicy.ResolveTier(-1),
            "a negative percentile is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetBasicSavvyRedistributionPolicy.ResolveTier(100),
            "a percentile above 99 is rejected");
    }

    private static PetContentStatVector CurrentSavvy(int totalUnits) =>
        new(totalUnits / 100m, 0m, 0m, 0m, 0m, 0m);

    private static decimal[] Values(PetContentStatVector values) =>
    [
        values.Agility,
        values.Strength,
        values.Accuracy,
        values.Technique,
        values.Wisdom,
        values.Luck
    ];

    private static int ToUnits(decimal value) =>
        decimal.ToInt32(value * 100m);

    private static void CheckTier(
        PetBasicSavvyRedistributionTier expected,
        PetBasicSavvyRedistributionTier actual,
        string description) =>
        Check.Equal((byte)expected, (byte)actual, description);

    private sealed class HeadRandom(
        IEnumerable<int> head,
        int seed) : Random
    {
        private readonly Queue<int> _head = new(head);
        private readonly Random _inner = new(seed);

        public override int Next(int maxValue)
        {
            if (_head.Count == 0)
            {
                return _inner.Next(maxValue);
            }

            var value = _head.Dequeue();
            if (value < 0 || value >= maxValue)
            {
                throw new InvalidOperationException(
                    $"Scripted random value {value} is outside " +
                    $"0-{maxValue - 1}.");
            }
            return value;
        }

        public override int Next(int minValue, int maxValue) =>
            _inner.Next(minValue, maxValue);

        public override long NextInt64(long minValue, long maxValue) =>
            _inner.NextInt64(minValue, maxValue);
    }
}
