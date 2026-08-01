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
            {PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias}
            WHERE cb.id = @id
              AND cb.lifecycle_state = 'active';
            """);
        command.Parameters.AddWithValue("id", id);
        AddItemContentRevisionParameter(command);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCharacter(reader)
            : null;
    }

    private async Task<GameCharacter?> GetCharacterByIdAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        int id,
        CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand($"""
            SELECT {CharacterColumns}
            FROM character_base cb
            {PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias}
            WHERE cb.id = @id
              AND cb.lifecycle_state = 'active';
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        AddItemContentRevisionParameter(command);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCharacter(reader)
            : null;
    }
}
