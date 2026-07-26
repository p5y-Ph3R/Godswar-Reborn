using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearMentorIntegrationChecks
{
    private static string CreateSingleConnectionPoolString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 1,
            Timeout = 2,
            ApplicationName = $"gear-mentor-readback-{Guid.NewGuid():N}"
        };
        return builder.ConnectionString;
    }

    private static async Task StageAuthoritativeRowsAsync(
        string connectionString,
        int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var updateGear = new NpgsqlCommand("""
            UPDATE character_items
            SET attribute1 = 0,
                attribute2 = 10,
                attribute3 = 20,
                attribute4 = 18,
                attribute5 = 16,
                attribute_level1 = 25,
                attribute_level2 = 24,
                attribute_level3 = 23,
                attribute_level4 = 22,
                attribute_level5 = 21,
                item_quality = 20,
                item_grade = 25,
                bound = 1,
                stack = 1,
                item_exp = 2147000000,
                holy_suit_code = 710,
                holy_socket_count = 4,
                holy_socket1_effect_id = 7,
                holy_socket1_level = 5,
                holy_socket2_effect_id = 8,
                holy_socket2_level = 4,
                holy_socket3_effect_id = 9,
                holy_socket3_level = 3,
                holy_socket4_effect_id = 10,
                holy_socket4_level = 2,
                updated_at = now()
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @gearSlot
              AND prop_id = 1000;
            """, connection, transaction))
        {
            updateGear.Parameters.AddWithValue("characterId", characterId);
            updateGear.Parameters.AddWithValue("gearSlot", checked((short)PreservedGearSlot));
            Check.Equal(
                1,
                await updateGear.ExecuteNonQueryAsync(),
                "PostgreSQL Gear Mentor preservation fixture seeded");
        }

        await UpsertMaterialAsync(
            connection,
            transaction,
            characterId,
            FillerSlotA,
            itemId: 4230,
            stack: 1,
            bound: 0);
        await UpsertMaterialAsync(
            connection,
            transaction,
            characterId,
            FillerSlotB,
            itemId: 4231,
            stack: 1,
            bound: 0);
        await UpsertMaterialAsync(
            connection,
            transaction,
            characterId,
            RecipeSlot,
            itemId: 9900,
            stack: 99,
            bound: 1);
        await transaction.CommitAsync();
    }

    private static async Task StageMaterialAsync(
        string connectionString,
        int characterId,
        int slot,
        int itemId,
        short stack,
        short bound)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await UpsertMaterialAsync(
            connection,
            transaction,
            characterId,
            slot,
            itemId,
            stack,
            bound);
        await transaction.CommitAsync();
    }

    private static async Task UpsertMaterialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int slot,
        int itemId,
        short stack,
        short bound)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @slotIndex, @itemId,
                1, 1, @bound, @stack, 0, 0
            )
            ON CONFLICT (user_id, item_location, slot_index) DO UPDATE
            SET prop_id = EXCLUDED.prop_id,
                attribute1 = NULL,
                attribute2 = NULL,
                attribute3 = NULL,
                attribute4 = NULL,
                attribute5 = NULL,
                attribute_level1 = NULL,
                attribute_level2 = NULL,
                attribute_level3 = NULL,
                attribute_level4 = NULL,
                attribute_level5 = NULL,
                item_quality = EXCLUDED.item_quality,
                item_grade = EXCLUDED.item_grade,
                bound = EXCLUDED.bound,
                stack = EXCLUDED.stack,
                item_exp = EXCLUDED.item_exp,
                holy_suit_code = EXCLUDED.holy_suit_code,
                holy_socket_count = 0,
                holy_socket1_effect_id = NULL,
                holy_socket1_level = NULL,
                holy_socket2_effect_id = NULL,
                holy_socket2_level = NULL,
                holy_socket3_effect_id = NULL,
                holy_socket3_level = NULL,
                holy_socket4_effect_id = NULL,
                holy_socket4_level = NULL,
                holy_socket5_effect_id = NULL,
                holy_socket5_level = NULL,
                holy_socket6_effect_id = NULL,
                holy_socket6_level = NULL,
                updated_at = now();
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", checked((short)slot));
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("stack", stack);
        command.Parameters.AddWithValue("bound", bound);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"PostgreSQL Gear Mentor material {itemId} staged");
    }

    private static async Task<string> ReadItemRowAsync(
        string connectionString,
        int characterId,
        int slot)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (to_jsonb(items) - 'id' - 'user_id')::text
            FROM character_items AS items
            WHERE items.user_id = @characterId
              AND items.item_location = 1
              AND items.slot_index = @slotIndex;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", checked((short)slot));
        return (string)(await command.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException(
                            $"PostgreSQL item row was missing from bag slot {slot}."));
    }
}
