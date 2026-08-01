using Godswar.Server.Application.Coordination;

namespace Godswar.Server.ProtocolChecks;

internal static class RuntimeContentFingerprintChecks
{
    public const string CheckName =
        "Combined world, item, and pet runtime-content fingerprint";

    public static Task RunAsync()
    {
        const string world =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string items =
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        const string pets =
            "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        var baseline = RuntimeContentFingerprint.Create(world, items, pets);

        Check.Equal(64, baseline.Length, "combined fingerprint SHA-256 length");
        Check.Equal(
            baseline,
            RuntimeContentFingerprint.Create(world, items, pets),
            "combined fingerprint determinism");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    "CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    items,
                    pets),
                StringComparison.Ordinal),
            "world revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    "DBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                    pets),
            StringComparison.Ordinal),
            "item revision participates in worker compatibility");
        Check.True(
            !baseline.Equals(
                RuntimeContentFingerprint.Create(
                    world,
                    items,
                    "ECCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"),
                StringComparison.Ordinal),
            "pet revision participates in worker compatibility");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world.ToLowerInvariant(),
                items,
                pets),
            "lowercase revision is rejected instead of ambiguously normalized");
        Check.Throws<ArgumentException>(
            () => RuntimeContentFingerprint.Create(
                world,
                items,
                pets.ToLowerInvariant()),
            "lowercase pet revision is rejected instead of ambiguously normalized");
        return Task.CompletedTask;
    }
}
