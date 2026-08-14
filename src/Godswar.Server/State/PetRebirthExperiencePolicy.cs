using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

/// <summary>
internal readonly record struct PetRebirthExperienceCarry(
    long HistoricalSurplusExperience,
    long PreRebirthUnspentExperience,
    long TotalExperience);

/// Refunds the historical transition cost of every complete level above the
/// configured rebirth gate and preserves the pet's unspent EXP pool. The
/// complete result must remain representable by the native uint32 field.
/// </summary>
internal static class PetRebirthExperiencePolicy
{
    public static bool TryCalculateCarry(
        IPetContentCatalog content,
        int petLevel,
        int requiredLevel,
        long currentExperience,
        out PetRebirthExperienceCarry carry)
    {
        ArgumentNullException.ThrowIfNull(content);
        carry = default;
        if (requiredLevel < content.Settings.MinimumLevel ||
            requiredLevel > content.Settings.MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredLevel));
        }
        if (petLevel < requiredLevel ||
            petLevel > content.Settings.MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(petLevel));
        }

        if (currentExperience < 0 ||
            currentExperience >
                PetExperienceItemPolicy.MaximumNativePetExperience)
        {
            return false;
        }

        var historicalSurplus = 0L;
        var total = currentExperience;
        for (var level = requiredLevel; level < petLevel; level++)
        {
            var cost = content.RequiredExperienceForNextLevel(level);
            if (cost < 0 ||
                total >
                    PetExperienceItemPolicy.MaximumNativePetExperience - cost)
            {
                return false;
            }
            historicalSurplus = checked(historicalSurplus + cost);
            total += cost;
        }
        carry = new(
            historicalSurplus,
            currentExperience,
            total);
        return true;
    }
}
