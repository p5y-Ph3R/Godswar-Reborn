using System.Buffers.Binary;

namespace Godswar.Server.State;

internal sealed record NpcSpawnDefinition(
    short MapId,
    string SceneKey,
    string NpcKey,
    string TemplateKey,
    uint ObjectId,
    float X,
    float Z,
    uint InteractionId,
    uint AppearanceType,
    float Facing,
    byte[] Detail10077,
    byte[] Detail10080);

internal readonly record struct NpcSpawnReferenceDefinition(
    short MapId,
    string SceneKey,
    string NpcKey,
    string TemplateKey,
    float X,
    float Z);

internal static class NpcSpawnDefinitionFactory
{
    internal const uint DefaultAppearanceType = 0x00000011;
    internal const float DefaultFacing = 1f;

    private const ushort WorldObjectAppearanceOpcode = 10020;
    private const uint SpartaNpcObjectIdBase = 4997;
    private const uint AthensNpcObjectIdBase = 5139;
    private const uint ReservedObjectIdStart = 0x00008000;
    private const uint ReservedObjectIdEnd = ushort.MaxValue - 1u;
    private const uint StableHashOffset = 2166136261;
    private const uint StableHashPrime = 16777619;
    private const float SpartaToAthensPositionXOffset = 0f;
    private const float SpartaToAthensPositionZOffset = 0f;
    private static readonly NpcSpawnReferenceDefinition[] RequiredCityReferences =
    [
        // Sparta is capture-backed. Matching numbered NPC packets available in the
        // embedded Athens stream share their positions, so an otherwise unplaced
        // paired NPC can reuse Sparta's coordinates. Map-specific references still
        // take priority where they exist.
        new(0, "Sparta", "Sparta_086", "Sparta_086_Male35", 126f, -169.9f),
        new(1, "Athens", "Athens_086", "Athens_086_Male35", 126f, -169.9f)
    ];

    public static IReadOnlyList<NpcSpawnDefinition> Create(
        short mapId,
        IReadOnlyList<CapturedNpcSpawn> capturedSpawns,
        IReadOnlyList<CapturedNpcSpawn> capturedAppearanceFallbacks,
        IReadOnlyList<NpcSpawnReferenceDefinition> referenceDefinitions)
    {
        var definitions = new List<NpcSpawnDefinition>();
        var usedObjectIds = new HashSet<uint>();
        var capturedNpcKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var spawn in capturedSpawns
                     .OrderBy(spawn => spawn.NpcKey, StringComparer.Ordinal)
                     .ThenBy(spawn => spawn.TemplateKey, StringComparer.Ordinal)
                     .ThenBy(spawn => spawn.ObjectId))
        {
            if (!TryCreateCapturedDefinition(mapId, spawn, out var definition) ||
                !usedObjectIds.Add(definition.ObjectId))
            {
                continue;
            }

            definitions.Add(definition);
            capturedNpcKeys.Add(definition.NpcKey);
        }

        var appearanceHints = BuildAppearanceHints(mapId, capturedSpawns, capturedAppearanceFallbacks);
        var pairedCityFallbacks = CreatePairedCityFallbackReferences(
            mapId,
            capturedAppearanceFallbacks,
            referenceDefinitions);
        var derivedReferences = referenceDefinitions
            .Concat(pairedCityFallbacks)
            .Concat(RequiredCityReferences.Where(reference => reference.MapId == mapId))
            .Where(reference =>
                reference.MapId == mapId &&
                !string.IsNullOrWhiteSpace(reference.SceneKey) &&
                !string.IsNullOrWhiteSpace(reference.NpcKey) &&
                !string.IsNullOrWhiteSpace(reference.TemplateKey) &&
                float.IsFinite(reference.X) &&
                float.IsFinite(reference.Z) &&
                !capturedNpcKeys.Contains(reference.NpcKey))
            .GroupBy(reference => reference.NpcKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(reference => reference.TemplateKey, StringComparer.Ordinal)
                .ThenBy(reference => reference.X)
                .ThenBy(reference => reference.Z)
                .First())
            .OrderBy(reference => reference.NpcKey, StringComparer.Ordinal)
            .ThenBy(reference => reference.TemplateKey, StringComparer.Ordinal)
            .ToArray();

        foreach (var reference in derivedReferences)
        {
            var objectId = TryGetCityObjectId(reference, out var cityObjectId) &&
                           !usedObjectIds.Contains(cityObjectId)
                ? cityObjectId
                : AllocateReservedObjectId(reference, usedObjectIds);
            usedObjectIds.Add(objectId);
            var appearance = appearanceHints.TryGetValue(reference.NpcKey, out var capturedAppearance)
                ? capturedAppearance
                : new NpcAppearanceHint(DefaultAppearanceType, DefaultFacing);

            definitions.Add(new NpcSpawnDefinition(
                reference.MapId,
                reference.SceneKey,
                reference.NpcKey,
                reference.TemplateKey,
                objectId,
                reference.X,
                reference.Z,
                objectId,
                appearance.AppearanceType,
                appearance.Facing,
                [],
                []));
        }

        return definitions
            .OrderBy(definition => definition.NpcKey, StringComparer.Ordinal)
            .ThenBy(definition => definition.TemplateKey, StringComparer.Ordinal)
            .ThenBy(definition => definition.ObjectId)
            .ToArray();
    }

    public static IReadOnlyList<NpcSpawnReferenceDefinition> FromGeneratedSeeds(short mapId)
    {
        var textScenes = NpcTemplateSeeds.Texts
            .Where(template => !string.IsNullOrWhiteSpace(template.NpcKey))
            .GroupBy(template => template.NpcKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(template => template.SceneKey).FirstOrDefault(scene => !string.IsNullOrWhiteSpace(scene)) ?? string.Empty,
                StringComparer.Ordinal);
        var appearances = NpcTemplateSeeds.Appearances
            .Where(template =>
                !string.IsNullOrWhiteSpace(template.NpcKey) &&
                !string.IsNullOrWhiteSpace(template.TemplateKey))
            .GroupBy(template => template.NpcKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var definitions = new List<NpcSpawnReferenceDefinition>();
        foreach (var npcReferences in NpcTemplateSeeds.SpawnReferences
                     .Where(reference =>
                         reference.MapId == mapId &&
                         !string.IsNullOrWhiteSpace(reference.NpcKey) &&
                         float.IsFinite(reference.X) &&
                         float.IsFinite(reference.Z))
                     .GroupBy(reference => reference.NpcKey, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (!appearances.TryGetValue(npcReferences.Key, out var npcAppearances))
            {
                continue;
            }

            textScenes.TryGetValue(npcReferences.Key, out var textScene);
            var appearance = npcAppearances
                .OrderBy(template => SceneRank(template.SceneKey, textScene))
                .ThenBy(template => template.TemplateKey.Length)
                .ThenBy(template => template.TemplateKey, StringComparer.Ordinal)
                .First();
            var position = npcReferences
                .GroupBy(reference => (reference.X, reference.Z))
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.X)
                .ThenBy(group => group.Key.Z)
                .First()
                .First();
            var sceneKey = FirstNonEmpty(appearance.SceneKey, textScene, InferSceneKey(npcReferences.Key));
            if (string.IsNullOrWhiteSpace(sceneKey))
            {
                continue;
            }

            definitions.Add(new NpcSpawnReferenceDefinition(
                mapId,
                sceneKey,
                npcReferences.Key,
                appearance.TemplateKey,
                position.X,
                position.Z));
        }

        return definitions;
    }

    private static bool TryCreateCapturedDefinition(
        short mapId,
        CapturedNpcSpawn spawn,
        out NpcSpawnDefinition definition)
    {
        definition = default!;
        if (spawn.MapId != mapId ||
            spawn.ObjectId == 0 ||
            string.IsNullOrWhiteSpace(spawn.SceneKey) ||
            string.IsNullOrWhiteSpace(spawn.NpcKey) ||
            string.IsNullOrWhiteSpace(spawn.TemplateKey) ||
            !float.IsFinite(spawn.X) ||
            !float.IsFinite(spawn.Z) ||
            !TryReadCapturedAppearance(spawn.Packet, out var packetObjectId, out var appearance))
        {
            return false;
        }

        if (packetObjectId != spawn.ObjectId)
        {
            return false;
        }

        definition = new NpcSpawnDefinition(
            spawn.MapId,
            spawn.SceneKey,
            spawn.NpcKey,
            spawn.TemplateKey,
            spawn.ObjectId,
            spawn.X,
            spawn.Z,
            spawn.ObjectId,
            appearance.AppearanceType,
            appearance.Facing,
            spawn.Detail10077,
            spawn.Detail10080);
        return true;
    }

    private static Dictionary<string, NpcAppearanceHint> BuildAppearanceHints(
        short mapId,
        IReadOnlyList<CapturedNpcSpawn> capturedSpawns,
        IReadOnlyList<CapturedNpcSpawn> capturedAppearanceFallbacks)
    {
        var hints = new Dictionary<string, NpcAppearanceHint>(StringComparer.Ordinal);
        AddAppearanceHints(hints, capturedSpawns, static spawn => spawn.NpcKey);

        if (mapId == 1)
        {
            AddAppearanceHints(
                hints,
                capturedAppearanceFallbacks,
                static spawn => spawn.MapId == 0 && spawn.NpcKey.StartsWith("Sparta_", StringComparison.Ordinal)
                    ? $"Athens_{spawn.NpcKey["Sparta_".Length..]}"
                    : null);
        }

        return hints;
    }

    private static IReadOnlyList<NpcSpawnReferenceDefinition> CreatePairedCityFallbackReferences(
        short mapId,
        IReadOnlyList<CapturedNpcSpawn> capturedAppearanceFallbacks,
        IReadOnlyList<NpcSpawnReferenceDefinition> referenceDefinitions)
    {
        if (mapId != 1 || capturedAppearanceFallbacks.Count == 0)
        {
            return [];
        }

        var referencedNpcKeys = referenceDefinitions
            .Select(reference => reference.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        var knownAthensTemplates = NpcTemplateSeeds.Appearances
            .Where(template =>
                template.NpcKey.StartsWith("Athens_", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(template.TemplateKey))
            .Select(template => template.TemplateKey)
            .ToHashSet(StringComparer.Ordinal);

        var fallbacks = new List<NpcSpawnReferenceDefinition>();
        foreach (var spawn in capturedAppearanceFallbacks
                     .Where(spawn =>
                         spawn.MapId == 0 &&
                         spawn.NpcKey.StartsWith("Sparta_", StringComparison.Ordinal) &&
                         spawn.TemplateKey.StartsWith("Sparta_", StringComparison.Ordinal))
                     .OrderBy(spawn => spawn.NpcKey, StringComparer.Ordinal)
                     .ThenBy(spawn => spawn.TemplateKey, StringComparer.Ordinal))
        {
            var npcKey = $"Athens_{spawn.NpcKey["Sparta_".Length..]}";
            var templateKey = $"Athens_{spawn.TemplateKey["Sparta_".Length..]}";
            if (referencedNpcKeys.Contains(npcKey) || !knownAthensTemplates.Contains(templateKey))
            {
                continue;
            }

            fallbacks.Add(new NpcSpawnReferenceDefinition(
                mapId,
                "Athens",
                npcKey,
                templateKey,
                spawn.X + SpartaToAthensPositionXOffset,
                spawn.Z + SpartaToAthensPositionZOffset));
            referencedNpcKeys.Add(npcKey);
        }

        return fallbacks;
    }

    private static void AddAppearanceHints(
        IDictionary<string, NpcAppearanceHint> hints,
        IEnumerable<CapturedNpcSpawn> spawns,
        Func<CapturedNpcSpawn, string?> keySelector)
    {
        foreach (var spawn in spawns
                     .OrderBy(spawn => spawn.NpcKey, StringComparer.Ordinal)
                     .ThenBy(spawn => spawn.TemplateKey, StringComparer.Ordinal))
        {
            var key = keySelector(spawn);
            if (string.IsNullOrWhiteSpace(key) ||
                hints.ContainsKey(key) ||
                !TryReadCapturedAppearance(spawn.Packet, out _, out var appearance))
            {
                continue;
            }

            hints[key] = appearance;
        }
    }

    private static bool TryReadCapturedAppearance(
        ReadOnlySpan<byte> packet,
        out uint objectId,
        out NpcAppearanceHint appearance)
    {
        objectId = 0;
        appearance = default;
        if (packet.Length < 44)
        {
            return false;
        }

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[..2]);
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2, 2));
        if (declaredLength < 44 || declaredLength > packet.Length || opcode != WorldObjectAppearanceOpcode)
        {
            return false;
        }

        var appearanceType = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4));
        objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8, 4));
        var facing = BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(40, 4));
        if (appearanceType == 0 || !float.IsFinite(facing))
        {
            return false;
        }

        appearance = new NpcAppearanceHint(appearanceType, facing);
        return true;
    }

    private static int SceneRank(string appearanceScene, string? expectedScene)
    {
        if (!string.IsNullOrWhiteSpace(expectedScene) &&
            string.Equals(appearanceScene, expectedScene, StringComparison.Ordinal))
        {
            return 0;
        }

        return string.IsNullOrWhiteSpace(appearanceScene) ? 2 : 1;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string InferSceneKey(string npcKey)
    {
        var separator = npcKey.IndexOf('_');
        return separator > 0 ? npcKey[..separator] : string.Empty;
    }

    private static bool TryGetCityObjectId(NpcSpawnReferenceDefinition reference, out uint objectId)
    {
        objectId = 0;
        string prefix;
        uint objectIdBase;
        if (reference.MapId == 0)
        {
            prefix = "Sparta_";
            objectIdBase = SpartaNpcObjectIdBase;
        }
        else if (reference.MapId == 1)
        {
            prefix = "Athens_";
            objectIdBase = AthensNpcObjectIdBase;
        }
        else
        {
            return false;
        }

        if (!reference.NpcKey.StartsWith(prefix, StringComparison.Ordinal) ||
            !uint.TryParse(reference.NpcKey.AsSpan(prefix.Length), out var npcNumber) ||
            npcNumber > ushort.MaxValue - objectIdBase)
        {
            return false;
        }

        objectId = objectIdBase + npcNumber;
        return true;
    }

    private static uint AllocateReservedObjectId(
        NpcSpawnReferenceDefinition reference,
        IReadOnlySet<uint> usedObjectIds)
    {
        var capacity = ReservedObjectIdEnd - ReservedObjectIdStart + 1;
        var initialOffset = StableIdentityHash(reference) % capacity;
        for (uint probe = 0; probe < capacity; probe++)
        {
            var objectId = ReservedObjectIdStart + ((initialOffset + probe) % capacity);
            if (!usedObjectIds.Contains(objectId))
            {
                return objectId;
            }
        }

        throw new InvalidOperationException($"Map {reference.MapId} has exhausted its reserved NPC object-ID range.");
    }

    private static uint StableIdentityHash(NpcSpawnReferenceDefinition reference)
    {
        var hash = StableHashOffset;
        AddHashValue(ref hash, unchecked((ushort)reference.MapId));
        AddHashValue(ref hash, reference.NpcKey);
        return hash;
    }

    private static void AddHashValue(ref uint hash, ushort value)
    {
        unchecked
        {
            hash = (hash ^ (byte)value) * StableHashPrime;
            hash = (hash ^ (byte)(value >> 8)) * StableHashPrime;
        }
    }

    private static void AddHashValue(ref uint hash, string value)
    {
        unchecked
        {
            foreach (var character in value)
            {
                hash = (hash ^ (byte)character) * StableHashPrime;
                hash = (hash ^ (byte)(character >> 8)) * StableHashPrime;
            }
        }
    }

    private readonly record struct NpcAppearanceHint(uint AppearanceType, float Facing);
}
