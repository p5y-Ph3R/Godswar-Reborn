using Godswar.Server.Application.Coordination;

namespace Godswar.Server.ProtocolChecks;

internal static class RuntimeContentFingerprintChecks
{
    public const string CheckName =
        "Combined gameplay and Holy-balance runtime-content fingerprint";

    public static Task RunAsync()
    {
        const string world =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string items =
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        const string pets =
            "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        const string ownerMerge =
            "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";
        const string learnedSkills =
            "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";
        const string holyBalance =
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";
        var baseline = RuntimeContentFingerprint.Create(
            world,
            items,
            pets,
            ownerMerge,
            learnedSkills,
            holyBalance);

        Check.Equal(64, baseline.Length, "combined fingerprint SHA-256 length");
        Check.Equal(
            baseline,
            RuntimeContentFingerprint.Create(
                world,
                items,
                pets,
                ownerMerge,
                learnedSkills,
                holyBalance),
            "combined fingerprint determinism");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    "CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    items,
                    pets,
                    ownerMerge,
                    learnedSkills,
                    holyBalance),
                StringComparison.Ordinal),
            "world revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    "DBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                    pets,
                    ownerMerge,
                    learnedSkills,
                    holyBalance),
            StringComparison.Ordinal),
            "item revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    items,
                    "ECCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
                    ownerMerge,
                    learnedSkills,
                    holyBalance),
                StringComparison.Ordinal),
            "pet revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    items,
                    pets,
                    "FDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD",
                    learnedSkills,
                    holyBalance),
                StringComparison.Ordinal),
            "owner-Merge revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    items,
                    pets,
                    ownerMerge,
                    "FEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE",
                    holyBalance),
                StringComparison.Ordinal),
            "learned pet-skill revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    items,
                    pets,
                    ownerMerge,
                    learnedSkills,
                    "AFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"),
                StringComparison.Ordinal),
            "Holy Spirit balance participates in worker compatibility");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world.ToLowerInvariant(),
                items,
                pets,
                ownerMerge,
                learnedSkills,
                holyBalance),
            "lowercase revision is rejected instead of ambiguously normalized");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world,
                items,
                pets.ToLowerInvariant(),
                ownerMerge,
                learnedSkills,
                holyBalance),
            "lowercase pet revision is rejected instead of ambiguously normalized");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world,
                items,
                pets,
                ownerMerge.ToLowerInvariant(),
                learnedSkills,
                holyBalance),
            "lowercase owner-Merge revision is rejected");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world,
                items,
                pets,
                ownerMerge,
                learnedSkills.ToLowerInvariant(),
                holyBalance),
            "lowercase learned pet-skill revision is rejected");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world,
                items,
                pets,
                ownerMerge,
                learnedSkills,
                holyBalance.ToLowerInvariant()),
            "lowercase Holy Spirit balance revision is rejected");
        return Task.CompletedTask;
    }
}
