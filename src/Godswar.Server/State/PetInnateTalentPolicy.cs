namespace Godswar.Server.State;

/// <summary>
/// Assigns innate pet talents from aptitude alone. Talent items and client
/// profile fields are compatibility data and must never author this mask.
/// </summary>
internal static class PetInnateTalentPolicy
{
    public const byte SmartTalentMask =
        2 | // Quest Dispatch
        8 | // Healing
        16; // Merge

    public const byte GodlyTalentMask = PetTalentCatalog.SupportedMask;

    public static byte Resolve(PetAptitude aptitude)
    {
        if (!PetAptitudeCatalog.TryGet(aptitude, out _))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aptitude),
                aptitude,
                "Unsupported pet aptitude.");
        }

        return aptitude >= PetAptitude.Godly
            ? GodlyTalentMask
            : aptitude >= PetAptitude.Smart
                ? SmartTalentMask
                : (byte)0;
    }

    public static bool HasTalent(
        PetAptitude aptitude,
        PetTalentKind talent)
    {
        if (!PetTalentCatalog.TryGet(talent, out var definition))
        {
            return false;
        }

        return (Resolve(aptitude) & definition.MaskBit) != 0;
    }
}
