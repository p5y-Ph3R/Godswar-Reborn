using Godswar.Server.Application.Pets;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Pets;

internal static class PetHatchRankContentBaseline
{
    public static PetHatchRankStepContentDefinition[] Create() =>
    [
        .. Pair(PetAptitude.Weak, PetAptitude.Fool, 0m, 0.30m, 0.40m),
        .. Pair(PetAptitude.Cowish, PetAptitude.Moderate, 0.30m, 0.40m, 0.80m),
        .. Pair(PetAptitude.Rational, PetAptitude.Calm, 0.40m, 0.80m, 1.00m),
        .. Pair(PetAptitude.Grumpy, PetAptitude.Brave, 0.80m, 1.00m, 1.50m),
        .. Pair(PetAptitude.Zealous, PetAptitude.Smart, 1.00m, 1.50m, 2.00m),
        .. Pair(PetAptitude.Overbearing, PetAptitude.Ferocious, 1.50m, 2.00m, 2.70m),
        .. Pair(PetAptitude.Almighty, PetAptitude.Godly, 2.00m, 2.70m, 3.00m),
        .. Pair(PetAptitude.Celestial, PetAptitude.Transcendent, 2.70m, 3.00m, 3.60m)
    ];

    private static PetHatchRankStepContentDefinition[] Pair(
        PetAptitude first,
        PetAptitude second,
        decimal low,
        decimal middle,
        decimal high) =>
    [
        .. Steps(first, low, middle, high),
        .. Steps(second, low, middle, high)
    ];

    private static PetHatchRankStepContentDefinition[] Steps(
        PetAptitude aptitude,
        decimal low,
        decimal middle,
        decimal high) =>
    [
        new((short)aptitude, 0, low, 60),
        new((short)aptitude, 1, middle, 30),
        new((short)aptitude, 2, high, 10)
    ];
}
