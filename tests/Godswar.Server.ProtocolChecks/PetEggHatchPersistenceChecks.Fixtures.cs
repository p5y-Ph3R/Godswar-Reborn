using Godswar.Server.Application.Items;
using System.Text.Json;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetEggHatchPersistenceChecks
{
    private static void CheckEggTemplates(
        IItemTemplateCatalog templates)
    {
        var eggs = templates.All
            .Where(static template =>
                template.Id is >= 10150 and <= 10193)
            .ToArray();
        Check.Equal(
            44,
            eggs.Length,
            "all client pet eggs are authoritative item templates");
        var thunderPixie = eggs.Single(static template =>
            template.Id == EggItemId);
        using var stats = JsonDocument.Parse(thunderPixie.StatsJson);
        var values = stats.RootElement.GetProperty("Values");
        var clientDeclaredValues = stats.RootElement.GetProperty(
            "ClientDeclaredValues");
        Check.Equal(
            ExpectedSpeciesType,
            int.Parse(
                values.GetString()!,
                System.Globalization.CultureInfo.InvariantCulture),
            "Thunder Pixie egg authoritative species metadata");
        Check.Equal(
            36,
            int.Parse(
                clientDeclaredValues.GetString()!,
                System.Globalization.CultureInfo.InvariantCulture),
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
                activity_state,
                birth_rank,
                hatch_rank_roll,
                hatch_rank_outcome_order,
                hatch_rank_content_revision
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
                'owned',
                evidence.rank,
                0,
                0,
                evidence.revision
            FROM generate_series(1, @count) AS fixture(ordinal)
            CROSS JOIN (
                SELECT publication.revision, step.rank
                FROM public.pet_content_publication publication
                JOIN public.pet_content_hatch_rank_steps step
                  ON step.revision = publication.revision
                 AND step.aptitude = 1
                 AND step.outcome_order = 0
                WHERE publication.family = 'pets'
            ) evidence;
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
