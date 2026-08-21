using Godswar.Server.Application.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetOwnerMergeChecks
{
    private static void CheckTwentyThousandSavvyGoldenVectors()
    {
        var technique = CalculateSingleSavvy(
            new PetSavvy(0m, 0m, 0m, 20_000m, 0m, 0m));
        Check.True(
            technique.PhysicalDamageReduction == 122_058m &&
            technique.MagicDamageReduction == 101_695m &&
            technique.TechniquePhysicalReduction == 3_000m &&
            technique.TechniqueMagicReduction == 3_000m,
            "20k Technique pins doubled fixed cancellation and capped percentage reduction");

        var wisdom = CalculateSingleSavvy(
            new PetSavvy(0m, 0m, 0m, 0m, 20_000m, 0m));
        Check.True(
            wisdom.PhysicalDamageIncrease == 50_807.5m &&
            decimal.Round(
                wisdom.PhysicalDamageIncrease,
                0,
                MidpointRounding.AwayFromZero) == 50_808m &&
            wisdom.CriticalDamageReduction == 152_622.5m &&
            decimal.Round(
                wisdom.CriticalDamageReduction,
                0,
                MidpointRounding.AwayFromZero) == 152_623m,
            "20k Wisdom pins fixed physical append and critical cancellation");

        var strength = CalculateSingleSavvy(
            new PetSavvy(0m, 20_000m, 0m, 0m, 0m, 0m));
        Check.True(
            strength.LifeAbsorption == 50_707.5m &&
            decimal.Round(
                strength.LifeAbsorption,
                0,
                MidpointRounding.AwayFromZero) == 50_708m,
            "20k Strength pins fixed on-hit healing");

        var luck = CalculateSingleSavvy(
            new PetSavvy(0m, 0m, 0m, 0m, 0m, 20_000m));
        Check.True(
            luck.MagicDamageIncrease == 40_636m &&
            luck.DamageRebound == 60_879m,
            "20k Luck pins fixed magic append and damage rebound");
    }

    private static PetOwnerStatContribution CalculateSingleSavvy(
        PetSavvy savvy) =>
        PetOwnerMergeContributionCalculator.Calculate(
            savvy,
            OwnerMergeContent);
}
