using Godswar.Server.Application.Coordination;

namespace Godswar.Server.ProtocolChecks;

internal static class RuntimeContentFingerprintChecks
{
    public const string CheckName =
        "Combined world, item, pet, owner-Merge, and pet-skill runtime-content fingerprint";

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
        var baseline = RuntimeContentFingerprint.Create(
            world,
            items,
            pets,
            ownerMerge,
            learnedSkills);

        Check.Equal(64, baseline.Length, "combined fingerprint SHA-256 length");
        Check.Equal(
            baseline,
            RuntimeContentFingerprint.Create(
                world,
                items,
                pets,
                ownerMerge,
                learnedSkills),
            "combined fingerprint determinism");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    "CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    items,
                    pets,
                    ownerMerge,
                    learnedSkills),
                StringComparison.Ordinal),
            "world revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    "DBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                    pets,
                    ownerMerge,
                    learnedSkills),
            StringComparison.Ordinal),
            "item revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    items,
                    "ECCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
                    ownerMerge,
                    learnedSkills),
                StringComparison.Ordinal),
            "pet revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    items,
                    pets,
                    "FDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD",
                    learnedSkills),
                StringComparison.Ordinal),
            "owner-Merge revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    items,
                    pets,
                    ownerMerge,
                    "FEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE"),
                StringComparison.Ordinal),
            "learned pet-skill revision participates in worker compatibility");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world.ToLowerInvariant(),
                items,
                pets,
                ownerMerge,
                learnedSkills),
            "lowercase revision is rejected instead of ambiguously normalized");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world,
                items,
                pets.ToLowerInvariant(),
                ownerMerge,
                learnedSkills),
            "lowercase pet revision is rejected instead of ambiguously normalized");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world,
                items,
                pets,
                ownerMerge.ToLowerInvariant(),
                learnedSkills),
            "lowercase owner-Merge revision is rejected");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world,
                items,
                pets,
                ownerMerge,
                learnedSkills.ToLowerInvariant()),
            "lowercase learned pet-skill revision is rejected");
        return Task.CompletedTask;
    }
}
