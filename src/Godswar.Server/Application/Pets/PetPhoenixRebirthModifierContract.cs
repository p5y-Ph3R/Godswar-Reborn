namespace Godswar.Server.Application.Pets;

/// <summary>
/// Validates the completed-Rebirth modifier captured in a Phoenix preview.
/// </summary>
internal static class PetPhoenixRebirthModifierContract
{
    public const int MaximumCompletedRebirths = 100;
    public const decimal MinimumPerRebirth = 0.10m;
    public const decimal MaximumPerRebirth = 0.20m;

    private const int ValueScale = 100;

    public static bool IsValid(
        int completedRebirths,
        PetContentStatVector modifier)
    {
        if (completedRebirths is < 0 or > MaximumCompletedRebirths ||
            !modifier.IsNonNegative)
        {
            return false;
        }

        var minimum = completedRebirths * MinimumPerRebirth;
        var maximum = completedRebirths * MaximumPerRebirth;
        return IsWithin(modifier.Agility, minimum, maximum) &&
            IsWithin(modifier.Strength, minimum, maximum) &&
            IsWithin(modifier.Accuracy, minimum, maximum) &&
            IsWithin(modifier.Technique, minimum, maximum) &&
            IsWithin(modifier.Wisdom, minimum, maximum) &&
            IsWithin(modifier.Luck, minimum, maximum);
    }

    private static bool IsWithin(
        decimal value,
        decimal minimum,
        decimal maximum) =>
        value >= minimum &&
        value <= maximum &&
        value * ValueScale == decimal.Truncate(value * ValueScale);
}
