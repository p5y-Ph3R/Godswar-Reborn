using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

/// <summary>
/// Phoenix redraws the completed-Rebirth contribution independently for
/// every Growth attribute. The completed count widens the nature roll by
/// 0.10..0.20 per Rebirth at integer-hundredth precision.
/// </summary>
internal static class PetPhoenixRebirthModifierPolicy
{
    public const decimal MinimumPerRebirth =
        PetPhoenixRebirthModifierContract.MinimumPerRebirth;
    public const decimal MaximumPerRebirth =
        PetPhoenixRebirthModifierContract.MaximumPerRebirth;

    private const int ValueScale = 100;

    public static PetSavvy Roll(int completedRebirths, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (completedRebirths is < 0 or >
            PetPhoenixRebirthModifierContract.MaximumCompletedRebirths)
        {
            throw new ArgumentOutOfRangeException(nameof(completedRebirths));
        }

        var minimum = checked(completedRebirths *
            decimal.ToInt32(MinimumPerRebirth * ValueScale));
        var maximum = checked(completedRebirths *
            decimal.ToInt32(MaximumPerRebirth * ValueScale));
        decimal Next() => random.Next(minimum, checked(maximum + 1)) /
            (decimal)ValueScale;
        return new PetSavvy(
            Next(), Next(), Next(), Next(), Next(), Next());
    }

    public static bool IsValid(int completedRebirths, PetSavvy modifier) =>
        PetPhoenixRebirthModifierContract.IsValid(
            completedRebirths,
            new PetContentStatVector(
                modifier.Agility,
                modifier.Strength,
                modifier.Accuracy,
                modifier.Technique,
                modifier.Wisdom,
                modifier.Luck));
}
