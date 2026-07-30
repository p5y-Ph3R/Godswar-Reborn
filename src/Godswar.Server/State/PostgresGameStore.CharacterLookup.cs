namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private async Task<GameCharacter?>
        GetCharacterByIdAsync(
            int id,
            CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand($"""
            SELECT {CharacterColumns}
            FROM character_base cb
            LEFT JOIN character_item_loadout ck ON ck.user_id = cb.id
            WHERE cb.id = @id
              AND cb.lifecycle_state = 'active';
            """);
        command.Parameters.AddWithValue("id", id);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCharacter(reader)
            : null;
    }

    private static async Task<GameCharacter?>
        GetCharacterByIdAsync(
            Npgsql.NpgsqlConnection connection,
            Npgsql.NpgsqlTransaction transaction,
            int id,
            CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand($"""
            SELECT {CharacterColumns}
            FROM character_base cb
            LEFT JOIN character_item_loadout ck ON ck.user_id = cb.id
            WHERE cb.id = @id
              AND cb.lifecycle_state = 'active';
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCharacter(reader)
            : null;
    }
}
