using System.Text.RegularExpressions;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresEconomyLedgerMigrationIntegrationChecks
{
    internal const string CheckName =
        "PostgreSQL economy ledger migration foundation";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_SCHEMA_RELEASE_CONNECTION_STRING";
    private const string FoundationMigrationId =
        "20260729_027_economy_ledger_foundation";
    private const string HardeningMigrationId =
        "20260729_028_economy_ledger_hardening";

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_(?:empty|restored)|b09_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL economy ledger migration integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var databaseName = await ReadTextAsync(
            dataSource,
            "SELECT current_database();");
        if (!DisposableDatabasePattern.IsMatch(databaseName))
        {
            Console.WriteLine(
                "SKIP PostgreSQL economy ledger migration integration " +
                "requires a disposable godswar_b03_*_(empty|restored) " +
                $"or godswar_b09_* database; received '{databaseName}'");
            return;
        }

        await using (var store =
                     new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        await AssertAppliedMigrationsAsync(dataSource);
        await AssertSchemaObjectsAsync(dataSource);
        await AssertBackfillAndViewsAsync(dataSource);
        await AssertRejectedAsync(
            dataSource,
            seedValidBaseline: true,
            """
            UPDATE public.character_economy_baseline
            SET silver = silver + 1
            WHERE character_id = @characterId;
            """,
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            "baseline update is rejected by the append-only guard");
        await AssertRejectedAsync(
            dataSource,
            seedValidBaseline: true,
            """
            DELETE FROM public.character_economy_baseline
            WHERE character_id = @characterId;
            """,
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            "baseline delete is rejected by the append-only guard");
        await AssertRejectedAsync(
            dataSource,
            seedValidBaseline: false,
            """
            INSERT INTO public.character_economy_baseline (
                character_id,
                account_id,
                wallet_revision,
                inventory_revision,
                silver,
                gold,
                item_count,
                baseline_source
            )
            VALUES (
                @characterId,
                @accountId,
                1,
                0,
                0,
                0,
                0,
                'integration_constraint'
            );
            """,
            PostgresErrorCodes.CheckViolation,
            "nonzero baseline revision is rejected");
        await AssertRejectedAsync(
            dataSource,
            seedValidBaseline: true,
            """
            INSERT INTO public.character_currency_ledger (
                command_inbox_id,
                account_id,
                character_id,
                wallet_revision,
                currency_code,
                delta,
                balance_before,
                balance_after,
                reason_code
            )
            VALUES (
                -9223372036854775807,
                @accountId,
                @characterId,
                1,
                'silver',
                1,
                0,
                1,
                'integration_fk'
            );
            """,
            PostgresErrorCodes.ForeignKeyViolation,
            "ledger entry without a command inbox row is rejected");

        Console.WriteLine(
            "PostgreSQL economy ledger migrations, evidence guards, " +
            "and reconciliation views verified.");
    }

    private static async Task AssertAppliedMigrationsAsync(
        NpgsqlDataSource dataSource)
    {
        foreach (var migrationId in new[]
                 {
                     FoundationMigrationId,
                     HardeningMigrationId
                 })
        {
            var expected = PostgresSchemaMigrationCatalog.All.Single(
                migration => migration.Id == migrationId);
            await using var command = dataSource.CreateCommand("""
                SELECT checksum
                FROM public.schema_migrations
                WHERE migration_id = @migrationId;
                """);
            command.Parameters.AddWithValue(
                "migrationId",
                migrationId);
            var actual = await command.ExecuteScalarAsync() as string;
            Check.Equal(
                expected.Checksum,
                actual ?? throw new InvalidOperationException(
                    $"Migration {migrationId} was not applied."),
                $"{migrationId} applied checksum");
        }
    }

    private static async Task AssertSchemaObjectsAsync(
        NpgsqlDataSource dataSource)
    {
        Check.Equal(
            2,
            await ReadInt32Async(dataSource, """
                SELECT count(*)::integer
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'character_base'
                  AND column_name IN (
                      'wallet_revision',
                      'inventory_revision'
                  )
                  AND data_type = 'bigint'
                  AND is_nullable = 'NO';
                """),
            "both authoritative revision columns exist");
        Check.Equal(
            4,
            await ReadInt32Async(dataSource, """
                WITH expected(table_name) AS (
                    VALUES
                        ('character_economy_baseline'),
                        ('character_inventory_baseline_items'),
                        ('character_currency_ledger'),
                        ('character_inventory_ledger')
                )
                SELECT count(*)::integer
                FROM expected
                JOIN pg_class relation_row
                  ON relation_row.oid = to_regclass(
                      'public.' || quote_ident(expected.table_name))
                 AND relation_row.relkind = 'r';
                """),
            "all four economy evidence tables exist");
        Check.Equal(
            2,
            await ReadInt32Async(dataSource, """
                WITH expected(view_name) AS (
                    VALUES
                        ('character_wallet_reconciliation'),
                        ('character_inventory_reconciliation')
                )
                SELECT count(*)::integer
                FROM expected
                JOIN pg_class relation_row
                  ON relation_row.oid = to_regclass(
                      'public.' || quote_ident(expected.view_name))
                 AND relation_row.relkind = 'v';
                """),
            "both report-only reconciliation views exist");
        Check.Equal(
            0,
            await ReadInt32Async(dataSource, """
                SELECT count(*)::integer
                FROM pg_constraint
                WHERE conrelid = ANY (ARRAY[
                    to_regclass('public.character_base'),
                    to_regclass('public.character_items'),
                    to_regclass('public.character_economy_baseline'),
                    to_regclass(
                        'public.character_inventory_baseline_items'),
                    to_regclass('public.character_currency_ledger'),
                    to_regclass('public.character_inventory_ledger')
                ])
                  AND NOT convalidated;
                """),
            "all economy and inventory constraints are validated");
        Check.Equal(
            8,
            await ReadInt32Async(dataSource, """
                WITH expected(table_name, trigger_name) AS (
                    VALUES
                        (
                            'character_economy_baseline',
                            'trg_character_economy_baseline_immutable'
                        ),
                        (
                            'character_economy_baseline',
                            'trg_character_economy_baseline_no_truncate'
                        ),
                        (
                            'character_inventory_baseline_items',
                            'trg_character_inventory_baseline_items_immutable'
                        ),
                        (
                            'character_inventory_baseline_items',
                            'trg_character_inventory_baseline_items_no_truncate'
                        ),
                        (
                            'character_currency_ledger',
                            'trg_character_currency_ledger_immutable'
                        ),
                        (
                            'character_currency_ledger',
                            'trg_character_currency_ledger_no_truncate'
                        ),
                        (
                            'character_inventory_ledger',
                            'trg_character_inventory_ledger_immutable'
                        ),
                        (
                            'character_inventory_ledger',
                            'trg_character_inventory_ledger_no_truncate'
                        )
                )
                SELECT count(*)::integer
                FROM expected
                JOIN pg_trigger trigger_row
                  ON trigger_row.tgrelid = to_regclass(
                      'public.' || quote_ident(expected.table_name))
                 AND trigger_row.tgname = expected.trigger_name
                 AND NOT trigger_row.tgisinternal
                 AND trigger_row.tgfoid = to_regprocedure(
                     'public.reject_character_economy_evidence_mutation()');
                """),
            "all eight immutable evidence triggers exist");
    }

    private static async Task AssertBackfillAndViewsAsync(
        NpgsqlDataSource dataSource)
    {
        Check.Equal(
            0,
            await ReadInt32Async(dataSource, """
                WITH current_item_counts AS (
                    SELECT
                        user_id AS character_id,
                        count(*)::integer AS item_count
                    FROM public.character_items
                    GROUP BY user_id
                )
                SELECT count(*)::integer
                FROM public.character_base character_row
                FULL OUTER JOIN public.character_economy_baseline
                    baseline_row
                  ON baseline_row.character_id = character_row.id
                LEFT JOIN current_item_counts item_count_row
                  ON item_count_row.character_id = COALESCE(
                      character_row.id,
                      baseline_row.character_id
                  )
                WHERE character_row.id IS NULL
                   OR baseline_row.character_id IS NULL
                   OR baseline_row.account_id <>
                      character_row.account_id
                   OR baseline_row.wallet_revision <> 0
                   OR baseline_row.inventory_revision <> 0
                   OR character_row.wallet_revision <> 0
                   OR character_row.inventory_revision <> 0
                   OR baseline_row.silver <>
                      character_row."Money"::bigint
                   OR baseline_row.gold <>
                      character_row."Stone"::bigint
                   OR baseline_row.item_count <>
                      COALESCE(item_count_row.item_count, 0);
                """),
            "character wallet and item-count baselines match current rows");
        Check.Equal(
            0,
            await ReadInt32Async(dataSource, """
                SELECT count(*)::integer
                FROM public.character_items item_row
                FULL OUTER JOIN
                    public.character_inventory_baseline_items snapshot_row
                  ON snapshot_row.character_id = item_row.user_id
                 AND snapshot_row.item_instance_id = item_row.id
                LEFT JOIN public.character_base character_row
                  ON character_row.id = COALESCE(
                      item_row.user_id,
                      snapshot_row.character_id
                  )
                WHERE item_row.id IS NULL
                   OR snapshot_row.item_instance_id IS NULL
                   OR character_row.id IS NULL
                   OR snapshot_row.account_id <>
                      character_row.account_id
                   OR snapshot_row.item_location <>
                      item_row.item_location
                   OR snapshot_row.slot_index <> item_row.slot_index
                   OR snapshot_row.prop_id <> item_row.prop_id
                   OR snapshot_row.item_state IS DISTINCT FROM
                      CASE
                          WHEN item_row.id IS NULL THEN NULL::jsonb
                          ELSE to_jsonb(item_row)
                      END;
                """),
            "per-item baseline snapshots preserve every current item");
        Check.Equal(
            0,
            await ReadInt32Async(dataSource, """
                SELECT count(*)::integer
                FROM public.character_wallet_reconciliation
                WHERE is_reconciled IS DISTINCT FROM TRUE;
                """),
            "wallet reconciliation reports no baseline drift");
        Check.Equal(
            0,
            await ReadInt32Async(dataSource, """
                SELECT count(*)::integer
                FROM public.character_inventory_reconciliation
                WHERE is_reconciled IS DISTINCT FROM TRUE;
                """),
            "inventory reconciliation reports no baseline drift");
    }

    private static async Task AssertRejectedAsync(
        NpgsqlDataSource dataSource,
        bool seedValidBaseline,
        string sql,
        string expectedSqlState,
        string description)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        var characterId = await ReadUnusedPositiveIdAsync(
            connection,
            transaction);

        if (seedValidBaseline)
        {
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO public.character_economy_baseline (
                    character_id,
                    account_id,
                    wallet_revision,
                    inventory_revision,
                    silver,
                    gold,
                    item_count,
                    baseline_source
                )
                VALUES (
                    @characterId,
                    @accountId,
                    0,
                    0,
                    0,
                    0,
                    0,
                    'integration_guard'
                );
                """,
                connection,
                transaction);
            AddIdentityParameters(seed, characterId);
            await seed.ExecuteNonQueryAsync();
        }

        var rejected = false;
        try
        {
            await using var command =
                new NpgsqlCommand(sql, connection, transaction);
            AddIdentityParameters(command, characterId);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
            when (exception.SqlState == expectedSqlState)
        {
            rejected = true;
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Check.True(rejected, description);
    }

    private static async Task<int> ReadUnusedPositiveIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT candidate::integer
            FROM generate_series(
                2147483647,
                2147483000,
                -1
            ) AS candidate
            WHERE NOT EXISTS (
                SELECT 1
                FROM public.character_economy_baseline baseline_row
                WHERE baseline_row.character_id = candidate
            )
            LIMIT 1;
            """,
            connection,
            transaction);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException(
                "No positive synthetic baseline identity is available."));
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        int characterId)
    {
        command.Parameters.AddWithValue(
            "characterId",
            characterId);
        command.Parameters.AddWithValue(
            "accountId",
            characterId);
    }

    private static async Task<int> ReadInt32Async(
        NpgsqlDataSource dataSource,
        string sql) =>
        Convert.ToInt32(await ReadScalarAsync(dataSource, sql));

    private static async Task<string> ReadTextAsync(
        NpgsqlDataSource dataSource,
        string sql) =>
        Convert.ToString(await ReadScalarAsync(dataSource, sql))
        ?? throw new InvalidOperationException(
            "PostgreSQL text query returned null.");

    private static async Task<object> ReadScalarAsync(
        NpgsqlDataSource dataSource,
        string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync()
               ?? throw new InvalidOperationException(
                   "PostgreSQL scalar query returned null.");
    }
}
