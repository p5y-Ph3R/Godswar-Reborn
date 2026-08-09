using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterLifecycleCommandIntegrationChecks
{
    private static async Task AssertStarterFashionSlotAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                count(*) FILTER (
                    WHERE item_location = 0
                      AND slot_index = @stylishSlot
                      AND prop_id = 8040
                ),
                count(*) FILTER (
                    WHERE item_location = 0
                      AND slot_index = 13
                )
            FROM public.character_items
            WHERE user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "stylishSlot",
            EquipmentSlots.Stylish);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "starter fashion slot query returns one aggregate");
        Check.Equal(
            1L,
            reader.GetInt64(0),
            "PostgreSQL starter robe 8040 uses native slot 12");
        Check.Equal(
            0L,
            reader.GetInt64(1),
            "PostgreSQL starter equipment leaves reserved slot 13 empty");
    }
}
