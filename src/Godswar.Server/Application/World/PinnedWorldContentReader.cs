using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.World;

internal sealed partial class PinnedWorldContentReader : IWorldContentReader
{
    private readonly IReadOnlyDictionary<short, StoredMapContent> _maps;
    private readonly IReadOnlyDictionary<string, StoredNpcDialogue>
        _npcDialogues;
    private readonly byte[][] _enterBootstrapPackets;

    private PinnedWorldContentReader(
        WorldContentManifest manifest,
        IReadOnlyDictionary<short, StoredMapContent> maps,
        IReadOnlyDictionary<string, StoredNpcDialogue> npcDialogues,
        byte[][] enterBootstrapPackets,
        GameplayContentCatalog gameplay)
    {
        Manifest = manifest;
        _maps = maps;
        _npcDialogues = npcDialogues;
        _enterBootstrapPackets = enterBootstrapPackets;
        Gameplay = gameplay;
    }

    public WorldContentManifest Manifest { get; }

    public GameplayContentCatalog Gameplay { get; }

    public static PinnedWorldContentReader Create(
        string source,
        IEnumerable<short> publishedMapIds,
        IEnumerable<NpcSpawnDefinition> npcDefinitions,
        IEnumerable<CapturedMonsterSpawn> monsterDefinitions,
        IEnumerable<byte[]> enterBootstrapPackets,
        DateTimeOffset? loadedAtUtc = null,
        IEnumerable<NpcTextDefinition>? npcTexts = null,
        IEnumerable<NpcDialogueRouteDefinition>? npcDialogueRoutes = null,
        GameplayContentCatalog? gameplay = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException(
                "World-content source is required.",
                nameof(source));
        }

        var mapIds = publishedMapIds
            .Distinct()
            .Order()
            .ToArray();
        if (mapIds.Length == 0)
        {
            throw Missing("maps", "No published map is available.");
        }

        var knownMaps = mapIds.ToHashSet();
        var npcs = npcDefinitions
            .Select(CloneAndValidateNpc)
            .OrderBy(static definition => definition.MapId)
            .ThenBy(
                static definition => definition.NpcKey,
                StringComparer.Ordinal)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ThenBy(static definition => definition.ObjectId)
            .ToArray();
        var monsters = monsterDefinitions
            .Select(CloneAndValidateMonster)
            .OrderBy(static definition => definition.MapId)
            .ThenBy(static definition => definition.ObjectId)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ToArray();

        EnsureDefinitionsUsePublishedMaps(
            "npcs",
            npcs.Select(static definition => definition.MapId),
            knownMaps);
        EnsureDefinitionsUsePublishedMaps(
            "monsters",
            monsters.Select(static definition => definition.MapId),
            knownMaps);

        var dialogues = PinNpcDialogues(
            npcs,
            npcTexts ?? [],
            npcDialogueRoutes ?? []);
        var bootstrap = enterBootstrapPackets
            .Select(CloneAndValidateBootstrapPacket)
            .ToArray();
        var pinnedGameplay = PinGameplay(gameplay ?? GameplayContentCatalog.Empty);
        EnsureMonsterSpawnsHaveGameplayTemplates(monsters, pinnedGameplay);
        var mapRevision = WorldContentRevisionHasher.HashMaps(mapIds);
        var npcRevision = WorldContentRevisionHasher.HashNpcs(npcs);
        var npcDialogueRevision =
            WorldContentRevisionHasher.HashNpcDialogues(
                dialogues.Texts,
                dialogues.Routes);
        var monsterRevision =
            WorldContentRevisionHasher.HashMonsters(monsters);
        var enterRevision =
            WorldContentRevisionHasher.HashEnterBootstrap(bootstrap);
        var gameplayRevision =
            WorldContentRevisionHasher.HashGameplay(pinnedGameplay);
        var manifest = new WorldContentManifest(
            source.Trim(),
            WorldContentRevisionHasher.HashManifest(
                mapRevision,
                npcRevision,
                npcDialogueRevision,
                monsterRevision,
                enterRevision,
                gameplayRevision),
            loadedAtUtc ?? DateTimeOffset.UtcNow,
            mapRevision,
            npcRevision,
            npcDialogueRevision,
            monsterRevision,
            enterRevision,
            gameplayRevision);

        var maps = mapIds.ToDictionary(
            static mapId => mapId,
            mapId => new StoredMapContent(
                npcs.Where(definition => definition.MapId == mapId).ToArray(),
                monsters
                    .Where(definition => definition.MapId == mapId)
                    .ToArray()));
        return new PinnedWorldContentReader(
            manifest,
            maps,
            dialogues.ByNpcKey,
            bootstrap,
            pinnedGameplay);
    }

    public ValueTask<WorldMapContent> ReadMapAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_maps.TryGetValue(mapId, out var content))
        {
            WorldContentMetrics.RecordRejection(
                "maps",
                WorldContentFailureReason.Missing);
            throw Missing(
                "maps",
                $"Map {mapId} is not present in pinned world content.");
        }

        return ValueTask.FromResult(new WorldMapContent(
            mapId,
            Manifest.Maps,
            Manifest.Npcs,
            Manifest.Monsters,
            content.Npcs.Select(CloneNpc).ToArray(),
            content.Monsters.Select(CloneMonster).ToArray()));
    }

    public ValueTask<EnterWorldBootstrapContent> ReadEnterBootstrapAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new EnterWorldBootstrapContent(
            Manifest.EnterBootstrap,
            _enterBootstrapPackets
                .Select(static packet => packet.ToArray())
                .ToArray()));
    }

    public void RequireRevision(string expectedRevision)
    {
        if (string.Equals(
                Manifest.Revision,
                expectedRevision,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        WorldContentMetrics.RecordRejection(
            "manifest",
            WorldContentFailureReason.RevisionMismatch);
        throw new WorldContentUnavailableException(
            "manifest",
            WorldContentFailureReason.RevisionMismatch,
            "Pinned world-content revision does not match the expected " +
            "revision.");
    }

    private static NpcSpawnDefinition CloneAndValidateNpc(
        NpcSpawnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.ObjectId == 0 ||
            definition.InteractionId == 0 ||
            string.IsNullOrWhiteSpace(definition.SceneKey) ||
            string.IsNullOrWhiteSpace(definition.NpcKey) ||
            string.IsNullOrWhiteSpace(definition.TemplateKey) ||
            !float.IsFinite(definition.X) ||
            !float.IsFinite(definition.Z) ||
            !float.IsFinite(definition.Facing) ||
            definition.Detail10077 is null ||
            definition.Detail10080 is null)
        {
            throw Invalid(
                "npcs",
                $"NPC definition '{definition.NpcKey}' is malformed.");
        }

        return CloneNpc(definition);
    }

    private static CapturedMonsterSpawn CloneAndValidateMonster(
        CapturedMonsterSpawn definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            definition.Validate(definition.MapId);
        }
        catch (InvalidDataException ex)
        {
            throw Invalid(
                "monsters",
                $"Monster definition {definition.ObjectId} is malformed.",
                ex);
        }

        return CloneMonster(definition);
    }

    private static byte[] CloneAndValidateBootstrapPacket(byte[] packet)
    {
        if (packet is null || packet.Length < 4)
        {
            throw Invalid(
                "enter-bootstrap",
                "A published enter-bootstrap packet is too short.");
        }

        var declaredLength =
            BinaryPrimitives.ReadUInt16LittleEndian(packet);
        if (declaredLength != packet.Length)
        {
            throw Invalid(
                "enter-bootstrap",
                "A published enter-bootstrap packet has an invalid frame " +
                "length.");
        }

        return packet.ToArray();
    }

    private static void EnsureDefinitionsUsePublishedMaps(
        string family,
        IEnumerable<short> definitionMapIds,
        IReadOnlySet<short> knownMaps)
    {
        var unknown = definitionMapIds
            .Where(mapId => !knownMaps.Contains(mapId))
            .Distinct()
            .Order()
            .ToArray();
        if (unknown.Length > 0)
        {
            throw Invalid(
                family,
                $"Definitions reference unpublished maps: " +
                string.Join(",", unknown));
        }
    }

    private static NpcSpawnDefinition CloneNpc(
        NpcSpawnDefinition definition) =>
        definition with
        {
            Detail10077 = definition.Detail10077.ToArray(),
            Detail10080 = definition.Detail10080.ToArray()
        };

    private static CapturedMonsterSpawn CloneMonster(
        CapturedMonsterSpawn definition) =>
        definition with { Packet = definition.Packet.ToArray() };

    private static WorldContentUnavailableException Missing(
        string family,
        string message) =>
        new(
            family,
            WorldContentFailureReason.Missing,
            message);

    private static WorldContentUnavailableException Invalid(
        string family,
        string message,
        Exception? innerException = null) =>
        new(
            family,
            WorldContentFailureReason.Invalid,
            message,
            innerException);

    private sealed record StoredMapContent(
        NpcSpawnDefinition[] Npcs,
        CapturedMonsterSpawn[] Monsters);
}
