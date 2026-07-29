using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresSchemaReleaseIntegrationChecks
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

        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        var after = await ReadSnapshotAsync(dataSource);
        AssertReleaseState(after);
        AssertDurableStatePreserved(before, after);

        await using (var reopenedStore = new PostgresGameStore(connectionString))
        {
            await reopenedStore.EnsureSeedDataAsync();
        }
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

    private static void AssertReleaseState(SchemaReleaseSnapshot snapshot)
    {
        Check.Equal(
            PostgresSchemaMigrationCatalog.All.Count,
            snapshot.AppliedMigrations.Count,
            "release has the exact registered migration count");
        for (var index = 0;
             index < PostgresSchemaMigrationCatalog.All.Count;
             index++)
        {
            var expected = PostgresSchemaMigrationCatalog.All[index];
            var actual = snapshot.AppliedMigrations[index];
            Check.Equal(
                expected.Id,
                actual.Id,
                $"release migration {index} ID");
            Check.Equal(
                expected.Checksum,
                actual.Checksum,
                $"release migration {expected.Id} checksum");
        }

        Check.Equal(3, snapshot.PacketRelationCount, "all packet metadata tables exist");
        Check.True(
            snapshot.HasOpcodeNameFunction,
            "packet opcode-name trigger function exists");
        Check.Equal(
            1,
            snapshot.OpcodeNameTriggerCount,
            "packet transaction opcode-name trigger exists once");
        Check.Equal(
            1,
            snapshot.PacketCaptureForeignKeyCount,
            "packet transactions retain the capture-session cascade foreign key");
        Check.Equal(0, snapshot.UnvalidatedConstraintCount, "all constraints validate");
        Check.Equal(0, snapshot.InvalidIndexCount, "all indexes are valid and ready");
    }

    private static void AssertDurableStatePreserved(
        SchemaReleaseSnapshot before,
        SchemaReleaseSnapshot after)
    {
        if (before.InventoryFingerprint is not null)
        {
            Check.Equal(
                before.InventoryFingerprint,
                after.InventoryFingerprint
                ?? throw new InvalidOperationException(
                    "Authoritative inventory disappeared during migration."),
                "schema release preserves authoritative inventory byte-for-byte");
        }

        if (before.AccountCharacterFingerprint is not null)
        {
            Check.Equal(
                before.AccountCharacterFingerprint,
                after.AccountCharacterFingerprint
                ?? throw new InvalidOperationException(
                    "Account or character state disappeared during migration."),
                "schema release preserves account and character identity rows");
        }

        if (before.PacketPayloadFingerprint is not null)
        {
            Check.Equal(
                before.PacketPayloadFingerprint,
                after.PacketPayloadFingerprint
                ?? throw new InvalidOperationException(
                    "Captured packet payloads disappeared during migration."),
                "schema release preserves captured packet bytes");
        }

        if (before.AppliedMigrations.Count ==
                PostgresSchemaMigrationCatalog.All.Count &&
            before.PetFingerprint is not null)
        {
            Check.Equal(
                before.PetFingerprint,
                after.PetFingerprint
                ?? throw new InvalidOperationException(
                    "Authoritative pet state disappeared during startup."),
                "current release startup preserves authoritative pet rows");
        }

        if (before.AppliedMigrations.Count ==
                PostgresSchemaMigrationCatalog.All.Count &&
            before.EconomyFingerprint is not null)
        {
            Check.Equal(
                before.EconomyFingerprint,
                after.EconomyFingerprint
                ?? throw new InvalidOperationException(
                    "Economy baseline or ledger evidence disappeared during startup."),
                "current release startup preserves economy evidence rows");
        }
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
        var accountCharacterFingerprint =
            await RelationExistsAsync(connection, "public.accounts") &&
            await RelationExistsAsync(connection, "public.character_base")
                ? await ReadTextAsync(connection, """
                    SELECT
                        (SELECT count(*)::text || ':' ||
                            md5(COALESCE(
                                string_agg(
                                    to_jsonb(account_row)::text,
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
                                            'inventory_revision'
                                        ]::text[]
                                    )::text,
                                    '|' ORDER BY character_row.id),
                                ''))
                         FROM public.character_base character_row);
                    """)
                : null;
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
            $"|{inventoryFingerprint}|{accountCharacterFingerprint}|" +
            $"{packetPayloadFingerprint}|{petFingerprint}|" +
            $"{economyFingerprint}|" +
            $"{packetRelationCount}:{hasFunction}:{triggerCount}:" +
            $"{captureForeignKeyCount}:{unvalidatedConstraints}:{invalidIndexes}";

        return new SchemaReleaseSnapshot(
            markerCount,
            migrations,
            inventoryFingerprint,
            accountCharacterFingerprint,
            packetPayloadFingerprint,
            petFingerprint,
            economyFingerprint,
            packetRelationCount,
            hasFunction,
            triggerCount,
            captureForeignKeyCount,
            unvalidatedConstraints,
            invalidIndexes,
            releaseFingerprint);
    }

    private static async Task<bool> RelationExistsAsync(
        NpgsqlConnection connection,
        string qualifiedName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualifiedName) IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue("qualifiedName", qualifiedName);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Relation check returned null."));
    }

    private static async Task<int> ReadInt32Async(
        NpgsqlConnection connection,
        string sql) =>
        Convert.ToInt32(await ReadScalarAsync(connection, sql));

    private static async Task<bool> ReadBooleanAsync(
        NpgsqlConnection connection,
        string sql) =>
        Convert.ToBoolean(await ReadScalarAsync(connection, sql));

    private static async Task<string> ReadTextAsync(
        NpgsqlConnection connection,
        string sql) =>
        Convert.ToString(await ReadScalarAsync(connection, sql))
        ?? throw new InvalidOperationException("Text query returned null.");

    private static async Task<object> ReadScalarAsync(
        NpgsqlConnection connection,
        string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync()
               ?? throw new InvalidOperationException("Scalar query returned null.");
    }

    private sealed record SchemaReleaseSnapshot(
        int CoreMarkerCount,
        IReadOnlyList<AppliedPostgresSchemaMigration> AppliedMigrations,
        string? InventoryFingerprint,
        string? AccountCharacterFingerprint,
        string? PacketPayloadFingerprint,
        string? PetFingerprint,
        string? EconomyFingerprint,
        int PacketRelationCount,
        bool HasOpcodeNameFunction,
        int OpcodeNameTriggerCount,
        int PacketCaptureForeignKeyCount,
        int UnvalidatedConstraintCount,
        int InvalidIndexCount,
        string ReleaseFingerprint);
}
