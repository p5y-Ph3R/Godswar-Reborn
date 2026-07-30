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
            LEFT JOIN character_item_loadout ck ON ck.user_id = cb.id
            WHERE cb.account_id = @accountId
              AND cb.lifecycle_state = 'active'
            ORDER BY cb.id;
            """);
        command.Parameters.AddWithValue("accountId", accountId);

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
            LEFT JOIN character_item_loadout ck ON ck.user_id = cb.id
            WHERE cb.account_id = @accountId
              AND cb.lifecycle_state = 'active'
            ORDER BY cb.id
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("accountId", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    public async Task<CharacterStats?> GetCharacterStatsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT
                user_id,
                account_id,
                name,
                profession,
                level,
                max_hp,
                max_mp,
                current_hp,
                current_mp,
                physical_attack,
                physical_defense,
                magic_attack,
                magic_defense,
                hit,
                dodge,
                critical,
                critical_resistance,
                damage_absorb,
                physical_damage_bonus,
                magic_damage_bonus,
                cure_bonus,
                be_cure_bonus,
                hp_recovery,
                mp_recovery,
                ignore_physical_defense,
                ignore_magic_defense,
                physical_append_damage,
                magic_append_damage,
                critical_damage_percent,
                critical_damage_flat,
                weapon_score,
                weapon_rank,
                weapon_aura_effect,
                armor_score,
                armor_rank,
                armor_aura_effect,
                learned_skill_count
            FROM character_stat_summary summary
            WHERE summary.account_id = @accountId
              AND summary.user_id = @characterId
              AND EXISTS (
                  SELECT 1
                  FROM character_base lifecycle
                  WHERE lifecycle.id = summary.user_id
                    AND lifecycle.account_id =
                        summary.account_id
                    AND lifecycle.lifecycle_state = 'active'
              );
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacterStats(reader) : null;
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
