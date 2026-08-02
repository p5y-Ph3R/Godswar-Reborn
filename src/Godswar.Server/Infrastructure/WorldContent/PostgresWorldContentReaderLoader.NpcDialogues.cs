using System.Collections.Immutable;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal sealed record PublishedNpcDialogueDefinitions(
    NpcTextDefinition[] Texts,
    NpcDialogueRouteDefinition[] Routes);

internal static partial class PostgresWorldContentReaderLoader
{
    private sealed record LoadedNpcDialogueProfile(
        string ProfileKey,
        int DialogIndex,
        NpcDialogueBehavior Behavior,
        int InitialRequestSubId,
        List<int> InitialMenuSubIds);

    private static async Task<PublishedNpcDialogueDefinitions>
        LoadPublishedNpcDialogueDefinitionsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<NpcSpawnDefinition> npcDefinitions,
            CancellationToken cancellationToken)
    {
        var spawnRevision =
            WorldContentRevisionHasher.HashNpcs(npcDefinitions);
        var header = await LoadNpcDialogueHeaderAsync(
            connection,
            transaction,
            cancellationToken);
        if (!string.Equals(
                header.SpawnRevision,
                spawnRevision.Sha256,
                StringComparison.Ordinal))
        {
            throw new WorldContentUnavailableException(
                "npc-dialogues",
                WorldContentFailureReason.RevisionMismatch,
                "The published NPC dialogue revision targets a different " +
                "NPC spawn revision.");
        }

        try
        {
            var texts = await LoadNpcDialogueTextsAsync(
                connection,
                transaction,
                header.Revision,
                header.TextCount,
                cancellationToken);
            var profiles = await LoadNpcDialogueProfilesAsync(
                connection,
                transaction,
                header.Revision,
                header.ProfileCount,
                header.MenuEntryCount,
                cancellationToken);
            var routes = await LoadNpcDialogueRoutesAsync(
                connection,
                transaction,
                header.Revision,
                header.RouteCount,
                profiles,
                cancellationToken);
            ValidateNpcDialogueCompatibility(
                npcDefinitions,
                texts,
                routes,
                profiles);

            var computed =
                WorldContentRevisionHasher.HashNpcDialogues(texts, routes);
            if (computed.EntryCount !=
                    checked(header.TextCount + header.RouteCount) ||
                !string.Equals(
                    computed.Sha256,
                    header.Revision,
                    StringComparison.Ordinal))
            {
                throw new WorldContentUnavailableException(
                    "npc-dialogues",
                    WorldContentFailureReason.RevisionMismatch,
                    "The published NPC dialogue content does not match its " +
                    "declared revision and entry counts.");
            }

            return new PublishedNpcDialogueDefinitions(texts, routes);
        }
        catch (WorldContentUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
                OverflowException or
                InvalidCastException)
        {
            throw new WorldContentUnavailableException(
                "npc-dialogues",
                WorldContentFailureReason.Invalid,
                "The published NPC dialogue content is malformed.",
                ex);
        }
    }

    private static async Task<(
        string Revision,
        string SpawnRevision,
        int TextCount,
        int ProfileCount,
        int RouteCount,
        int MenuEntryCount)> LoadNpcDialogueHeaderAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT publication.revision,
                   release.spawn_revision,
                   release.text_count,
                   release.profile_count,
                   release.route_count,
                   release.menu_entry_count
            FROM npc_dialogue_publication publication
            JOIN npc_dialogue_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'npc-dialogues';
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new WorldContentUnavailableException(
                "npc-dialogues",
                WorldContentFailureReason.Missing,
                "No official NPC dialogue revision is published.");
        }

        var header = (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5));
        if (header.Item3 is < 1 or > 10000 ||
            header.Item4 is < 1 or > 1024 ||
            header.Item5 is < 1 or > 10000 ||
            header.Item6 is < 1 or > 65535)
        {
            throw new WorldContentUnavailableException(
                "npc-dialogues",
                WorldContentFailureReason.Invalid,
                "The official NPC dialogue counts are outside supported " +
                "bounds.");
        }

        if (await reader.ReadAsync(cancellationToken))
        {
            throw new WorldContentUnavailableException(
                "npc-dialogues",
                WorldContentFailureReason.Invalid,
                "More than one official NPC dialogue revision is published.");
        }

        return header;
    }

    private static async Task<NpcTextDefinition[]>
        LoadNpcDialogueTextsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            int expectedCount,
            CancellationToken cancellationToken)
    {
        var texts = new List<NpcTextDefinition>(expectedCount);
        await using var command = new NpgsqlCommand(
            """
            SELECT npc_key, scene_key, display_name, description
            FROM npc_dialogue_texts
            WHERE revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            texts.Add(new NpcTextDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        if (texts.Count != expectedCount)
        {
            throw new InvalidDataException(
                "The NPC dialogue text count is incomplete.");
        }

        return texts
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<
        IReadOnlyDictionary<string, LoadedNpcDialogueProfile>>
        LoadNpcDialogueProfilesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            int expectedProfileCount,
            int expectedEntryCount,
            CancellationToken cancellationToken)
    {
        var profiles = new Dictionary<string, LoadedNpcDialogueProfile>(
            StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT profile_key,
                                dialog_index,
                                behavior,
                                initial_request_sub_id
                         FROM npc_dialogue_profiles
                         WHERE revision = @revision;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue(
                "revision",
                NpgsqlDbType.Varchar,
                revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var behaviorValue = reader.GetInt16(2);
                if (!Enum.IsDefined(
                        typeof(NpcDialogueBehavior),
                        (int)behaviorValue))
                {
                    throw new InvalidDataException(
                        "An NPC dialogue behavior is unknown.");
                }

                var profile = new LoadedNpcDialogueProfile(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    (NpcDialogueBehavior)behaviorValue,
                    reader.GetInt32(3),
                    []);
                if (profile.InitialRequestSubId != -1 ||
                    !profiles.TryAdd(profile.ProfileKey, profile))
                {
                    throw new InvalidDataException(
                        "An NPC dialogue profile is invalid or duplicated.");
                }
            }
        }

        if (profiles.Count != expectedProfileCount)
        {
            throw new InvalidDataException(
                "The NPC dialogue profile count is incomplete.");
        }

        var actualEntryCount = 0;
        await using var entryCommand = new NpgsqlCommand(
            """
            SELECT profile_key, menu_order, sub_id
            FROM npc_dialogue_profile_entries
            WHERE revision = @revision
            ORDER BY profile_key, menu_order;
            """,
            connection,
            transaction);
        entryCommand.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision);
        await using var entryReader =
            await entryCommand.ExecuteReaderAsync(cancellationToken);
        while (await entryReader.ReadAsync(cancellationToken))
        {
            var profileKey = entryReader.GetString(0);
            if (!profiles.TryGetValue(profileKey, out var profile) ||
                entryReader.GetInt16(1) !=
                    profile.InitialMenuSubIds.Count)
            {
                throw new InvalidDataException(
                    "An NPC dialogue menu is non-contiguous or orphaned.");
            }

            profile.InitialMenuSubIds.Add(entryReader.GetInt32(2));
            actualEntryCount++;
        }

        if (actualEntryCount != expectedEntryCount ||
            profiles.Values.Any(
                static profile =>
                    profile.InitialMenuSubIds.Count == 0 ||
                    profile.InitialMenuSubIds.Distinct().Count() !=
                    profile.InitialMenuSubIds.Count))
        {
            throw new InvalidDataException(
                "The NPC dialogue menu entry set is invalid.");
        }

        return profiles;
    }

    private static async Task<NpcDialogueRouteDefinition[]>
        LoadNpcDialogueRoutesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            int expectedCount,
            IReadOnlyDictionary<string, LoadedNpcDialogueProfile> profiles,
            CancellationToken cancellationToken)
    {
        var routes = new List<NpcDialogueRouteDefinition>(expectedCount);
        await using var command = new NpgsqlCommand(
            """
            SELECT npc_key, client_script_key, profile_key, route_order
            FROM npc_dialogue_bindings
            WHERE revision = @revision
            ORDER BY npc_key, route_order;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var profileKey = reader.GetString(2);
            if (!profiles.TryGetValue(profileKey, out var profile))
            {
                throw new InvalidDataException(
                    "An NPC dialogue binding references an unknown profile.");
            }

            routes.Add(new NpcDialogueRouteDefinition(
                reader.GetString(0),
                reader.GetString(1),
                profile.DialogIndex,
                profile.Behavior,
                ImmutableArray.CreateRange(
                    profile.InitialMenuSubIds))
            {
                RouteOrder = reader.GetInt16(3)
            });
        }

        if (routes.Count != expectedCount)
        {
            throw new InvalidDataException(
                "The NPC dialogue route count is incomplete.");
        }

        return routes
            .OrderBy(static route => route.NpcKey, StringComparer.Ordinal)
            .ThenBy(static route => route.RouteOrder)
            .ToArray();
    }

    private static void ValidateNpcDialogueCompatibility(
        IReadOnlyList<NpcSpawnDefinition> npcDefinitions,
        IReadOnlyList<NpcTextDefinition> texts,
        IReadOnlyList<NpcDialogueRouteDefinition> routes,
        IReadOnlyDictionary<string, LoadedNpcDialogueProfile> profiles)
    {
        var spawns = npcDefinitions.ToDictionary(
            static spawn => spawn.NpcKey,
            StringComparer.Ordinal);
        var textKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            if (!spawns.TryGetValue(text.NpcKey, out var spawn) ||
                !string.Equals(
                    text.SceneKey,
                    spawn.SceneKey,
                    StringComparison.Ordinal) ||
                !textKeys.Add(text.NpcKey))
            {
                throw new InvalidDataException(
                    "An NPC dialogue text does not match the spawn release.");
            }
        }

        if (textKeys.Count != spawns.Count)
        {
            throw new InvalidDataException(
                "The NPC dialogue text set does not cover the spawn release.");
        }

        var routeKeys = new HashSet<(string NpcKey, int RouteOrder)>();
        foreach (var route in routes)
        {
            if (!textKeys.Contains(route.NpcKey) ||
                !routeKeys.Add((route.NpcKey, route.RouteOrder)) ||
                !string.Equals(
                    route.NpcKey,
                    route.ClientScriptKey,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "An NPC dialogue route is invalid or duplicated.");
            }
        }

        foreach (var npcRoutes in routes.GroupBy(
                     static route => route.NpcKey,
                     StringComparer.Ordinal))
        {
            var expectedOrder = 0;
            var dialogIndices = new HashSet<int>();
            foreach (var route in npcRoutes)
            {
                if (route.RouteOrder != expectedOrder++ ||
                    !dialogIndices.Add(route.DialogIndex))
                {
                    throw new InvalidDataException(
                        "NPC dialogue routes are non-contiguous or duplicate " +
                        "an endpoint.");
                }
            }
        }

        var usedBehaviors = routes
            .Select(static route => route.Behavior)
            .ToHashSet();
        if (profiles.Values.Any(
                profile => !usedBehaviors.Contains(profile.Behavior)))
        {
            throw new InvalidDataException(
                "An NPC dialogue profile has no route binding.");
        }
    }
}
