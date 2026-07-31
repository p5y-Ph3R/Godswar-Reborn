using Godswar.Server.Application.Reconciliation;

namespace Godswar.Server.Infrastructure.Reconciliation;

internal sealed partial class PostgresReconciliationSnapshot
{
    public async Task<IReadOnlyList<ReconciliationCategoryCount>>
        ReadManifestAndContentAsync(
            CancellationToken cancellationToken)
    {
        var counts = new Dictionary<ReconciliationCategory, long>();
        var manifestMatches =
            await MigrationManifestMatchesAsync(cancellationToken);
        Add(
            counts,
            ReconciliationCategory.SchemaMigrationManifestMismatch,
            !manifestMatches);
        if (!manifestMatches)
        {
            return ToCounts(counts);
        }

        var content = await ReadNpcContentStateAsync(cancellationToken);
        Add(
            counts,
            ReconciliationCategory.NpcContentPublicationMismatch,
            content.PublicationMismatch);
        Add(
            counts,
            ReconciliationCategory.NpcContentCountMismatch,
            content.CountMismatch);
        return ToCounts(counts);
    }

    private async Task<bool> MigrationManifestMatchesAsync(
        CancellationToken cancellationToken)
    {
        await using (var existence = CreateCommand(
            """
            SELECT to_regclass(
                'public.schema_migrations'
            ) IS NOT NULL;
            """))
        {
            if (!Convert.ToBoolean(
                    await existence.ExecuteScalarAsync(
                        cancellationToken)))
            {
                return false;
            }
        }

        const string sql =
            """
            SELECT migration_id, checksum
            FROM public.schema_migrations
            ORDER BY migration_id
            LIMIT @manifest_limit;
            """;
        var index = 0;
        await using var command = CreateCommand(sql);
        command.Parameters.AddWithValue(
            "manifest_limit",
            _expectedMigrations.Count + 1);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (index >= _expectedMigrations.Count ||
                !string.Equals(
                    reader.GetString(0),
                    _expectedMigrations[index].Id,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reader.GetString(1),
                    _expectedMigrations[index].Checksum,
                    StringComparison.Ordinal))
            {
                return false;
            }

            index++;
        }

        return index == _expectedMigrations.Count;
    }

    private async Task<NpcContentState> ReadNpcContentStateAsync(
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            WITH spawn AS (
                SELECT
                    publication.revision,
                    revision.entry_count,
                    count(definition.object_id)::integer AS actual_count
                FROM public.npc_content_publication publication
                LEFT JOIN public.npc_content_revisions revision
                    ON revision.revision = publication.revision
                LEFT JOIN public.npc_spawn_definitions definition
                    ON definition.revision = publication.revision
                WHERE publication.family = 'npcs'
                GROUP BY publication.revision, revision.entry_count
            ),
            dialogue_release AS (
                SELECT
                    publication.revision,
                    revision.spawn_revision,
                    revision.text_count,
                    revision.profile_count,
                    revision.route_count,
                    revision.menu_entry_count
                FROM public.npc_dialogue_publication publication
                LEFT JOIN public.npc_dialogue_revisions revision
                    ON revision.revision = publication.revision
                WHERE publication.family = 'npc-dialogues'
            ),
            dialogue_counts AS (
                SELECT
                    dialogue.revision,
                    (
                        SELECT count(*)::integer
                        FROM public.npc_dialogue_texts text
                        WHERE text.revision = dialogue.revision
                    ) AS actual_text_count,
                    (
                        SELECT count(*)::integer
                        FROM public.npc_dialogue_profiles profile
                        WHERE profile.revision = dialogue.revision
                    ) AS actual_profile_count,
                    (
                        SELECT count(*)::integer
                        FROM public.npc_dialogue_bindings binding
                        WHERE binding.revision = dialogue.revision
                    ) AS actual_route_count,
                    (
                        SELECT count(*)::integer
                        FROM public.npc_dialogue_profile_entries entry
                        WHERE entry.revision = dialogue.revision
                    ) AS actual_menu_count
                FROM dialogue_release dialogue
            )
            SELECT
                spawn.revision IS NULL
                    OR dialogue.revision IS NULL
                    OR dialogue.spawn_revision
                        IS DISTINCT FROM spawn.revision,
                spawn.entry_count
                        IS DISTINCT FROM spawn.actual_count
                    OR dialogue.text_count
                        IS DISTINCT FROM counts.actual_text_count
                    OR dialogue.profile_count
                        IS DISTINCT FROM counts.actual_profile_count
                    OR dialogue.route_count
                        IS DISTINCT FROM counts.actual_route_count
                    OR dialogue.menu_entry_count
                        IS DISTINCT FROM counts.actual_menu_count
            FROM (SELECT 1) singleton
            LEFT JOIN spawn ON true
            LEFT JOIN dialogue_release dialogue ON true
            LEFT JOIN dialogue_counts counts
                ON counts.revision = dialogue.revision;
            """;
        await using var command = CreateCommand(sql);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new NpcContentState(true, true);
        }

        return new NpcContentState(
            reader.GetBoolean(0),
            reader.GetBoolean(1));
    }

    private readonly record struct NpcContentState(
        bool PublicationMismatch,
        bool CountMismatch);
}
