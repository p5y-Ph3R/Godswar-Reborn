using System.Text.Json;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationPrefixFixtureChecks
{
    private const string ElementalMigrationId =
        "20260803_054_elemental_class_suit_attributes";

    private static async Task
        AssertElementalMigrationRejectsLossyHistoryAsync(
            NpgsqlDataSource dataSource)
    {
        var fixtures = new[]
        {
            new HistoricalAttributeFixture(
                "two legacy ordinary attributes",
                new Dictionary<string, object?>
                {
                    ["attribute1"] = 200,
                    ["attribute2"] = 201
                }),
            new HistoricalAttributeFixture(
                "camel-case second Class Suit attribute",
                new Dictionary<string, object?>
                {
                    ["attribute1"] = 40,
                    ["classAttribute2"] = 210
                }),
            new HistoricalAttributeFixture(
                "mixed dedicated and legacy attributes",
                new Dictionary<string, object?>
                {
                    ["attribute1"] = 201,
                    ["class_attribute1"] = 200
                })
        };

        for (var index = 0; index < fixtures.Length; index++)
        {
            var identity = 1_900_000_000 + index;
            await InsertHistoricalFixtureAsync(
                dataSource,
                identity,
                fixtures[index].Fields);
            try
            {
                var rejected = false;
                try
                {
                    await new PostgresSchemaMigrationRunner(dataSource)
                        .InitializeGodswarSchemaAsync();
                }
                catch (PostgresException exception)
                    when (exception.MessageText.Contains(
                        "migration 054 requires operator repair",
                        StringComparison.Ordinal))
                {
                    rejected = true;
                }

                Check.True(
                    rejected,
                    $"migration 054 rejects {fixtures[index].Name}");
                await AssertElementalMigrationRolledBackAsync(
                    dataSource,
                    fixtures[index].Name);
            }
            finally
            {
                await DeleteHistoricalFixtureAsync(dataSource, identity);
            }
        }
    }

    private static async Task InsertHistoricalFixtureAsync(
        NpgsqlDataSource dataSource,
        int identity,
        IReadOnlyDictionary<string, object?> fields)
    {
        var state = new Dictionary<string, object?>(fields)
        {
            ["id"] = (long)identity,
            ["user_id"] = identity,
            ["item_location"] = 1,
            ["slot_index"] = 0,
            ["prop_id"] = 1000
        };
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var baseline = new NpgsqlCommand(
            """
            INSERT INTO public.character_economy_baseline (
                character_id, account_id, wallet_revision,
                inventory_revision, silver, gold, item_count,
                baseline_source)
            VALUES (@identity, @identity, 0, 0, 0, 0, 1,
                    'migration054-regression');
            """,
            connection,
            transaction))
        {
            baseline.Parameters.AddWithValue("identity", identity);
            await baseline.ExecuteNonQueryAsync();
        }

        await using (var item = new NpgsqlCommand(
            """
            INSERT INTO public.character_inventory_baseline_items (
                character_id, account_id, item_instance_id,
                item_location, slot_index, prop_id,
                state_contract_version, item_state)
            VALUES (@identity, @identity, @identity, 1, 0, 1000, 1,
                    @state);
            """,
            connection,
            transaction))
        {
            item.Parameters.AddWithValue("identity", identity);
            item.Parameters.Add("state", NpgsqlDbType.Jsonb).Value =
                JsonSerializer.Serialize(state);
            await item.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task AssertElementalMigrationRolledBackAsync(
        NpgsqlDataSource dataSource,
        string fixtureName)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                NOT EXISTS (
                    SELECT 1
                    FROM public.schema_migrations
                    WHERE migration_id = @migrationId),
                NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'character_items'
                      AND column_name IN (
                          'elemental_attribute1',
                          'elemental_attribute2'));
            """);
        command.Parameters.AddWithValue("migrationId", ElementalMigrationId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetBoolean(0) &&
            reader.GetBoolean(1),
            $"migration 054 fully rolls back for {fixtureName}");
    }

    private static async Task DeleteHistoricalFixtureAsync(
        NpgsqlDataSource dataSource,
        int identity)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            ALTER TABLE public.character_inventory_baseline_items
                DISABLE TRIGGER
                    trg_character_inventory_baseline_items_immutable;
            ALTER TABLE public.character_economy_baseline
                DISABLE TRIGGER trg_character_economy_baseline_immutable;
            DELETE FROM public.character_inventory_baseline_items
            WHERE character_id = @identity;
            DELETE FROM public.character_economy_baseline
            WHERE character_id = @identity;
            ALTER TABLE public.character_inventory_baseline_items
                ENABLE TRIGGER
                    trg_character_inventory_baseline_items_immutable;
            ALTER TABLE public.character_economy_baseline
                ENABLE TRIGGER trg_character_economy_baseline_immutable;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("identity", identity);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private sealed record HistoricalAttributeFixture(
        string Name,
        IReadOnlyDictionary<string, object?> Fields);
}
