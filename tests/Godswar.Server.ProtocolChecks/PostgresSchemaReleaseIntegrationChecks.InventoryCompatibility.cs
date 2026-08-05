using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresSchemaReleaseIntegrationChecks
{
    private static async Task<IReadOnlyList<InventoryRowSnapshot>>
        ReadInventoryRowsAsync(NpgsqlConnection connection)
    {
        var rows = new List<InventoryRowSnapshot>();
        await using var command = new NpgsqlCommand(
            """
            SELECT item_row.id, to_jsonb(item_row)::text
            FROM public.character_items item_row
            ORDER BY item_row.id;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new InventoryRowSnapshot(
                reader.GetInt64(0),
                reader.GetString(1)));
        }

        return rows;
    }

    private static async Task AssertClassSuitMigrationPreservedAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<InventoryRowSnapshot> beforeRows)
    {
        var snapshot = new JsonArray();
        foreach (var row in beforeRows)
        {
            snapshot.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["state"] = JsonNode.Parse(row.StateJson)
            });
        }

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH before_items AS (
                SELECT
                    (entry ->> 'id')::bigint AS id,
                    entry -> 'state' AS item_state
                FROM jsonb_array_elements(@inventory_snapshot::jsonb) entry
            ),
            canonical_before AS (
                SELECT
                    before_item.id,
                    before_item.item_state,
                    public.canonical_character_item_state_v2(
                        before_item.item_state) AS canonical_state
                FROM before_items before_item
            ),
            expected_items AS (
                SELECT
                    canonical_before.id,
                    (CASE
                        WHEN canonical_before.canonical_state
                                -> 'class_attribute1' <> 'null'::jsonb
                            THEN canonical_before.canonical_state
                        ELSE canonical_before.item_state ||
                            jsonb_build_object(
                                'class_attribute1', NULL,
                                'class_attribute2', NULL)
                    END) || jsonb_build_object(
                        'elemental_attribute1', NULL,
                        'elemental_attribute2', NULL,
                        'holy_socket1_value', NULL,
                        'holy_socket2_value', NULL,
                        'holy_socket3_value', NULL,
                        'holy_socket4_value', NULL) AS item_state
                FROM canonical_before
            ),
            actual_items AS (
                SELECT
                    item_row.id,
                    to_jsonb(item_row) AS item_state
                FROM public.character_items item_row
            )
            SELECT COALESCE(expected_item.id, actual_item.id)
            FROM expected_items expected_item
            FULL OUTER JOIN actual_items actual_item
                ON actual_item.id = expected_item.id
            WHERE expected_item.item_state IS DISTINCT FROM
                  actual_item.item_state
            ORDER BY COALESCE(expected_item.id, actual_item.id)
            LIMIT 1;
            """,
            connection);
        command.Parameters.Add(
            "inventory_snapshot",
            NpgsqlDbType.Jsonb).Value = snapshot.ToJsonString();
        var mismatchItemId = await command.ExecuteScalarAsync();

        Check.True(
            mismatchItemId is null,
            mismatchItemId is null
                ? "migrations 053-054 preserve authoritative inventory semantics"
                : $"migrations 053-054 changed unrelated durable state for item " +
                  $"{mismatchItemId}");
    }
}
