using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetEggHatchPersistenceChecks
{
    private static async Task CheckEggTemplatesAsync(
        string connectionString)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                count(*),
                max(
                    CASE WHEN id = 10187
                        THEN (stats ->> 'Values')::integer
                    END
                ),
                max(
                    CASE WHEN id = 10187
                        THEN (stats ->> 'ClientDeclaredValues')::integer
                    END
                )
            FROM item_templates
            WHERE id BETWEEN 10150 AND 10193;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "pet egg template aggregate exists");
        Check.Equal(
            44L,
            reader.GetInt64(0),
            "all client pet eggs are authoritative item templates");
        Check.Equal(
            ExpectedSpeciesType,
            reader.GetInt32(1),
            "Thunder Pixie egg authoritative species metadata");
        Check.Equal(
            36,
            reader.GetInt32(2),
            "Thunder Pixie stock mismatch remains documented");
    }

    private static async Task InsertEggAsync(
        string connectionString,
        int characterId,
        short stack,
        short bound)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO character_items (
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
                @slot,
                @itemId,
                @rarity,
                1,
                @bound,
                @stack,
                0,
                0
            );
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", (short)EggSlot);
        command.Parameters.AddWithValue("itemId", EggItemId);
        command.Parameters.AddWithValue(
            "rarity",
            (short)EggAptitude);
        command.Parameters.AddWithValue("bound", bound);
        command.Parameters.AddWithValue("stack", stack);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "pet egg fixture inserts exactly once");
    }

    private static async Task UpdateEggRarityAsync(
        string connectionString,
        int characterId,
        short rarity)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE character_items
            SET item_quality = @rarity,
                updated_at = now()
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @slot
              AND prop_id = @itemId;
            """,
            connection);
        command.Parameters.AddWithValue("rarity", rarity);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", (short)EggSlot);
        command.Parameters.AddWithValue("itemId", EggItemId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "egg rarity fixture updates exactly");
    }

    private static async Task<int> ReadEggStackAsync(
        string connectionString,
        int characterId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT stack
            FROM character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @slot
              AND prop_id = @itemId;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", (short)EggSlot);
        command.Parameters.AddWithValue("itemId", EggItemId);
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null || scalar is DBNull
            ? 0
            : Convert.ToInt32(scalar);
    }

    private static async Task UpdateEggStackAsync(
        string connectionString,
        int characterId,
        short stack)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE character_items
            SET stack = @stack,
                updated_at = now()
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @slot
              AND prop_id = @itemId;
            """,
            connection);
        command.Parameters.AddWithValue("stack", stack);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", (short)EggSlot);
        command.Parameters.AddWithValue("itemId", EggItemId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "egg stack fixture updates exactly");
    }

    private static async Task InsertCapacityPetsAsync(
        string connectionString,
        int characterId,
        int count)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO character_pets (
                user_id,
                species_id,
                name,
                sex,
                level,
                experience,
                aptitude,
                rank,
                current_energy,
                maximum_energy,
                amity,
                satiety,
                remaining_lifetime,
                activity_state
            )
            SELECT
                @characterId,
                1,
                concat('Capacity ', ordinal),
                0,
                1,
                0,
                1,
                0,
                100,
                100,
                100,
                100,
                600,
                'owned'
            FROM generate_series(1, @count) AS fixture(ordinal);
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("count", count);
        Check.Equal(
            count,
            await command.ExecuteNonQueryAsync(),
            "capacity pet fixtures insert exactly");
    }
}
