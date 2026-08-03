using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresSchemaReleaseIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_SCHEMA_RELEASE_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL schema-release integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var before = await ReadSnapshotAsync(dataSource);
        Check.True(
            before.CoreMarkerCount is 0 or 4,
            "schema-release target is either genuinely empty or a complete legacy database");
        _ = PostgresSchemaMigrationPlan.Build(
            PostgresSchemaMigrationCatalog.All,
            before.AppliedMigrations);

        await InitializeReleaseAsync(connectionString);

        var after = await ReadSnapshotAsync(dataSource);
        AssertReleaseState(after);
        await AssertDurableStatePreservedAsync(
            dataSource,
            before,
            after);

        await InitializeReleaseAsync(connectionString);
        var repeated = await ReadSnapshotAsync(dataSource);
        AssertReleaseState(repeated);
        Check.Equal(
            after.ReleaseFingerprint,
            repeated.ReleaseFingerprint,
            "second initialization is a schema and durable-row no-op");

        Console.WriteLine(
            $"PostgreSQL schema release verified from " +
            $"{before.AppliedMigrations.Count} applied migrations.");
    }

    private static async Task<SchemaReleaseSnapshot> ReadSnapshotAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var markerCount = await ReadInt32Async(connection, """
            SELECT count(*)::integer
            FROM unnest(ARRAY[
                'accounts',
                'character_base',
                'character_items',
                'item_templates'
            ]::text[]) AS expected(table_name)
            WHERE to_regclass(
                'public.' || quote_ident(expected.table_name)) IS NOT NULL;
            """);

        var migrations = new List<AppliedPostgresSchemaMigration>();
        if (await RelationExistsAsync(connection, "public.schema_migrations"))
        {
            await using var command = new NpgsqlCommand("""
                SELECT migration_id, checksum
                FROM public.schema_migrations
                ORDER BY migration_id;
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                migrations.Add(new AppliedPostgresSchemaMigration(
                    reader.GetString(0),
                    reader.GetString(1)));
            }
        }

        var inventoryFingerprint =
            await RelationExistsAsync(connection, "public.character_items")
                ? await ReadTextAsync(connection, """
                    SELECT count(*)::text || ':' ||
                        md5(COALESCE(
                            string_agg(
                                to_jsonb(item_row)::text,
                                '|' ORDER BY item_row.id),
                            ''))
                    FROM public.character_items item_row;
                    """)
                : null;
        var inventoryRows =
            inventoryFingerprint is null
                ? null
                : await ReadInventoryRowsAsync(
                    connection);
        var accountCharacterFingerprint =
            await RelationExistsAsync(connection, "public.accounts") &&
            await RelationExistsAsync(connection, "public.character_base")
                ? await ReadTextAsync(connection, """
                    SELECT
                        (SELECT count(*)::text || ':' ||
                            md5(COALESCE(
                                string_agg(
                                (
                                    to_jsonb(account_row) -
                                    'character_lifecycle_version'
                                )::text,
                                    '|' ORDER BY account_row.id),
                                ''))
                         FROM public.accounts account_row) ||
                        '|' ||
                        (SELECT count(*)::text || ':' ||
                            md5(COALESCE(
                                string_agg(
                                    (
                                        to_jsonb(character_row) -
                                        ARRAY[
                                            'wallet_revision',
                                            'inventory_revision',
                                            'progression_reward_revision',
                                            'fighter_level_sealed',
                                            'position_revision',
                                            'checkpoint_owner_id',
                                            'checkpoint_owner_generation',
                                            'character_slot',
                                            'lifecycle_state',
                                            'lifecycle_version',
                                            'deleted_at',
                                            'restore_until',
                                            'purge_after'
                                        ]::text[]
                                    )::text,
                                    '|' ORDER BY character_row.id),
                                ''))
                         FROM public.character_base character_row);
                    """)
                : null;
        var checkpointColumnCount =
            await RelationExistsAsync(connection, "public.character_base")
                ? await ReadInt32Async(connection, """
                    SELECT count(*)::integer
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'character_base'
                      AND column_name = ANY(ARRAY[
                          'position_revision',
                          'checkpoint_owner_id',
                          'checkpoint_owner_generation'
                      ]::text[]);
                    """)
                : 0;
        var checkpointFingerprint =
            checkpointColumnCount == 3
                ? await ReadTextAsync(connection, """
                    SELECT count(*)::text || ':' ||
                        md5(COALESCE(
                            string_agg(
                                character_row.id::text || ':' ||
                                COALESCE(
                                    character_row.checkpoint_owner_id::text,
                                    '<null>') || ':' ||
                                character_row.checkpoint_owner_generation::text ||
                                ':' ||
                                character_row.position_revision::text || ':' ||
                                character_row.vitals_revision::text,
                                '|' ORDER BY character_row.id),
                            ''))
                    FROM public.character_base character_row;
                    """)
                : null;
        var lifecycle =
            await ReadLifecycleReleaseStateAsync(connection);
        var classSuitAttributeColumnCount =
            await RelationExistsAsync(connection, "public.character_items")
                ? await ReadInt32Async(connection, """
                    SELECT count(*)::integer
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'character_items'
                      AND column_name = ANY(ARRAY[
                          'class_attribute1',
                          'class_attribute2'
                      ]::text[]);
                    """)
                : 0;
        var packetPayloadFingerprint =
            await RelationExistsAsync(connection, "public.packet_transactions")
                ? await ReadTextAsync(connection, """
                    SELECT count(*)::text || ':' ||
                        md5(COALESCE(
                            string_agg(
                                packet_row.id::text || ':' ||
                                encode(packet_row.clear_bytes, 'hex') || ':' ||
                                encode(packet_row.raw_bytes, 'hex'),
                                '|' ORDER BY packet_row.id),
                            ''))
                    FROM public.packet_transactions packet_row;
                    """)
                : null;
        var petFingerprint =
            await RelationExistsAsync(connection, "public.character_pets") &&
            await RelationExistsAsync(
                connection,
                "public.character_pet_stat_values")
                ? await ReadTextAsync(connection, """
                    SELECT
                        (SELECT count(*)::text || ':' ||
                            md5(COALESCE(
                                string_agg(
                                    to_jsonb(pet_row)::text,
                                    '|' ORDER BY pet_row.id),
                                ''))
                         FROM public.character_pets pet_row) ||
                        '|' ||
                        (SELECT count(*)::text || ':' ||
                            md5(COALESCE(
                                string_agg(
                                    to_jsonb(stat_row)::text,
                                    '|' ORDER BY
                                        stat_row.pet_id,
                                        stat_row.stat_code),
                                ''))
                         FROM public.character_pet_stat_values stat_row);
                    """)
                : null;
        var economyFingerprint =
            await RelationExistsAsync(
                connection,
                "public.character_economy_baseline") &&
            await RelationExistsAsync(
                connection,
                "public.character_inventory_baseline_items") &&
            await RelationExistsAsync(
                connection,
                "public.character_currency_ledger") &&
            await RelationExistsAsync(
                connection,
                "public.character_inventory_ledger")
                ? await ReadTextAsync(connection, """
                    SELECT
                        (SELECT count(*)::text || ':' ||
                            md5(COALESCE(
                                string_agg(
                                    to_jsonb(baseline_row)::text,
                                    '|' ORDER BY
                                        baseline_row.character_id),
                                ''))
                         FROM public.character_economy_baseline
                             baseline_row) ||
                        '|' ||
                        (SELECT count(*)::text || ':' ||
                            md5(COALESCE(
                                string_agg(
                                    to_jsonb(item_row)::text,
                                    '|' ORDER BY
                                        item_row.character_id,
                                        item_row.item_instance_id),
                                ''))
                         FROM public.character_inventory_baseline_items
                             item_row) ||
                        '|' ||
                        (SELECT count(*)::text || ':' ||
                            md5(COALESCE(
                                string_agg(
                                    to_jsonb(currency_row)::text,
                                    '|' ORDER BY currency_row.id),
                                ''))
                         FROM public.character_currency_ledger
                             currency_row) ||
                        '|' ||
                        (SELECT count(*)::text || ':' ||
                            md5(COALESCE(
                                string_agg(
                                    to_jsonb(inventory_row)::text,
                                    '|' ORDER BY inventory_row.id),
                                ''))
                         FROM public.character_inventory_ledger
                             inventory_row);
                    """)
                : null;

        var packetRelationCount = await ReadInt32Async(connection, """
            SELECT count(*)::integer
            FROM unnest(ARRAY[
                'packet_capture_sessions',
                'packet_transactions',
                'packet_opcodes'
            ]::text[]) AS expected(table_name)
            WHERE to_regclass(
                'public.' || quote_ident(expected.table_name)) IS NOT NULL;
            """);
        var hasFunction = await ReadBooleanAsync(connection, """
            SELECT to_regprocedure(
                'public.set_packet_transaction_opcode_name()') IS NOT NULL;
            """);
        var triggerCount = await ReadInt32Async(connection, """
            SELECT count(*)::integer
            FROM pg_trigger
            WHERE tgrelid = to_regclass('public.packet_transactions')
              AND tgname = 'trg_packet_transactions_opcode_name'
              AND NOT tgisinternal;
            """);
        var captureForeignKeyCount = await ReadInt32Async(connection, """
            SELECT count(*)::integer
            FROM pg_constraint
            WHERE conrelid = to_regclass('public.packet_transactions')
              AND confrelid = to_regclass('public.packet_capture_sessions')
              AND contype = 'f'
              AND confdeltype = 'c'
              AND convalidated;
            """);
        var checkpointConstraintCount = await ReadInt32Async(connection, """
            SELECT count(*)::integer
            FROM pg_constraint
            WHERE conrelid = to_regclass('public.character_base')
              AND conname = ANY(ARRAY[
                  'ck_character_base_position_revision',
                  'ck_character_base_vitals_revision',
                  'ck_character_base_checkpoint_owner_generation',
                  'ck_character_base_checkpoint_owner_pair'
              ]::text[])
              AND contype = 'c'
              AND convalidated;
            """);
        var unvalidatedConstraints = await ReadInt32Async(connection, """
            SELECT count(*)::integer
            FROM pg_constraint constraint_row
            JOIN pg_namespace schema_row
              ON schema_row.oid = constraint_row.connamespace
            WHERE schema_row.nspname IN ('public', 'legacy')
              AND NOT constraint_row.convalidated;
            """);
        var invalidIndexes = await ReadInt32Async(connection, """
            SELECT count(*)::integer
            FROM pg_index index_row
            JOIN pg_class index_class
              ON index_class.oid = index_row.indexrelid
            JOIN pg_namespace schema_row
              ON schema_row.oid = index_class.relnamespace
            WHERE schema_row.nspname IN ('public', 'legacy')
              AND (NOT index_row.indisvalid OR NOT index_row.indisready);
            """);

        var releaseFingerprint = string.Join(
            "|",
            migrations.Select(static migration =>
                $"{migration.Id}:{migration.Checksum}")) +
            $"|{inventoryFingerprint}|" +
            $"{accountCharacterFingerprint}|" +
            $"{packetPayloadFingerprint}|{petFingerprint}|" +
            $"{economyFingerprint}|" +
            $"{checkpointFingerprint}|" +
            $"{lifecycle.Fingerprint}|" +
            $"{packetRelationCount}:{hasFunction}:{triggerCount}:" +
            $"{captureForeignKeyCount}:{checkpointColumnCount}:" +
            $"{checkpointConstraintCount}:{lifecycle.ColumnCount}:" +
            $"{lifecycle.ConstraintCount}:{lifecycle.IndexCount}:" +
            $"{lifecycle.AccountColumnCount}:" +
            $"{lifecycle.AccountConstraintCount}:" +
            $"{classSuitAttributeColumnCount}:" +
            $"{unvalidatedConstraints}:" +
            $"{invalidIndexes}";

        return new SchemaReleaseSnapshot(
            markerCount,
            migrations,
            inventoryFingerprint,
            inventoryRows,
            accountCharacterFingerprint,
            checkpointFingerprint,
            lifecycle.Fingerprint,
            packetPayloadFingerprint,
            petFingerprint,
            economyFingerprint,
            packetRelationCount,
            hasFunction,
            triggerCount,
            captureForeignKeyCount,
            checkpointColumnCount,
            checkpointConstraintCount,
            lifecycle.ColumnCount,
            lifecycle.ConstraintCount,
            lifecycle.IndexCount,
            lifecycle.AccountColumnCount,
            lifecycle.AccountConstraintCount,
            classSuitAttributeColumnCount,
            unvalidatedConstraints,
            invalidIndexes,
            releaseFingerprint);
    }

}
