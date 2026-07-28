using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetRebirthGrowthPolicyChecks
{
    public static Task RunAsync()
    {
        CheckTiers();
        CheckRanges();
        CheckRolls();
        CheckOutcomeValidation();
        return Task.CompletedTask;
    }

    private static void CheckTiers()
    {
        Check.Equal(3, PetRebirthGrowthPolicy.Tiers.Count, "rebirth tiers");
        Check.True(
            PetRebirthGrowthPolicy.TryGetTier(1, out var first) &&
            first.FirstRebirth == 1 &&
            first.LastRebirth == 30 &&
            first.ChanceItemId == PetItemCatalog.SpringWater,
            "rebirths 1-30 use Spring Water");
        Check.True(
            PetRebirthGrowthPolicy.TryGetTier(31, out var second) &&
            second.LastRebirth == 60 &&
            second.ChanceItemId == PetItemCatalog.JuiceOfRebirth,
            "rebirths 31-60 use Juice of Rebirth");
        Check.True(
            PetRebirthGrowthPolicy.TryGetTier(61, out var third) &&
            third.LastRebirth == 100 &&
            third.ChanceItemId ==
                PetRebirthGrowthPolicy.AmbrosiaOfRebirthItemId,
            "rebirths 61-100 use Ambrosia of Rebirth");
        Check.True(
            !PetRebirthGrowthPolicy.TryGetTier(0, out _) &&
            !PetRebirthGrowthPolicy.TryGetTier(101, out _),
            "rebirth tiers are bounded to 1-100");
    }

    private static void CheckRanges()
    {
        var expected = new Dictionary<int, (decimal Min, decimal Max)>
        {
            [1] = (0.10m, 0.20m),
            [30] = (0.10m, 0.20m),
            [31] = (0.10m, 0.20m),
            [45] = (0.20m, 0.30m),
            [60] = (0.30m, 0.40m),
            [61] = (0.30m, 0.40m),
            [80] = (0.40m, 0.50m),
            [100] = (0.50m, 0.60m)
        };

        foreach (var (rebirth, range) in expected)
        {
            var actual =
                PetRebirthGrowthPolicy.GetIncreaseRange(rebirth);
            Check.Equal(
                range.Min,
                actual.Minimum,
                $"rebirth {rebirth} minimum increase");
            Check.Equal(
                range.Max,
                actual.Maximum,
                $"rebirth {rebirth} maximum increase");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => PetRebirthGrowthPolicy.GetIncreaseRange(0),
            "rebirth zero is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetRebirthGrowthPolicy.GetIncreaseRange(101),
            "rebirth 101 is rejected");
    }

    private static void CheckRolls()
    {
        var current = new PetSavvy(1m, 2m, 3m, 4m, 5m, 6m);
        var minimum = PetRebirthGrowthPolicy.Roll(
            1,
            5,
            current,
            new ConstantRandom(0));
        Check.Equal(
            new PetSavvy(0.10m, 0.10m, 0.10m, 0.10m, 0.10m, 0.10m),
            minimum.Increase,
            "minimum rebirth increase");
        Check.Equal(
            new PetSavvy(1.10m, 2.10m, 3.10m, 4.10m, 5.10m, 6.10m),
            minimum.GrowthAccelerationAfter,
            "rebirth increase is cumulative");

        var maximum = PetRebirthGrowthPolicy.Roll(
            100,
            5,
            current,
            new MaximumRandom());
        Check.Equal(
            new PetSavvy(0.60m, 0.60m, 0.60m, 0.60m, 0.60m, 0.60m),
            maximum.Increase,
            "maximum rebirth increase");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PetRebirthGrowthPolicy.Roll(
                1,
                4,
                current,
                Random.Shared),
            "rebirth requires exactly five spirits");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetRebirthGrowthPolicy.Roll(
                1,
                6,
                current,
                Random.Shared),
            "rebirth rejects extra spirits");
    }

    private static void CheckOutcomeValidation()
    {
        var current = new PetSavvy(1m, 1m, 1m, 1m, 1m, 1m);
        Check.True(
            PetRebirthGrowthPolicy.IsValidOutcome(
                60,
                current,
                new PetSavvy(
                    1.30m,
                    1.31m,
                    1.32m,
                    1.33m,
                    1.39m,
                    1.40m)),
            "all per-stat rebirth deltas inside the tier are accepted");
        Check.True(
            !PetRebirthGrowthPolicy.IsValidOutcome(
                60,
                current,
                current with { Agility = 1.29m }),
            "too-small rebirth delta is rejected");
        Check.True(
            !PetRebirthGrowthPolicy.IsValidOutcome(
                60,
                current,
                current with { Agility = 1.41m }),
            "too-large rebirth delta is rejected");
        Check.True(
            !PetRebirthGrowthPolicy.IsValidOutcome(
                60,
                current,
                current with { Agility = 1.305m }),
            "sub-cent rebirth delta is rejected");
    }

    private sealed class ConstantRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) =>
            minValue + Math.Min(value, maxValue - minValue - 1);
    }

    private sealed class MaximumRandom : Random
    {
        public override int Next(int minValue, int maxValue) =>
            maxValue - 1;
    }
}
