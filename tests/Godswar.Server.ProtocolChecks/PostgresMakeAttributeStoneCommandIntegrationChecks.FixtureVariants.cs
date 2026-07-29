using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresMakeAttributeStoneCommandIntegrationChecks
{
    private const uint FillerItemId = 4200;

    private static async Task InsertFixtureItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short slot,
        uint itemId,
        short stack,
        short bound)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack,
                item_exp,
                holy_suit_code
            )
            VALUES (
                @characterId,
                1,
                @slotIndex,
                @itemId,
                1,
                1,
                @bound,
                @stack,
                0,
                0
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", slot);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)itemId));
        command.Parameters.AddWithValue("bound", bound);
        command.Parameters.AddWithValue("stack", stack);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"fixture inserts item {itemId} in bag slot {slot}");
    }

    private static async Task FillRemainingKitBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack,
                item_exp,
                holy_suit_code
            )
            SELECT
                @characterId,
                1,
                slot::smallint,
                @itemId,
                1,
                1,
                0,
                1,
                0,
                0
            FROM generate_series(1, 95) AS slot;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)FillerItemId));
        Check.Equal(
            95,
            await command.ExecuteNonQueryAsync(),
            "full-bag fixture inserts all remaining authoritative slots");
    }
}
