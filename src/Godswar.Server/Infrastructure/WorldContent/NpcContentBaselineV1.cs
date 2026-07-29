using System.Reflection;
using System.Security.Cryptography;
using Godswar.Server.Application.World;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class NpcContentBaselineV1
{
    public const int ExpectedEntryCount = 383;
    public const string ExpectedRevision =
        "06BCC3DD4665BB5F3F3AE0843B1AA2A1B6C211DDA07DB0381B5EA663068040C7";
    public const string ExpectedArtifactSha256 =
        "4E6AEF697560276141C0A61E923FB016824FEEA607090C4294FEE1F6B6728926";
    public const string Source = "reviewed-legacy-projection-v1";

    private const string ResourceName =
        "Godswar.Server.Infrastructure.WorldContent." +
        "Baselines.NpcContentBaseline.v1.br";

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream =
            assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(
                "The reviewed NPC baseline resource is missing.");
        using var compressed = new MemoryStream();
        stream.CopyTo(compressed);
        var bytes = compressed.ToArray();
        var artifactHash = Convert.ToHexString(
            SHA256.HashData(bytes));
        if (!string.Equals(
                ExpectedArtifactSha256,
                artifactHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The reviewed NPC baseline artifact checksum is invalid.");
        }

        var definitions = NpcContentBaselineCodec.Deserialize(bytes);
        if (definitions.Length != ExpectedEntryCount)
        {
            throw new InvalidDataException(
                "The reviewed NPC baseline entry count is invalid.");
        }

        var revision =
            WorldContentRevisionHasher.HashNpcs(definitions);
        if (!string.Equals(
                ExpectedRevision,
                revision.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The reviewed NPC baseline content revision is invalid.");
        }

        return definitions;
    }
}
