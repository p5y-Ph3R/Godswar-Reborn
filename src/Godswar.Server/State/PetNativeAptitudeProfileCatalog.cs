using System.Collections.Frozen;

namespace Godswar.Server.State;

/// <summary>
/// One aptitude row from the installed client's Pet_Confect.xml. Trait vectors
/// retain their authored six-value order. These are native creation facts,
/// separate from the project's randomized base-growth policy.
/// </summary>
internal sealed record PetNativeAptitudeProfile(
    int SpeciesType,
    PetAptitude Aptitude,
    PetSavvy StartingTraits,
    PetSavvy GeniusTraits,
    int NativeQuality,
    int NativeSamsara,
    int NativeGenius,
    int StarterSkillId,
    int NativeSkillCount,
    int NativeProcreate,
    int Lifetime)
{
    public short AptitudeValue => (short)Aptitude;
}

/// <summary>
/// Compact, authoritative transcription of the 495 aptitude rows in the
/// stock English client's Pet_Confect.xml.
///
/// The client only authors aptitudes 1,2,3,4,5,7,8,9,10,12,14. Lookup is
/// deliberately fail-closed for all other values, including server extension
/// aptitudes 15 and 16.
/// </summary>
internal static class PetNativeAptitudeProfileCatalog
{
    public const int NativeAptitudeCount = 11;
    public const int ProfileCount =
        PetSpeciesCatalog.SpeciesCount * NativeAptitudeCount;

    public static IReadOnlyList<PetAptitude> SupportedAptitudes { get; } =
        Array.AsReadOnly(
        [
            PetAptitude.Weak,
            PetAptitude.Fool,
            PetAptitude.Cowish,
            PetAptitude.Moderate,
            PetAptitude.Rational,
            PetAptitude.Grumpy,
            PetAptitude.Brave,
            PetAptitude.Zealous,
            PetAptitude.Smart,
            PetAptitude.Ferocious,
            PetAptitude.Godly
        ]);

    public static IReadOnlyList<PetNativeAptitudeProfile> All { get; } =
        CreateAll();

    private static readonly FrozenDictionary<
        (int SpeciesType, short Aptitude),
        PetNativeAptitudeProfile> ByKey =
        All.ToFrozenDictionary(
            static profile =>
                (profile.SpeciesType, profile.AptitudeValue));

    static PetNativeAptitudeProfileCatalog()
    {
        if (All.Count != ProfileCount ||
            SupportedAptitudes.Count != NativeAptitudeCount ||
            !SupportedAptitudes
                .Select(static aptitude => (short)aptitude)
                .SequenceEqual(
                    new short[] { 1, 2, 3, 4, 5, 7, 8, 9, 10, 12, 14 }))
        {
            throw new InvalidDataException(
                "The native pet aptitude catalog is incomplete.");
        }

        foreach (var species in PetSpeciesCatalog.All)
        {
            var profiles = All
                .Where(profile => profile.SpeciesType == species.Type)
                .ToArray();
            if (profiles.Length != NativeAptitudeCount ||
                !profiles
                    .Select(static profile => profile.AptitudeValue)
                    .SequenceEqual(
                        SupportedAptitudes.Select(
                            static aptitude => (short)aptitude)) ||
                profiles.Any(profile =>
                    profile.StarterSkillId != species.StarterSkillId ||
                    profile.Lifetime <= 0 ||
                    profile.NativeSkillCount <= 0))
            {
                throw new InvalidDataException(
                    $"Native aptitude profiles for species {species.Type} are incomplete.");
            }

            var catalogLifetimes = profiles
                .Select(static profile => profile.Lifetime)
                .Distinct()
                .Order()
                .ToArray();
            var speciesLifetimes = species.ClientLifetimeValues
                .Distinct()
                .Order()
                .ToArray();
            if (!catalogLifetimes.SequenceEqual(speciesLifetimes))
            {
                throw new InvalidDataException(
                    $"Native lifetime profiles disagree for species {species.Type}.");
            }
        }
    }

    public static bool TryGet(
        int speciesType,
        short aptitudeValue,
        out PetNativeAptitudeProfile profile) =>
        ByKey.TryGetValue(
            (speciesType, aptitudeValue),
            out profile!);

    public static bool TryGet(
        int speciesType,
        PetAptitude aptitude,
        out PetNativeAptitudeProfile profile) =>
        TryGet(speciesType, (short)aptitude, out profile);

    private static IReadOnlyList<PetNativeAptitudeProfile> CreateAll()
    {
        var defaults = CreateSpeciesDefaults();
        var overrides = CreateOverrides().ToFrozenDictionary(
            static profile =>
                (profile.SpeciesType, profile.AptitudeValue));
        var profiles =
            new List<PetNativeAptitudeProfile>(ProfileCount);

        foreach (var defaultsForSpecies in defaults)
        {
            if (!PetSpeciesCatalog.TryGet(
                    defaultsForSpecies.SpeciesType,
                    out var species))
            {
                throw new InvalidDataException(
                    $"Unknown native pet species {defaultsForSpecies.SpeciesType}.");
            }

            foreach (var aptitude in SupportedAptitudes)
            {
                var key = (
                    defaultsForSpecies.SpeciesType,
                    (short)aptitude);
                profiles.Add(
                    overrides.TryGetValue(key, out var authored)
                        ? authored with
                        {
                            StarterSkillId = species.StarterSkillId
                        }
                        : new PetNativeAptitudeProfile(
                            species.Type,
                            aptitude,
                            PetSavvy.Zero,
                            PetSavvy.Zero,
                            NativeQuality: 0,
                            NativeSamsara: 0,
                            NativeGenius: 0,
                            species.StarterSkillId,
                            defaultsForSpecies.NativeSkillCount,
                            NativeProcreate: 0,
                            defaultsForSpecies.Lifetime));
            }
        }

        return Array.AsReadOnly(profiles.ToArray());
    }

    private static IReadOnlyList<PetSpeciesProfileDefaults>
        CreateSpeciesDefaults() =>
        [
            D(1, 2, 600),
            D(2, 1, 400),
            D(3, 1, 400),
            D(4, 3, 1_200),
            D(5, 2, 900),
            D(6, 1, 500),
            D(7, 1, 600),
            D(8, 3, 1_500),
            D(9, 3, 1_200),
            D(10, 1, 500),
            D(11, 3, 1_500),
            D(12, 3, 1_500),
            .. Enumerable.Range(13, 33)
                .Select(static type => D(type, 3, 1_200))
        ];

    private static IReadOnlyList<PetNativeAptitudeProfile>
        CreateOverrides() =>
        [
            P(1, 3, S(50, 300, 120, 0, 150, 80), S(10, 45, 25, 7, 30, 17), 40, 1, 1, 2, 600),
            P(1, 9, S(80, 450, 160, 80, 200, 30), S(15, 60, 30, 10, 40, 20), 80, 1, 7, 2, 800),
            P(1, 10, S(200, 800, 400, 160, 640, 300), S(20, 80, 40, 14, 54, 27), 150, 2, 25, 3, 1_100),
            P(2, 1, S(150, 0, 50, 70, 0, 30), S(30, 3, 15, 20, 5, 10), 0, 0, 2, 1, 400),
            P(2, 4, S(200, 0, 80, 100, 20, 50), S(40, 6, 20, 27, 9, 14), 30, 0, 7, 1, 500),
            P(2, 5, S(280, 20, 130, 140, 0, 80), S(50, 8, 25, 34, 12, 17), 80, 1, 7, 2, 700),
            P(2, 8, S(400, 30, 80, 250, 20, 120), S(60, 10, 30, 40, 15, 20), 150, 1, 23, 3, 1_000),
            P(2, 14, S(280, 20, 130, 140, 0, 80), S(50, 8, 25, 34, 12, 17), 100, 1, 23, 2, 800),
            P(3, 2, S(0, 30, 70, 50, 150, 0), S(3, 10, 20, 15, 30, 5), 0, 0, 4, 1, 400),
            P(3, 4, S(20, 50, 100, 80, 200, 0), S(6, 14, 27, 20, 40, 9), 30, 0, 7, 1, 500),
            P(3, 9, S(0, 80, 140, 130, 280, 20), S(8, 17, 34, 25, 50, 12), 80, 1, 7, 2, 700),
            P(3, 12, S(20, 120, 250, 80, 400, 30), S(10, 20, 40, 30, 60, 15), 150, 1, 23, 3, 1_000),
            P(3, 14, S(0, 80, 140, 130, 280, 20), S(8, 17, 34, 25, 50, 12), 100, 1, 23, 2, 800),
            P(4, 7, S(400, 200, 160, 500, 740, 1_000), S(35, 25, 18, 50, 70, 100), 300, 0, 25, 3, 1_200),
            P(5, 1, S(20, 0, 130, 280, 140, 80), S(10, 7, 25, 45, 30, 17), 100, 0, 7, 2, 900),
            P(5, 7, S(240, 880, 1_200, 200, 600, 480), S(25, 70, 100, 18, 50, 35), 360, 0, 29, 3, 1_500),
            P(6, 4, S(100, 80, 200, 0, 50, 20), S(27, 20, 40, 6, 14, 9), 30, 0, 1, 1, 500),
            P(6, 8, S(670, 450, 900, 180, 140, 360), S(45, 63, 90, 22, 16, 32), 270, 0, 19, 2, 1_000),
            P(8, 7, S(1_200, 600, 200, 480, 240, 880), S(100, 50, 18, 35, 25, 70), 360, 0, 29, 3, 1_500),
            P(10, 5, S(250, 100, 300, 150, 200, 500), S(30, 10, 40, 15, 20, 60), 200, 0, 1, 2, 700),
            P(10, 10, S(300, 150, 400, 200, 250, 700), S(40, 14, 54, 20, 27, 80), 300, 0, 7, 3, 1_000),
            P(11, 7, S(600, 1_200, 880, 200, 480, 240), S(50, 100, 70, 18, 35, 25), 360, 0, 27, 3, 1_500),
            P(12, 7, S(240, 600, 200, 1_200, 880, 480), S(25, 50, 18, 100, 70, 35), 360, 0, 27, 3, 1_500)
        ];

    private static PetNativeAptitudeProfile P(
        int speciesType,
        short aptitude,
        PetSavvy startingTraits,
        PetSavvy geniusTraits,
        int nativeQuality,
        int nativeSamsara,
        int nativeGenius,
        int nativeSkillCount,
        int lifetime) =>
        new(
            speciesType,
            (PetAptitude)aptitude,
            startingTraits,
            geniusTraits,
            nativeQuality,
            nativeSamsara,
            nativeGenius,
            StarterSkillId: 0,
            nativeSkillCount,
            NativeProcreate: 0,
            lifetime);

    private static PetSpeciesProfileDefaults D(
        int speciesType,
        int nativeSkillCount,
        int lifetime) =>
        new(speciesType, nativeSkillCount, lifetime);

    private static PetSavvy S(
        decimal first,
        decimal second,
        decimal third,
        decimal fourth,
        decimal fifth,
        decimal sixth) =>
        new(first, second, third, fourth, fifth, sixth);

    private sealed record PetSpeciesProfileDefaults(
        int SpeciesType,
        int NativeSkillCount,
        int Lifetime);
}
