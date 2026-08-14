namespace Godswar.Server.Application.Pets;

internal sealed record PetRebirthGrowthEvidence(
    PetContentStatVector Increase)
{
    public bool IsValid =>
        IsHundredth(Increase.Agility) &&
        IsHundredth(Increase.Strength) &&
        IsHundredth(Increase.Accuracy) &&
        IsHundredth(Increase.Technique) &&
        IsHundredth(Increase.Wisdom) &&
        IsHundredth(Increase.Luck);

    public decimal[] ToOrderedIncrease() =>
    [
        Increase.Agility,
        Increase.Strength,
        Increase.Accuracy,
        Increase.Technique,
        Increase.Wisdom,
        Increase.Luck
    ];

    private static bool IsHundredth(decimal value) =>
        value is >= 0.01m and <= 0.20m &&
        value * 100m == decimal.Truncate(value * 100m);
}
