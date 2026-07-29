using System.Data;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal sealed record NpcDialoguePublicationResult(
    string Revision,
    string SpawnRevision,
    int TextCount,
    int ProfileCount,
    int RouteCount,
    int MenuEntryCount,
    string Source,
    bool Created);

internal static partial class PostgresNpcDialogueBaselinePublisher
{
    private const int PublicationLockNamespace = 1_193_657_936;
    private const int PublicationLockKey = 1_448_298_802;
    private const string Publisher = "server-baseline-v1";

    public static async Task<NpcDialoguePublicationResult>
        EnsurePublishedAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        return await EnsurePublishedOnceAsync(
            dataSource,
            cancellationToken);
    }

    private static async Task<NpcDialoguePublicationResult>
        EnsurePublishedOnceAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        await AcquirePublicationLockAsync(
            connection,
            transaction,
            cancellationToken);

        var current = await ReadCurrentPublicationAsync(
            connection,
            transaction,
            cancellationToken);
        if (current is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return current with { Created = false };
        }

        var spawnRevision = await ReadCurrentSpawnRevisionAsync(
            connection,
            transaction,
            cancellationToken);
        if (!string.Equals(
                spawnRevision.Revision,
                NpcDialogueBaselineV1.ExpectedSpawnRevision,
                StringComparison.Ordinal) ||
            spawnRevision.EntryCount !=
                NpcDialogueBaselineV1.ExpectedTextCount)
        {
            throw new InvalidDataException(
                "The reviewed NPC dialogue baseline does not target the " +
                "currently published NPC spawn revision.");
        }

        var texts = await ReadOfficialNpcTextsAsync(
            connection,
            transaction,
            spawnRevision.Revision,
            cancellationToken);
        var routes = NpcDialogueBaselineV1.CreateRoutes();
        var revision = ValidateBaseline(texts, routes);

        var releaseCreated = await InsertReleaseAsync(
            connection,
            transaction,
            revision,
            spawnRevision.Revision,
            cancellationToken);
        if (releaseCreated)
        {
            await InsertTextsAsync(
                connection,
                transaction,
                revision.Sha256,
                texts,
                cancellationToken);
            await InsertProfilesAsync(
                connection,
                transaction,
                revision.Sha256,
                cancellationToken);
            await InsertBindingsAsync(
                connection,
                transaction,
                revision.Sha256,
                cancellationToken);
        }

        await VerifyStoredReleaseAsync(
            connection,
            transaction,
            revision,
            spawnRevision.Revision,
            cancellationToken);
        await PublishAsync(
            connection,
            transaction,
            revision.Sha256,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CreateResult(
            revision.Sha256,
            spawnRevision.Revision,
            Created: true);
    }

    private static WorldContentFamilyRevision ValidateBaseline(
        IReadOnlyList<NpcTextDefinition> texts,
        IReadOnlyList<NpcDialogueRouteDefinition> routes)
    {
        if (texts.Count != NpcDialogueBaselineV1.ExpectedTextCount ||
            routes.Count != NpcDialogueBaselineV1.ExpectedRouteCount ||
            NpcDialogueBaselineV1.Profiles.Length !=
                NpcDialogueBaselineV1.ExpectedProfileCount ||
            NpcDialogueBaselineV1.Profiles.Sum(
                static profile => profile.InitialMenuSubIds.Length) !=
                NpcDialogueBaselineV1.ExpectedMenuEntryCount)
        {
            throw new InvalidDataException(
                "The reviewed NPC dialogue baseline has unexpected counts.");
        }

        var textKeys = new HashSet<string>(StringComparer.Ordinal);
        string? previousTextKey = null;
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text.NpcKey) ||
                string.IsNullOrWhiteSpace(text.SceneKey) ||
                string.IsNullOrWhiteSpace(text.DisplayName) ||
                string.IsNullOrWhiteSpace(text.Description) ||
                !textKeys.Add(text.NpcKey) ||
                (previousTextKey is not null &&
                 StringComparer.Ordinal.Compare(
                     previousTextKey,
                     text.NpcKey) >= 0))
            {
                throw new InvalidDataException(
                    "The official NPC dialogue text set is malformed.");
            }

            previousTextKey = text.NpcKey;
        }

        var routeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (!textKeys.Contains(route.NpcKey) ||
                !routeKeys.Add(route.NpcKey) ||
                !string.Equals(
                    route.NpcKey,
                    route.ClientScriptKey,
                    StringComparison.Ordinal) ||
                route.InitialMenuSubIds.IsDefaultOrEmpty ||
                route.InitialMenuSubIds.Distinct().Count() !=
                    route.InitialMenuSubIds.Length)
            {
                throw new InvalidDataException(
                    "The reviewed NPC dialogue route set is malformed.");
            }
        }

        var revision =
            WorldContentRevisionHasher.HashNpcDialogues(texts, routes);
        if (revision.EntryCount !=
                NpcDialogueBaselineV1.ExpectedHashedEntryCount ||
            !string.Equals(
                revision.Sha256,
                NpcDialogueBaselineV1.ExpectedRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The reviewed NPC dialogue baseline failed golden " +
                "revision validation.");
        }

        return revision;
    }

    private static async Task AcquirePublicationLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@namespace, @key);",
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "namespace",
            NpgsqlDbType.Integer,
            PublicationLockNamespace);
        command.Parameters.AddWithValue(
            "key",
            NpgsqlDbType.Integer,
            PublicationLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<NpcDialoguePublicationResult?>
        ReadCurrentPublicationAsync(
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
                   release.menu_entry_count,
                   release.source
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
            return null;
        }

        return new NpcDialoguePublicationResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            Created: false);
    }

    private static async Task<(string Revision, int EntryCount)>
        ReadCurrentSpawnRevisionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT publication.revision, release.entry_count
            FROM npc_content_publication publication
            JOIN npc_content_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'npcs';
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "NPC dialogue publication requires an official NPC spawn " +
                "publication.");
        }

        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<NpcTextDefinition[]>
        ReadOfficialNpcTextsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string spawnRevision,
            CancellationToken cancellationToken)
    {
        var texts = new List<NpcTextDefinition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT text.npc_key,
                   text.scene_key,
                   text.display_name,
                   text.description
            FROM npc_spawn_definitions spawn
            JOIN npc_text_templates text
              ON text.npc_key = spawn.npc_key
            WHERE spawn.revision = @spawn_revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "spawn_revision",
            NpgsqlDbType.Varchar,
            spawnRevision);
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

        return texts
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static NpcDialoguePublicationResult CreateResult(
        string revision,
        string spawnRevision,
        bool Created) =>
        new(
            revision,
            spawnRevision,
            NpcDialogueBaselineV1.ExpectedTextCount,
            NpcDialogueBaselineV1.ExpectedProfileCount,
            NpcDialogueBaselineV1.ExpectedRouteCount,
            NpcDialogueBaselineV1.ExpectedMenuEntryCount,
            NpcDialogueBaselineV1.Source,
            Created);
}
