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
            [0] = (0.01m, 0.20m),
            [1] = (0.02m, 0.20m),
            [2] = (0.04m, 0.20m),
            [3] = (0.06m, 0.20m),
            [4] = (0.08m, 0.20m),
            [5] = (0.10m, 0.20m)
        };

        foreach (var (spirits, range) in expected)
        {
            var actual =
                PetRebirthSpiritPolicy.GetIncreaseRange(spirits);
            Check.Equal(
                range.Min,
                actual.Minimum,
                $"rebirth {spirits}-spirit minimum increase");
            Check.Equal(
                range.Max,
                actual.Maximum,
                $"rebirth {spirits}-spirit maximum increase");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => PetRebirthSpiritPolicy.GetIncreaseRange(-1),
            "negative rebirth spirit count is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetRebirthSpiritPolicy.GetIncreaseRange(6),
            "six rebirth spirits are rejected");
    }

    private static void CheckRolls()
    {
        var current = new PetSavvy(1m, 2m, 3m, 4m, 5m, 6m);
        var minimum = PetRebirthSpiritPolicy.Roll(
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

        var maximum = PetRebirthSpiritPolicy.Roll(
            100,
            5,
            current,
            new MaximumRandom());
        Check.Equal(
            new PetSavvy(0.20m, 0.20m, 0.20m, 0.20m, 0.20m, 0.20m),
            maximum.Increase,
            "maximum rebirth increase");

        var zeroSpirit = PetRebirthSpiritPolicy.Roll(
            1,
            0,
            current,
            new ConstantRandom(0));
        Check.Equal(
            new PetSavvy(0.01m, 0.01m, 0.01m, 0.01m, 0.01m, 0.01m),
            zeroSpirit.Increase,
            "zero-spirit rebirth retains the stock minimum roll");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetRebirthSpiritPolicy.Roll(
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
            PetRebirthSpiritPolicy.IsValidOutcome(
                60,
                5,
                current,
                new PetSavvy(
                    1.10m,
                    1.11m,
                    1.12m,
                    1.13m,
                    1.19m,
                    1.20m)),
            "all per-stat rebirth deltas inside q5 are accepted");
        Check.True(
            !PetRebirthSpiritPolicy.IsValidOutcome(
                60,
                5,
                current,
                current with { Agility = 1.09m }),
            "too-small rebirth delta is rejected");
        Check.True(
            !PetRebirthSpiritPolicy.IsValidOutcome(
                60,
                5,
                current,
                current with { Agility = 1.21m }),
            "too-large rebirth delta is rejected");
        Check.True(
            !PetRebirthSpiritPolicy.IsValidOutcome(
                60,
                5,
                current,
                current with { Agility = 1.105m }),
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
