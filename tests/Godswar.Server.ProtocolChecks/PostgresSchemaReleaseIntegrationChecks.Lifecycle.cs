using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresSchemaReleaseIntegrationChecks
{
    private static async Task<LifecycleReleaseState>
        ReadLifecycleReleaseStateAsync(
            NpgsqlConnection connection)
    {
        var columnCount = await ReadInt32Async(connection, """
            SELECT count(*)::integer
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'character_base'
              AND column_name = ANY(ARRAY[
                  'character_slot',
                  'lifecycle_state',
                  'lifecycle_version',
                  'deleted_at',
                  'restore_until',
                  'purge_after'
              ]::text[]);
            """);
        var fingerprint = columnCount == 6
            ? await ReadTextAsync(connection, """
                SELECT
                    (SELECT count(*)::text || ':' ||
                        md5(COALESCE(
                            string_agg(
                                account_row.id::text || ':' ||
                                account_row.character_lifecycle_version::text,
                                '|' ORDER BY account_row.id),
                            ''))
                     FROM public.accounts account_row) ||
                    '|' ||
                    (SELECT count(*)::text || ':' ||
                        md5(COALESCE(
                            string_agg(
                                character_row.id::text || ':' ||
                                character_row.character_slot::text || ':' ||
                                character_row.lifecycle_state || ':' ||
                                character_row.lifecycle_version::text || ':' ||
                                COALESCE(
                                    character_row.deleted_at::text,
                                    '<null>') || ':' ||
                                COALESCE(
                                    character_row.restore_until::text,
                                    '<null>') || ':' ||
                                COALESCE(
                                    character_row.purge_after::text,
                                    '<null>'),
                                '|' ORDER BY character_row.id),
                            ''))
                     FROM public.character_base character_row);
                """)
            : null;
        var constraintCount = await ReadInt32Async(connection, """
            SELECT count(*)::integer
            FROM pg_constraint
            WHERE conrelid = to_regclass('public.character_base')
              AND conname = ANY(ARRAY[
                  'ck_character_base_character_slot',
                  'ck_character_base_lifecycle_state',
                  'ck_character_base_lifecycle_version',
                  'ck_character_base_lifecycle_timestamps',
                  'ck_character_base_deleted_owner'
              ]::text[])
              AND contype = 'c'
              AND convalidated;
            """);
        var indexCount = await ReadInt32Async(connection, """
            SELECT count(*)::integer
            FROM pg_index index_row
            JOIN pg_class index_class
              ON index_class.oid = index_row.indexrelid
            WHERE index_row.indrelid =
                    to_regclass('public.character_base')
              AND index_class.relname = ANY(ARRAY[
                  'ux_character_base_active_account_slot',
                  'ix_character_base_deleted_account_slot',
                  'ix_character_base_purge_due'
              ]::text[])
              AND index_row.indisvalid
              AND index_row.indisready;
            """);
        var accountColumnCount = await ReadInt32Async(
            connection,
            """
            SELECT count(*)::integer
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'accounts'
              AND column_name = 'character_lifecycle_version';
            """);
        var accountConstraintCount = await ReadInt32Async(
            connection,
            """
            SELECT count(*)::integer
            FROM pg_constraint
            WHERE conrelid = to_regclass('public.accounts')
              AND conname =
                    'ck_accounts_character_lifecycle_version'
              AND contype = 'c'
              AND convalidated;
            """);
        return new LifecycleReleaseState(
            fingerprint,
            columnCount,
            constraintCount,
            indexCount,
            accountColumnCount,
            accountConstraintCount);
    }

    private sealed record LifecycleReleaseState(
        string? Fingerprint,
        int ColumnCount,
        int ConstraintCount,
        int IndexCount,
        int AccountColumnCount,
        int AccountConstraintCount);
}
