using System.Reflection;
using System.Security.Cryptography;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class MonsterContentBaselineV1
{
    public const int ExpectedEntryCount = 270;
    public const string ExpectedRevision =
        "E3FB09F3D1EC721073BA60EDBE2709B34CA1CBC613F3EDFB25A79ADF5B7E52F7";
    public const string ExpectedArtifactSha256 =
        "26573882FCF30A86C70858A93C1BF3E1914AE2306E6372148861F902B89B0BCA";
    public const string Source = "reviewed-capture-promotion-v1";

    private const string ResourceName =
        "Godswar.Server.Infrastructure.WorldContent." +
        "Baselines.MonsterContentBaseline.v1.gz";

    public static CapturedMonsterSpawn[] LoadDefinitions()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream =
            assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(
                "The reviewed monster baseline resource is missing.");
        using var compressed = new MemoryStream();
        stream.CopyTo(compressed);
        var bytes = compressed.ToArray();
        var artifactHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(
                ExpectedArtifactSha256,
                artifactHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The reviewed monster baseline artifact checksum is invalid.");
        }

        var definitions = MonsterContentBaselineCodec.Deserialize(bytes);
        if (definitions.Length != ExpectedEntryCount)
        {
            throw new InvalidDataException(
                "The reviewed monster baseline entry count is invalid.");
        }

        var revision = WorldContentRevisionHasher.HashMonsters(definitions);
        if (!string.Equals(
                ExpectedRevision,
                revision.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The reviewed monster baseline content revision is invalid.");
        }

        return definitions;
    }
}
