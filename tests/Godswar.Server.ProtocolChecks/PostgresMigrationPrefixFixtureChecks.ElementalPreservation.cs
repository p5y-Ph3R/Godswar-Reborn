using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationPrefixFixtureChecks
{
    private static readonly Regex B03TemplateDatabasePattern = new(
        @"^godswar_b03_[a-f0-9]{10}_smoke_template$",
        RegexOptions.CultureInvariant);

    private static async Task
        AssertElementalMigrationPreservesSingleLegacyClassAsync(
            string connectionString)
    {
        var source = new NpgsqlConnectionStringBuilder(connectionString);
        var sourceDatabase = source.Database ?? string.Empty;
        if (!B03TemplateDatabasePattern.IsMatch(sourceDatabase))
        {
            Console.WriteLine(
                "SKIP migration054 preservation clone requires the " +
                "disposable B03 smoke-template database");
            return;
        }

        var guardDatabase = sourceDatabase.Replace(
            "_smoke_template",
            "_m054_guard",
            StringComparison.Ordinal);
        var maintenance = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        var quotedDatabase = new NpgsqlCommandBuilder()
            .QuoteIdentifier(guardDatabase);
        var created = false;
        try
        {
            await using (var connection =
                         new NpgsqlConnection(maintenance.ConnectionString))
            {
                await connection.OpenAsync();
                await DropGuardDatabaseAsync(
                    connection,
                    guardDatabase,
                    quotedDatabase);
                await using var create = new NpgsqlCommand(
                    $"CREATE DATABASE {quotedDatabase};",
                    connection);
                await create.ExecuteNonQueryAsync();
                created = true;
            }

            var guard = new NpgsqlConnectionStringBuilder(source.ConnectionString)
            {
                Database = guardDatabase,
                Pooling = false
            };
            await using var dataSource =
                NpgsqlDataSource.Create(guard.ConnectionString);
            var prefix = PostgresSchemaMigrationCatalog.All
                .TakeWhile(static migration =>
                    migration.Id != ElementalMigrationId)
                .ToArray();
            var runner = new PostgresSchemaMigrationRunner(dataSource);
            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                prefix);
            await InsertSingleLegacyClassHistoryAsync(dataSource);
            await runner.InitializeGodswarSchemaAsync();
            await AssertSingleLegacyClassPreservedAsync(dataSource);
        }
        finally
        {
            if (created)
            {
                NpgsqlConnection.ClearAllPools();
                await using var connection =
                    new NpgsqlConnection(maintenance.ConnectionString);
                await connection.OpenAsync();
                await DropGuardDatabaseAsync(
                    connection,
                    guardDatabase,
                    quotedDatabase);
            }
        }
    }

    private static async Task DropGuardDatabaseAsync(
        NpgsqlConnection connection,
        string database,
        string quotedDatabase)
    {
        await using (var terminate = new NpgsqlCommand(
            """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @database
              AND pid <> pg_backend_pid();
            """,
            connection))
        {
            terminate.Parameters.AddWithValue("database", database);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {quotedDatabase};",
            connection);
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task InsertSingleLegacyClassHistoryAsync(
        NpgsqlDataSource dataSource)
    {
        const int identity = 1_900_000_100;
        var baselineState = HistoricalState(identity, itemExperience: 0);
        var afterState = HistoricalState(identity, itemExperience: 1);
        var operationId = Guid.NewGuid().ToByteArray();
        var requestHash = SHA256.HashData(operationId);
        var resultHash = SHA256.HashData(requestHash);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_economy_baseline (
                character_id, account_id, wallet_revision,
                inventory_revision, silver, gold, item_count,
                baseline_source)
            VALUES (@identity, @identity, 0, 0, 0, 0, 1,
                    'migration054-preservation');

            INSERT INTO public.character_inventory_baseline_items (
                character_id, account_id, item_instance_id,
                item_location, slot_index, prop_id,
                state_contract_version, item_state)
            VALUES (@identity, @identity, @identity, 1, 0, 1000, 1,
                    @beforeState);

            WITH audit AS (
                INSERT INTO public.command_audit (
                    principal_type, principal_key,
                    aggregate_type, aggregate_key, command_family,
                    operation_id, request_hash, outcome_code,
                    detail_payload)
                VALUES (
                    'account', @identityText,
                    'character_economy', @aggregateKey,
                    'migration054_preservation', @operationId,
                    @requestHash, 'committed',
                    '{"fixture":"migration054"}'::jsonb)
                RETURNING id
            ), inbox AS (
                INSERT INTO public.command_inbox (
                    principal_type, principal_key,
                    aggregate_type, aggregate_key, command_family,
                    operation_id, request_hash,
                    result_contract_version, result_code,
                    result_payload, result_hash, audit_id)
                SELECT
                    'account', @identityText,
                    'character_economy', @aggregateKey,
                    'migration054_preservation', @operationId,
                    @requestHash, 1, 'committed',
                    '{"status":"committed"}'::jsonb,
                    @resultHash, audit.id
                FROM audit
                RETURNING id
            )
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id, account_id, character_id,
                inventory_revision, entry_ordinal, item_instance_id,
                mutation_kind, state_contract_version,
                before_state, after_state, reason_code)
            SELECT inbox.id, @identity, @identity, 1, 0, @identity,
                   'update', 1, @beforeState, @afterState,
                   'migration054.preservation'
            FROM inbox;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("identity", identity);
        command.Parameters.AddWithValue("identityText", identity.ToString());
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{identity}:economy");
        command.Parameters.AddWithValue("operationId", operationId);
        command.Parameters.AddWithValue("requestHash", requestHash);
        command.Parameters.AddWithValue("resultHash", resultHash);
        command.Parameters.Add("beforeState", NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(baselineState);
        command.Parameters.Add("afterState", NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(afterState);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static Dictionary<string, object?> HistoricalState(
        int identity,
        int itemExperience) =>
        new()
        {
            ["id"] = (long)identity,
            ["user_id"] = identity,
            ["item_location"] = 1,
            ["slot_index"] = 0,
            ["prop_id"] = 1000,
            ["attribute3"] = 200,
            ["attribute_level3"] = 1,
            ["item_exp"] = itemExperience
        };

    private static async Task AssertSingleLegacyClassPreservedAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            WITH states AS (
                SELECT item_state AS state
                FROM public.character_inventory_baseline_items
                WHERE character_id = 1900000100
                UNION ALL
                SELECT before_state
                FROM public.character_inventory_ledger
                WHERE character_id = 1900000100
                UNION ALL
                SELECT after_state
                FROM public.character_inventory_ledger
                WHERE character_id = 1900000100
            )
            SELECT
                count(*) = 3,
                bool_and(
                    public.canonical_character_item_state_v3(state)
                        ->> 'class_attribute1' = '200'
                    AND public.canonical_character_item_state_v3(state)
                        ->> 'class_attribute2' IS NULL
                    AND public.canonical_character_item_state_v3(state)
                        ->> 'attribute3' IS NULL),
                EXISTS (
                    SELECT 1
                    FROM public.schema_migrations
                    WHERE migration_id =
                        '20260803_054_elemental_class_suit_attributes')
            FROM states;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetBoolean(0) &&
            reader.GetBoolean(1) &&
            reader.GetBoolean(2),
            "migration 054 preserves one legacy Class Suit attribute in " +
            "baseline and ledger history while clearing its ordinary slot");
    }
}
