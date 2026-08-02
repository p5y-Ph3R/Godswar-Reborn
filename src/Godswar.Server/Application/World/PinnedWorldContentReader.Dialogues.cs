using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.World;

internal sealed partial class PinnedWorldContentReader
{
    private const int MaximumNpcDialogueDefinitions = 10_000;
    private const int MaximumNpcKeyLength = 96;
    private const int MaximumSceneKeyLength = 96;
    private const int MaximumDisplayNameLength = 255;
    private const int MaximumDescriptionLength = 16_384;
    private const int MaximumClientScriptKeyLength = 32;
    private const int MaximumInitialMenuItems = 64;
    private const int MaximumRoutesPerNpc = 64;
    private const int MaximumSubId = 1_000_000;

    public ValueTask<NpcDialogueContent> ReadNpcDialogueAsync(
        string npcKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(npcKey) ||
            !_npcDialogues.TryGetValue(npcKey, out var dialogue))
        {
            WorldContentMetrics.RecordRejection(
                "npc-dialogues",
                WorldContentFailureReason.Missing);
            throw Missing(
                "npc-dialogues",
                $"NPC dialogue '{npcKey}' is not present in pinned world " +
                "content.");
        }

        return ValueTask.FromResult(new NpcDialogueContent(
            Manifest.NpcDialogues,
            CloneText(dialogue.Text),
            dialogue.Routes.Select(CloneRoute).ToArray()));
    }

    private static PinnedNpcDialogues PinNpcDialogues(
        IReadOnlyList<NpcSpawnDefinition> npcs,
        IEnumerable<NpcTextDefinition> textDefinitions,
        IEnumerable<NpcDialogueRouteDefinition> routeDefinitions)
    {
        var texts = MaterializeBounded(
                textDefinitions,
                MaximumNpcDialogueDefinitions,
                "NPC text")
            .Select(CloneAndValidateText)
            .OrderBy(static value => value.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var routes = MaterializeBounded(
                routeDefinitions,
                MaximumNpcDialogueDefinitions,
                "NPC dialogue route")
            .Select(CloneAndValidateRoute)
            .OrderBy(static value => value.NpcKey, StringComparer.Ordinal)
            .ToArray();

        var textsByKey = ToUniqueDictionary(
            texts,
            static value => value.NpcKey,
            "NPC text");
        var routesByKey = routes
            .GroupBy(static value => value.NpcKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => group
                    .OrderBy(static route => route.RouteOrder)
                    .ToArray(),
                StringComparer.Ordinal);
        var spawnedByKey = npcs
            .GroupBy(static value => value.NpcKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);

        if (texts.Length == 0 && routes.Length == 0)
        {
            return new PinnedNpcDialogues(
                texts,
                routes,
                new Dictionary<string, StoredNpcDialogue>(
                    StringComparer.Ordinal));
        }

        var missingTexts = spawnedByKey.Keys
            .Where(key => !textsByKey.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var unspawnedTexts = textsByKey.Keys
            .Where(key => !spawnedByKey.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missingTexts.Length > 0 || unspawnedTexts.Length > 0)
        {
            throw Invalid(
                "npc-dialogues",
                "NPC text coverage does not exactly match the published NPC " +
                $"spawn keys (missing={FormatKeys(missingTexts)}, " +
                $"unspawned={FormatKeys(unspawnedTexts)}).");
        }

        foreach (var text in texts)
        {
            if (!spawnedByKey[text.NpcKey].Any(spawn =>
                    string.Equals(
                        spawn.SceneKey,
                        text.SceneKey,
                        StringComparison.Ordinal)))
            {
                throw Invalid(
                    "npc-dialogues",
                    $"NPC text '{text.NpcKey}' does not match a published " +
                    $"spawn in scene '{text.SceneKey}'.");
            }
        }

        var unboundRoutes = routesByKey.Keys
            .Where(key => !textsByKey.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unboundRoutes.Length > 0)
        {
            throw Invalid(
                "npc-dialogues",
                "NPC dialogue routes lack published NPC text: " +
                FormatKeys(unboundRoutes) + ".");
        }

        foreach (var group in routesByKey)
        {
            var expectedOrder = 0;
            var dialogIndices = new HashSet<int>();
            foreach (var route in group.Value)
            {
                if (route.RouteOrder != expectedOrder++ ||
                    !dialogIndices.Add(route.DialogIndex))
                {
                    throw Invalid(
                        "npc-dialogues",
                        $"NPC dialogue routes for '{group.Key}' are " +
                        "non-contiguous or duplicate an endpoint.");
                }
            }
        }

        var byNpcKey = texts.ToDictionary(
            static text => text.NpcKey,
            text => new StoredNpcDialogue(
                text,
                routesByKey.GetValueOrDefault(text.NpcKey) ?? []),
            StringComparer.Ordinal);
        return new PinnedNpcDialogues(texts, routes, byNpcKey);
    }

    private static NpcTextDefinition CloneAndValidateText(
        NpcTextDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!IsBoundedText(definition.NpcKey, MaximumNpcKeyLength) ||
            !IsBoundedText(definition.SceneKey, MaximumSceneKeyLength) ||
            !IsBoundedText(
                definition.DisplayName,
                MaximumDisplayNameLength) ||
            !IsBoundedText(
                definition.Description,
                MaximumDescriptionLength))
        {
            throw Invalid(
                "npc-dialogues",
                $"NPC text '{definition.NpcKey}' is malformed.");
        }

        return CloneText(definition);
    }

    private static NpcDialogueRouteDefinition CloneAndValidateRoute(
        NpcDialogueRouteDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!IsBoundedText(definition.NpcKey, MaximumNpcKeyLength) ||
            !IsBoundedAscii(
                definition.ClientScriptKey,
                MaximumClientScriptKeyLength) ||
            definition.DialogIndex is <= 0 or > short.MaxValue ||
            definition.RouteOrder is < 0 or >= MaximumRoutesPerNpc ||
            !Enum.IsDefined(definition.Behavior) ||
            definition.InitialMenuSubIds.IsDefaultOrEmpty ||
            definition.InitialMenuSubIds.Length >
            MaximumInitialMenuItems ||
            definition.InitialMenuSubIds.Any(
                static subId => subId is <= 0 or > MaximumSubId) ||
            definition.InitialMenuSubIds.Distinct().Count() !=
            definition.InitialMenuSubIds.Length)
        {
            throw Invalid(
                "npc-dialogues",
                $"NPC dialogue route '{definition.NpcKey}' is malformed.");
        }

        return CloneRoute(definition);
    }

    private static T[] MaterializeBounded<T>(
        IEnumerable<T> source,
        int maximumCount,
        string description)
    {
        ArgumentNullException.ThrowIfNull(source);
        var values = new List<T>();
        foreach (var value in source)
        {
            if (values.Count >= maximumCount)
            {
                throw Invalid(
                    "npc-dialogues",
                    $"{description} count exceeds {maximumCount}.");
            }

            values.Add(value);
        }

        return values.ToArray();
    }

    private static Dictionary<string, T> ToUniqueDictionary<T>(
        IEnumerable<T> definitions,
        Func<T, string> keySelector,
        string description)
    {
        var values = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var key = keySelector(definition);
            if (!values.TryAdd(key, definition))
            {
                throw Invalid(
                    "npc-dialogues",
                    $"{description} '{key}' occurs more than once.");
            }
        }

        return values;
    }

    private static bool IsBoundedText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength;

    private static bool IsBoundedAscii(string? value, int maximumLength) =>
        IsBoundedText(value, maximumLength) &&
        value!.All(static character => character is >= ' ' and <= '~');

    private static string FormatKeys(IReadOnlyList<string> keys)
    {
        const int maximumReportedKeys = 5;
        if (keys.Count == 0)
        {
            return "none";
        }

        var summary = string.Join(",", keys.Take(maximumReportedKeys));
        return keys.Count <= maximumReportedKeys
            ? summary
            : summary + $",...({keys.Count} total)";
    }

    private static NpcTextDefinition CloneText(
        NpcTextDefinition definition) =>
        definition with { };

    private static NpcDialogueRouteDefinition CloneRoute(
        NpcDialogueRouteDefinition definition) =>
        definition with
        {
            InitialMenuSubIds = ImmutableArray.CreateRange(
                definition.InitialMenuSubIds)
        };

    private sealed record StoredNpcDialogue(
        NpcTextDefinition Text,
        NpcDialogueRouteDefinition[] Routes);

    private sealed record PinnedNpcDialogues(
        NpcTextDefinition[] Texts,
        NpcDialogueRouteDefinition[] Routes,
        IReadOnlyDictionary<string, StoredNpcDialogue> ByNpcKey);
}
