using System.IO.Compression;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

internal static class NpcContentAuthorityChecks
{
    private const int GoldenEntryCount = 383;
    private const string GoldenRevision =
        "06BCC3DD4665BB5F3F3AE0843B1AA2A1B6C211DDA07DB0381B5EA663068040C7";

    private static readonly byte[] Magic = "GWNPCB01"u8.ToArray();

    private static readonly string[] ProhibitedLoaderReferences =
    [
        "NpcSpawnDefinitionFactory",
        "NpcTemplateSeeds",
        "NpcActorPlacementCatalog",
        "CapturedNpcSpawn",
        "npc_spawn_packets",
        "npc_spawn_references",
        "npc_appearance_templates",
        "npc_text_templates"
    ];

    public static Task RunAsync()
    {
        CheckFrozenBaseline();
        CheckCodecRoundTrip();
        CheckCodecRejectsMalformedAndUnboundedInput();
        CheckPostgresLoaderHasOneNpcAuthority();
        return Task.CompletedTask;
    }

    private static void CheckFrozenBaseline()
    {
        var definitions = NpcContentBaselineV1.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);

        Check.Equal(
            GoldenEntryCount,
            definitions.Length,
            "frozen NPC baseline golden entry count");
        Check.Equal(
            GoldenEntryCount,
            revision.EntryCount,
            "frozen NPC baseline revision entry count");
        Check.Equal(
            GoldenRevision,
            revision.Sha256,
            "frozen NPC baseline golden revision");
        Check.Equal(
            GoldenEntryCount,
            NpcContentBaselineV1.ExpectedEntryCount,
            "baseline declaration remains pinned to the reviewed count");
        Check.Equal(
            GoldenRevision,
            NpcContentBaselineV1.ExpectedRevision,
            "baseline declaration remains pinned to the reviewed revision");
    }

    private static void CheckCodecRoundTrip()
    {
        var later = CreateDefinition(
            mapId: 2,
            npcKey: "Vendor_\u03A9",
            objectId: 0x9002,
            detailSeed: 0x30);
        var earlier = CreateDefinition(
            mapId: 1,
            npcKey: "Artisan",
            objectId: 0x9001,
            detailSeed: 0x10);

        var encoded =
            NpcContentBaselineCodec.Serialize([later, earlier]);
        var decoded = NpcContentBaselineCodec.Deserialize(encoded);

        Check.Equal(2, decoded.Length, "NPC codec round-trip count");
        CheckDefinitionEqual(
            earlier,
            decoded[0],
            "NPC codec canonical first definition");
        CheckDefinitionEqual(
            later,
            decoded[1],
            "NPC codec canonical second definition");
        Check.True(
            !ReferenceEquals(
                earlier.Detail10077,
                decoded[0].Detail10077),
            "NPC codec creates independent detail buffers");
    }

    private static void CheckCodecRejectsMalformedAndUnboundedInput()
    {
        Check.Throws<InvalidDataException>(
            () => NpcContentBaselineCodec.Deserialize([]),
            "NPC codec rejects an empty artifact");
        Check.Throws<InvalidDataException>(
            () => NpcContentBaselineCodec.Deserialize(
                new byte[(1024 * 1024) + 1]),
            "NPC codec rejects an oversized compressed artifact");

        Check.Throws<InvalidDataException>(
            () => NpcContentBaselineCodec.Deserialize(
                CompressRaw(writer =>
                {
                    writer.Write("BADMAGIC"u8);
                    writer.Write(0);
                })),
            "NPC codec rejects an unknown format");
        Check.Throws<InvalidDataException>(
            () => NpcContentBaselineCodec.Deserialize(
                CompressRaw(writer =>
                {
                    writer.Write(Magic);
                    writer.Write(10_001);
                })),
            "NPC codec rejects an excessive definition count");
        Check.Throws<InvalidDataException>(
            () => NpcContentBaselineCodec.Deserialize(
                CompressRaw(writer =>
                {
                    writer.Write(Magic);
                    writer.Write(1);
                    writer.Write((short)1);
                    writer.Write(385);
                })),
            "NPC codec rejects an oversized field before allocation");
        Check.Throws<EndOfStreamException>(
            () => NpcContentBaselineCodec.Deserialize(
                CompressRaw(writer =>
                {
                    writer.Write(Magic);
                    writer.Write(1);
                })),
            "NPC codec rejects a truncated definition");

        var valid =
            NpcContentBaselineCodec.Serialize(
                [CreateDefinition(1, "Valid", 0x9010, 0x50)]);
        Check.Throws<InvalidDataException>(
            () => NpcContentBaselineCodec.Deserialize(
                AppendUncompressedByte(valid)),
            "NPC codec rejects trailing uncompressed bytes");

        var definition =
            CreateDefinition(1, "Bounded", 0x9020, 0x60);
        Check.Throws<InvalidDataException>(
            () => NpcContentBaselineCodec.Serialize(
                Enumerable.Repeat(definition, 10_001)),
            "NPC codec bounds serialized definition count");
        Check.Throws<InvalidDataException>(
            () => NpcContentBaselineCodec.Serialize(
                [definition with { SceneKey = new string('x', 385) }]),
            "NPC codec bounds serialized strings");
        Check.Throws<InvalidDataException>(
            () => NpcContentBaselineCodec.Serialize(
                [
                    definition with
                    {
                        Detail10077 = new byte[ushort.MaxValue + 1]
                    }
                ]),
            "NPC codec bounds serialized detail payloads");

        var expansionBomb = CompressBytes(
            new byte[(8 * 1024 * 1024) + 1]);
        Check.Throws<InvalidDataException>(
            () => NpcContentBaselineCodec.Deserialize(expansionBomb),
            "NPC codec bounds decompressed input");
    }

    private static void CheckPostgresLoaderHasOneNpcAuthority()
    {
        var repositoryRoot = FindRepositoryRoot();
        var loaderRoot = Path.Combine(
            repositoryRoot,
            "src",
            "Godswar.Server",
            "Infrastructure",
            "WorldContent");
        var loaderFiles = Directory
            .EnumerateFiles(
                loaderRoot,
                "PostgresWorldContentReaderLoader*.cs",
                SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Check.True(
            loaderFiles.Length > 0,
            "PostgreSQL world-content loader sources are present");
        foreach (var path in loaderFiles)
        {
            var source = File.ReadAllText(path);
            foreach (var prohibited in ProhibitedLoaderReferences)
            {
                Check.True(
                    !source.Contains(
                        prohibited,
                        StringComparison.OrdinalIgnoreCase),
                    $"PostgreSQL world-content loader does not reference " +
                    $"legacy NPC authority '{prohibited}' in " +
                    Path.GetFileName(path));
            }
        }
    }

    private static NpcSpawnDefinition CreateDefinition(
        short mapId,
        string npcKey,
        uint objectId,
        byte detailSeed) =>
        new(
            mapId,
            $"Scene_{mapId}",
            npcKey,
            $"Template_{npcKey}",
            objectId,
            mapId + 0.25f,
            mapId + 0.75f,
            objectId + 10,
            objectId + 20,
            1.5f,
            [detailSeed, (byte)(detailSeed + 1)],
            [(byte)(detailSeed + 2), (byte)(detailSeed + 3)]);

    private static void CheckDefinitionEqual(
        NpcSpawnDefinition expected,
        NpcSpawnDefinition actual,
        string description)
    {
        Check.Equal(expected.MapId, actual.MapId, $"{description} map");
        Check.Equal(
            expected.SceneKey,
            actual.SceneKey,
            $"{description} scene");
        Check.Equal(expected.NpcKey, actual.NpcKey, $"{description} key");
        Check.Equal(
            expected.TemplateKey,
            actual.TemplateKey,
            $"{description} template");
        Check.Equal(
            expected.ObjectId,
            actual.ObjectId,
            $"{description} object ID");
        Check.Equal(expected.X, actual.X, $"{description} X");
        Check.Equal(expected.Z, actual.Z, $"{description} Z");
        Check.Equal(
            expected.InteractionId,
            actual.InteractionId,
            $"{description} interaction");
        Check.Equal(
            expected.AppearanceType,
            actual.AppearanceType,
            $"{description} appearance");
        Check.Equal(
            expected.Facing,
            actual.Facing,
            $"{description} facing");
        Check.True(
            expected.Detail10077.SequenceEqual(actual.Detail10077),
            $"{description} detail 10077");
        Check.True(
            expected.Detail10080.SequenceEqual(actual.Detail10080),
            $"{description} detail 10080");
    }

    private static byte[] AppendUncompressedByte(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(
            input,
            CompressionMode.Decompress);
        using var raw = new MemoryStream();
        brotli.CopyTo(raw);
        raw.WriteByte(0xFF);
        return CompressBytes(raw.ToArray());
    }

    private static byte[] CompressRaw(Action<BinaryWriter> write)
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(
                   raw,
                   Encoding.UTF8,
                   leaveOpen: true))
        {
            write(writer);
        }

        return CompressBytes(raw.ToArray());
    }

    private static byte[] CompressBytes(byte[] raw)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(
                   compressed,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            brotli.Write(raw);
        }

        return compressed.ToArray();
    }

    private static string FindRepositoryRoot()
    {
        foreach (var seed in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            for (var candidate = new DirectoryInfo(seed);
                 candidate is not null;
                 candidate = candidate.Parent)
            {
                if (File.Exists(
                        Path.Combine(candidate.FullName, "AGENTS.md")) &&
                    File.Exists(
                        Path.Combine(
                            candidate.FullName,
                            "GodswarServer.sln")))
                {
                    return candidate.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }
}
