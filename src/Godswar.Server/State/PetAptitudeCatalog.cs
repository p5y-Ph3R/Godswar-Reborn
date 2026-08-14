using System.Collections.Frozen;

namespace Godswar.Server.State;

internal enum PetAptitude : short
{
    Weak = 1,
    Fool = 2,
    Cowish = 3,
    Moderate = 4,
    Rational = 5,
    Calm = 6,
    Grumpy = 7,
    Brave = 8,
    Zealous = 9,
    Smart = 10,
    Overbearing = 11,
    Ferocious = 12,
    Almighty = 13,
    Godly = 14,
    Celestial = 15,
    Transcendent = 16
}

internal sealed record PetAptitudeDefinition(
    PetAptitude Aptitude,
    string NameKey,
    string DisplayName,
    bool IsServerExtension)
{
    public short Value => (short)Aptitude;
}

/// <summary>
/// Authoritative pet aptitude ladder. Values 1-14 preserve the installed
/// client's PETAPTITUDE labels. Values 15-16 replace its "Backup"
/// placeholders with explicit server extensions.
/// </summary>
internal static class PetAptitudeCatalog
{
    public const int Count = 16;

    public static IReadOnlyList<PetAptitudeDefinition> All { get; } =
        Array.AsReadOnly(
        [
            D(PetAptitude.Weak),
            D(PetAptitude.Fool),
            D(PetAptitude.Cowish),
            D(PetAptitude.Moderate),
            D(PetAptitude.Rational),
            D(PetAptitude.Calm),
            D(PetAptitude.Grumpy),
            D(PetAptitude.Brave),
            D(PetAptitude.Zealous),
            D(PetAptitude.Smart),
            D(PetAptitude.Overbearing),
            D(PetAptitude.Ferocious),
            D(PetAptitude.Almighty),
            D(PetAptitude.Godly),
            D(PetAptitude.Celestial, isServerExtension: true),
            D(PetAptitude.Transcendent, isServerExtension: true)
        ]);

    private static readonly FrozenDictionary<short, PetAptitudeDefinition>
        ByValue = All.ToFrozenDictionary(static definition => definition.Value);

    static PetAptitudeCatalog()
    {
        if (All.Count != Count ||
            !All.Select(static definition => (int)definition.Value)
                .SequenceEqual(Enumerable.Range(1, Count)) ||
            All.Select(static definition => definition.NameKey).Distinct().Count() != Count ||
            All.Select(static definition => definition.DisplayName).Distinct().Count() != Count)
        {
            throw new InvalidDataException(
                "The authoritative pet aptitude catalog is incomplete.");
        }
    }

    public static bool TryGet(
        short value,
        out PetAptitudeDefinition definition) =>
        ByValue.TryGetValue(value, out definition!);

    public static bool TryGet(
        PetAptitude aptitude,
        out PetAptitudeDefinition definition) =>
        TryGet((short)aptitude, out definition);

    private static PetAptitudeDefinition D(
        PetAptitude aptitude,
        bool isServerExtension = false) =>
        new(
            aptitude,
            $"PETAPTITUDE{(short)aptitude}",
            aptitude.ToString(),
            isServerExtension);
}
