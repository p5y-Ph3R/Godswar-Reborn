namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Project-authored pet Healing balance. Version changes must be deliberate
/// because the original client's exact level formula was not recoverable.
/// </summary>
internal static class PetHealingTalentPolicy
{
    public const int Version = 2;
    public const short HealingTalentMaskBit = 8;
    public const int TriggerThresholdBasisPoints = 4_000;
    public const int BasisPointScale = 10_000;
    public const int MaximumPetLevel = 120;
    public const int MinimumLevelEffectivenessBasisPoints = 5_000;
    public const uint CombatTextSkillId = 0;

    public static readonly TimeSpan Cooldown =
        TimeSpan.FromSeconds(180);

    public static bool IsAtOrBelowTriggerThreshold(
        int currentHealth,
        int maximumHealth)
    {
        if (maximumHealth <= 0 ||
            currentHealth <= 0 ||
            currentHealth > maximumHealth)
        {
            return false;
        }

        return (long)currentHealth * BasisPointScale <=
               (long)maximumHealth * TriggerThresholdBasisPoints;
    }

    public static PetHealingAmount ResolveAmount(
        short aptitude,
        int petLevel,
        int currentHealth,
        int maximumHealth)
    {
        if (petLevel <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(petLevel));
        }
        if (maximumHealth <= 0 ||
            currentHealth < 0 ||
            currentHealth > maximumHealth)
        {
            throw new ArgumentOutOfRangeException(nameof(currentHealth));
        }

        var maximumHealBasisPoints =
            ResolveMaximumHealBasisPoints(aptitude);
        if (maximumHealBasisPoints == 0)
        {
            return new PetHealingAmount(0, 0);
        }

        var boundedPetLevel = Math.Min(petLevel, MaximumPetLevel);
        var levelProgress = (boundedPetLevel - 1m) /
            (MaximumPetLevel - 1m);
        var levelEffectiveness =
            (MinimumLevelEffectivenessBasisPoints /
             (decimal)BasisPointScale) +
            ((1m -
              (MinimumLevelEffectivenessBasisPoints /
               (decimal)BasisPointScale)) * levelProgress);
        var resolved = decimal.ToInt32(decimal.Round(
            maximumHealth *
            (maximumHealBasisPoints / (decimal)BasisPointScale) *
            levelEffectiveness,
            0,
            MidpointRounding.AwayFromZero));
        var applied = Math.Min(
            resolved,
            maximumHealth - currentHealth);
        return new PetHealingAmount(resolved, applied);
    }

    public static int ResolveMaximumHealBasisPoints(short aptitude) =>
        aptitude switch
        {
            >= 1 and <= 9 => 0,
            10 => 1_200, // Smart
            11 => 1_400, // Overbearing
            12 => 1_600, // Ferocious
            13 => 1_800, // Almighty
            14 => 2_000, // Godly
            15 => 2_200, // Celestial
            16 => 2_500, // Transcendent
            _ => throw new ArgumentOutOfRangeException(
                nameof(aptitude),
                aptitude,
                "Unsupported pet aptitude.")
        };
}

internal readonly record struct PetHealingAmount(
    int Resolved,
    int Applied);
