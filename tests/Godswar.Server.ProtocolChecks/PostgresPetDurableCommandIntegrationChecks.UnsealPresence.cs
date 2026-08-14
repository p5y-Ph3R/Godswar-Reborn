using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task<UnsealPresenceState>
        PrepareDisplacedUnsealPetAsync(
            NpgsqlDataSource dataSource,
            int characterId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var remove = new NpgsqlCommand(
            """
            DELETE FROM public.character_pets
            WHERE user_id = @characterId
              AND name = 'Utility Shed Full 2';
            """,
            connection,
            transaction))
        {
            remove.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(
                1,
                await remove.ExecuteNonQueryAsync(),
                "Unseal presence fixture frees one full-shed cell");
        }

        UnsealPresenceState selected;
        await using (var select = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET is_carried = true,
                is_summoned = true,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE user_id = @characterId
              AND name = 'Utility Shed Full 1'
              AND activity_state = 'owned'
              AND NOT is_carried
              AND NOT is_summoned
              AND NOT contributes_to_character
            RETURNING id, is_carried, is_summoned, revision;
            """,
            connection,
            transaction))
        {
            select.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await select.ExecuteReaderAsync();
            selected = await reader.ReadAsync()
                ? new UnsealPresenceState(
                    reader.GetInt64(0),
                    reader.GetBoolean(1),
                    reader.GetBoolean(2),
                    reader.GetInt64(3))
                : throw new InvalidDataException(
                    "The remaining full-shed pet could not be selected.");
        }
        await transaction.CommitAsync();
        return selected;
    }

    private static async Task<UnsealPresenceState>
        ReadUnsealPresenceAsync(
            NpgsqlDataSource dataSource,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, is_carried, is_summoned, revision
            FROM public.character_pets
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new UnsealPresenceState(
                reader.GetInt64(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetInt64(3))
            : throw new InvalidDataException(
                "The Unseal presence fixture pet is missing.");
    }

    private sealed record UnsealPresenceState(
        long PetId,
        bool IsCarried,
        bool IsSummoned,
        long Revision);
}
