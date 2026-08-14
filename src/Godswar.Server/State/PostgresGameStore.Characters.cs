using Godswar.Server.Game;
using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(int accountId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand($"""
            SELECT {CharacterColumns}
            FROM character_base cb
            {PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias}
            WHERE cb.account_id = @accountId
              AND cb.lifecycle_state = 'active'
            ORDER BY cb.id;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        AddItemContentRevisionParameter(command);

        var characters = new List<GameCharacter>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            characters.Add(ReadCharacter(reader));
        }

        return characters;
    }

    public async Task<GameCharacter?> GetFirstCharacterAsync(int accountId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand($"""
            SELECT {CharacterColumns}
            FROM character_base cb
            {PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias}
            WHERE cb.account_id = @accountId
              AND cb.lifecycle_state = 'active'
            ORDER BY cb.id
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        AddItemContentRevisionParameter(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    public async Task<CharacterStats?> GetCharacterStatsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            PostgresCharacterRuntimeItemProjectionSql
                .CalculatedStatsForCharacter);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        AddItemContentRevisionParameter(command);
        AddGameplayContentRevisionParameter(command);
        AddPetLearnedSkillRevisionParameter(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? CharacterStatsReader.Read(reader)
            : null;
    }

    public async Task<GameCharacter> CreateCharacterAsync(int accountId, GameCharacter character, CancellationToken cancellationToken = default)
    {
        var baseName = CleanCharacterName(character.Name);

        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidateName = attempt == 0 ? baseName : $"{baseName}{attempt + 1}";
            character.Name = candidateName.Length <= 32 ? candidateName : candidateName[..32];
            character.AccountId = accountId;
            GameDefaults.InitializeStartingLocation(character);
            character.Equipment = GameDefaults.DefaultEquipment(character.Profession);
            character.CreatedUtc = DateTime.UtcNow;

            try
            {
                return await InsertCharacterAsync(character, cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
            }
        }

        character.Name = $"{baseName}{Guid.NewGuid():N}"[..32];
        character.Equipment = GameDefaults.DefaultEquipment(character.Profession);
        return await InsertCharacterAsync(character, cancellationToken);
    }

}
