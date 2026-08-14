using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetNativeAptitudeProfileChecks
{
    private const string InstalledClientProfileSha256 =
        "76BF776AE820520FCF0CCE697245CAB663E09F728DB5BDF56EFA926F90924E6E";

    public static Task RunAsync()
    {
        CheckCompleteness();
        CheckRockElfProfiles();
        CheckFailClosedLookup();
        CheckInstalledClientFingerprint();
        return Task.CompletedTask;
    }

    private static void CheckCompleteness()
    {
        var expectedAptitudes = new short[]
        {
            1, 2, 3, 4, 5, 7, 8, 9, 10, 12, 14
        };

        Check.Equal(
            495,
            PetNativeAptitudeProfileCatalog.All.Count,
            "all 45 x 11 Pet_Confect aptitude rows are cataloged");
        Check.True(
            PetNativeAptitudeProfileCatalog.SupportedAptitudes
                .Select(static aptitude => (short)aptitude)
                .SequenceEqual(expectedAptitudes),
            "only the eleven client-authored aptitude values are supported");

        foreach (var species in PetSpeciesCatalog.All)
        {
            var profiles = PetNativeAptitudeProfileCatalog.All
                .Where(profile => profile.SpeciesType == species.Type)
                .ToArray();
            Check.Equal(
                expectedAptitudes.Length,
                profiles.Length,
                $"{species.DisplayName} has every native aptitude row");
            Check.True(
                profiles
                    .Select(static profile => profile.AptitudeValue)
                    .SequenceEqual(expectedAptitudes),
                $"{species.DisplayName} native aptitude rows remain ordered");
            Check.True(
                profiles.All(profile =>
                    profile.StarterSkillId == species.StarterSkillId),
                $"{species.DisplayName} aptitude profiles retain its starter skill");
            Check.True(
                profiles
                    .Select(static profile => profile.Lifetime)
                    .Distinct()
                    .Order()
                    .SequenceEqual(
                        species.ClientLifetimeValues
                            .Distinct()
                            .Order()),
                $"{species.DisplayName} aptitude lifetimes are complete");
        }
    }

    private static void CheckRockElfProfiles()
    {
        var aptitudeThree = RequiredRockElf(PetAptitude.Cowish);
        Check.Equal(
            new PetSavvy(50m, 300m, 120m, 0m, 150m, 80m),
            aptitudeThree.StartingTraits,
            "Rock Elf aptitude 3 starting traits");
        Check.Equal(
            new PetSavvy(10m, 45m, 25m, 7m, 30m, 17m),
            aptitudeThree.GeniusTraits,
            "Rock Elf aptitude 3 genius traits");
        CheckRockElfScalars(
            aptitudeThree,
            quality: 40,
            samsara: 1,
            genius: 1,
            skillCount: 2,
            lifetime: 600,
            "aptitude 3");

        var aptitudeNine = RequiredRockElf(PetAptitude.Zealous);
        Check.Equal(
            new PetSavvy(80m, 450m, 160m, 80m, 200m, 30m),
            aptitudeNine.StartingTraits,
            "Rock Elf aptitude 9 starting traits");
        Check.Equal(
            new PetSavvy(15m, 60m, 30m, 10m, 40m, 20m),
            aptitudeNine.GeniusTraits,
            "Rock Elf aptitude 9 genius traits");
        CheckRockElfScalars(
            aptitudeNine,
            quality: 80,
            samsara: 1,
            genius: 7,
            skillCount: 2,
            lifetime: 800,
            "aptitude 9");

        var aptitudeTen = RequiredRockElf(PetAptitude.Smart);
        Check.Equal(
            new PetSavvy(200m, 800m, 400m, 160m, 640m, 300m),
            aptitudeTen.StartingTraits,
            "Rock Elf aptitude 10 starting traits");
        Check.Equal(
            new PetSavvy(20m, 80m, 40m, 14m, 54m, 27m),
            aptitudeTen.GeniusTraits,
            "Rock Elf aptitude 10 genius traits");
        CheckRockElfScalars(
            aptitudeTen,
            quality: 150,
            samsara: 2,
            genius: 25,
            skillCount: 3,
            lifetime: 1_100,
            "aptitude 10");
    }

    private static void CheckFailClosedLookup()
    {
        foreach (var unsupported in new short[] { 6, 11, 13, 15, 16 })
        {
            Check.True(
                !PetNativeAptitudeProfileCatalog.TryGet(
                    speciesType: 1,
                    unsupported,
                    out _),
                $"Rock Elf aptitude {unsupported} fails closed");
        }

        Check.True(
            !PetNativeAptitudeProfileCatalog.TryGet(
                speciesType: 0,
                aptitudeValue: 3,
                out _),
            "unknown species below the native range fails closed");
        Check.True(
            !PetNativeAptitudeProfileCatalog.TryGet(
                speciesType: 46,
                aptitudeValue: 3,
                out _),
            "unknown species above the native range fails closed");
    }

    private static void CheckInstalledClientFingerprint()
    {
        var canonical = string.Join(
            '\n',
            PetNativeAptitudeProfileCatalog.All.Select(Canonical));
        var actual = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        Check.Equal(
            InstalledClientProfileSha256,
            actual,
            "native aptitude catalog matches the installed Pet_Confect.xml profile fingerprint");
    }

    private static PetNativeAptitudeProfile RequiredRockElf(
        PetAptitude aptitude)
    {
        Check.True(
            PetNativeAptitudeProfileCatalog.TryGet(
                speciesType: 1,
                aptitude,
                out var profile),
            $"Rock Elf {aptitude} profile resolves");
        return profile;
    }

    private static void CheckRockElfScalars(
        PetNativeAptitudeProfile profile,
        int quality,
        int samsara,
        int genius,
        int skillCount,
        int lifetime,
        string context)
    {
        Check.Equal(quality, profile.NativeQuality, $"{context} quality");
        Check.Equal(samsara, profile.NativeSamsara, $"{context} samsara");
        Check.Equal(genius, profile.NativeGenius, $"{context} genius");
        Check.Equal(405, profile.StarterSkillId, $"{context} starter skill");
        Check.Equal(
            skillCount,
            profile.NativeSkillCount,
            $"{context} skill count");
        Check.Equal(0, profile.NativeProcreate, $"{context} procreate");
        Check.Equal(lifetime, profile.Lifetime, $"{context} lifetime");
    }

    private static string Canonical(
        PetNativeAptitudeProfile profile) =>
        string.Join(
            '|',
            profile.SpeciesType.ToString(CultureInfo.InvariantCulture),
            profile.AptitudeValue.ToString(CultureInfo.InvariantCulture),
            Vector(profile.StartingTraits),
            Vector(profile.GeniusTraits),
            profile.NativeQuality.ToString(CultureInfo.InvariantCulture),
            profile.NativeSamsara.ToString(CultureInfo.InvariantCulture),
            profile.NativeGenius.ToString(CultureInfo.InvariantCulture),
            profile.StarterSkillId.ToString(CultureInfo.InvariantCulture),
            profile.NativeSkillCount.ToString(CultureInfo.InvariantCulture),
            profile.NativeProcreate.ToString(CultureInfo.InvariantCulture),
            profile.Lifetime.ToString(CultureInfo.InvariantCulture));

    private static string Vector(PetSavvy values) =>
        string.Join(
            ',',
            new[]
            {
                values.Agility,
                values.Strength,
                values.Accuracy,
                values.Technique,
                values.Wisdom,
                values.Luck
            }.Select(value =>
                value.ToString(CultureInfo.InvariantCulture)));
}
