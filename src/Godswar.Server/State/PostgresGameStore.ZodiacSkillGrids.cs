using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<ZodiacSkillGridActivationResult?>
        ActivateZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        var character = await ReadZodiacSkillGridCharacterForUpdateAsync(
            connection,
            transaction,
            accountId,
            characterId,
            gridIndex,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var result = ZodiacSkillGridActivation.Apply(
            character,
            gridIndex);
        if (!result.Committed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        await using (var command = new NpgsqlCommand("""
            UPDATE character_base
            SET "Stone" = @gold
            WHERE id = @characterId
              AND account_id = @accountId;

            INSERT INTO character_zodiac_skill_grids (
                user_id, grid_index, level, selected_skill_id, updated_at
            )
            VALUES (
                @characterId, @gridIndex, @level, @selectedSkillId, now()
            )
            ON CONFLICT (user_id, grid_index) DO UPDATE
            SET level = EXCLUDED.level,
                selected_skill_id = EXCLUDED.selected_skill_id,
                updated_at = EXCLUDED.updated_at;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("gridIndex", checked((short)gridIndex));
            command.Parameters.AddWithValue("level", checked((short)result.CurrentLevel));
            command.Parameters.AddWithValue(
                "selectedSkillId",
                result.SelectedSkillId);
            command.Parameters.AddWithValue("gold", result.CurrentGold);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ZodiacSkillGridUpgradeResult?>
        UpgradeZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        var character =
            await ReadZodiacSkillGridUpgradeCharacterForUpdateAsync(
                connection,
                transaction,
                accountId,
                characterId,
                gridIndex,
                cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var result = ZodiacSkillGridUpgrade.Apply(
            character,
            gridIndex);
        if (!result.Committed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        await using (var command = new NpgsqlCommand("""
            UPDATE character_base
            SET zodiac_energy = @energy,
                zodiac_energy_remainder_x100 = @energyRemainderX100,
                "SkillPoint" = @talentPoints
            WHERE id = @characterId
              AND account_id = @accountId;

            INSERT INTO character_zodiac_skill_grids (
                user_id, grid_index, level, selected_skill_id, updated_at
            )
            VALUES (
                @characterId, @gridIndex, @level, @selectedSkillId, now()
            )
            ON CONFLICT (user_id, grid_index) DO UPDATE
            SET level = EXCLUDED.level,
                selected_skill_id = EXCLUDED.selected_skill_id,
                updated_at = EXCLUDED.updated_at;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue(
                "gridIndex",
                checked((short)gridIndex));
            command.Parameters.AddWithValue(
                "level",
                checked((short)result.CurrentLevel));
            command.Parameters.AddWithValue(
                "selectedSkillId",
                result.SelectedSkillId);
            command.Parameters.AddWithValue(
                "energy",
                result.CurrentEnergy);
            command.Parameters.AddWithValue(
                "energyRemainderX100",
                result.CurrentEnergyRemainderX100);
            command.Parameters.AddWithValue(
                "talentPoints",
                result.CurrentTalentPoints);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ZodiacSkillGridSelectionResult?>
        SelectZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            int selectedSkillKind,
            CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var character = await ReadZodiacSkillSelectionCharacterAsync(
            connection,
            transaction,
            accountId,
            characterId,
            gridIndex,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var learned =
            !ZodiacSkillGridSelectionCatalog.RequiresLearnedSkill(
                gridIndex,
                selectedSkillKind) ||
            ZodiacSkillGridSelectionCatalog.IsAllowedForClass(
                character.Profession,
                selectedSkillKind) &&
            await IsZodiacSkillFamilyLearnedAsync(
                connection,
                transaction,
                characterId,
                selectedSkillKind,
                cancellationToken);
        var result = ZodiacSkillGridSelection.Apply(
            character,
            gridIndex,
            selectedSkillKind,
            learned);
        if (!result.Committed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        await using var command = new NpgsqlCommand("""
            UPDATE character_zodiac_skill_grids
            SET selected_skill_id = @selectedSkillKind,
                updated_at = now()
            WHERE user_id = @characterId
              AND grid_index = @gridIndex
              AND level = @currentLevel
              AND selected_skill_id = @previousSkillKind;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "gridIndex",
            checked((short)gridIndex));
        command.Parameters.AddWithValue(
            "currentLevel",
            checked((short)result.CurrentLevel));
        command.Parameters.AddWithValue(
            "previousSkillKind",
            result.PreviousSkillKind);
        command.Parameters.AddWithValue(
            "selectedSkillKind",
            result.SelectedSkillKind);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Zodiac skill selection did not update exactly once.");
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<GameCharacter?>
        ReadZodiacSkillGridCharacterForUpdateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT "Stone"
            FROM character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null)
        {
            return null;
        }

        var character = new GameCharacter
        {
            Gold = Convert.ToInt32(scalar)
        };
        if (!ZodiacSkillGridCatalog.IsValidGrid(gridIndex))
        {
            return character;
        }

        await ReadZodiacSkillGridStateAsync(
            connection,
            transaction,
            character,
            characterId,
            gridIndex,
            cancellationToken);
        return character;
    }

    private static async Task<GameCharacter?>
        ReadZodiacSkillGridUpgradeCharacterForUpdateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken)
    {
        GameCharacter? character = null;
        await using (var command = new NpgsqlCommand("""
            SELECT zodiac_level, zodiac_energy,
                   zodiac_energy_remainder_x100, "SkillPoint"
            FROM character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                character = new GameCharacter
                {
                    ZodiacLevel = checked((byte)reader.GetInt16(0)),
                    ZodiacEnergy = reader.GetInt32(1),
                    ZodiacEnergyRemainderX100 = reader.GetInt32(2),
                    TalentPoints = reader.GetInt32(3)
                };
            }
        }

        if (character is null ||
            !ZodiacSkillGridCatalog.IsValidGrid(gridIndex))
        {
            return character;
        }

        await ReadZodiacSkillGridStateAsync(
            connection,
            transaction,
            character,
            characterId,
            gridIndex,
            cancellationToken);
        return character;
    }

    private static async Task ReadZodiacSkillGridStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GameCharacter character,
        int characterId,
        int gridIndex,
        CancellationToken cancellationToken)
    {
        await using var gridCommand = new NpgsqlCommand("""
            SELECT level, selected_skill_id
            FROM character_zodiac_skill_grids
            WHERE user_id = @characterId
              AND grid_index = @gridIndex;
            """, connection, transaction);
        gridCommand.Parameters.AddWithValue("characterId", characterId);
        gridCommand.Parameters.AddWithValue(
            "gridIndex",
            checked((short)gridIndex));
        await using var reader =
            await gridCommand.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            character.ZodiacSkillGridLevels[gridIndex] =
                reader.GetInt16(0);
            character.ZodiacSkillGridSkillIds[gridIndex] =
                reader.GetInt32(1);
        }
    }

    private static async Task<GameCharacter?>
        ReadZodiacSkillSelectionCharacterAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken)
    {
        byte? profession = null;
        await using (var command = new NpgsqlCommand("""
            SELECT profession
            FROM character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is not null)
            {
                profession = checked((byte)Convert.ToInt16(scalar));
            }
        }

        if (profession is null)
        {
            return null;
        }

        var character = new GameCharacter
        {
            Profession = profession.Value
        };
        if (!ZodiacSkillGridCatalog.IsValidGrid(gridIndex))
        {
            return character;
        }

        var rowStart =
            ZodiacSkillGridSelectionCatalog.RowStart(gridIndex);
        await using var gridCommand = new NpgsqlCommand("""
            SELECT grid_index, level, selected_skill_id
            FROM character_zodiac_skill_grids
            WHERE user_id = @characterId
              AND grid_index >= @rowStart
              AND grid_index < @rowEnd;
            """, connection, transaction);
        gridCommand.Parameters.AddWithValue("characterId", characterId);
        gridCommand.Parameters.AddWithValue(
            "rowStart",
            checked((short)rowStart));
        gridCommand.Parameters.AddWithValue(
            "rowEnd",
            checked((short)(
                rowStart +
                ZodiacSkillGridSelectionCatalog.GridsPerRow)));
        await using var reader =
            await gridCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var candidate = reader.GetInt16(0);
            character.ZodiacSkillGridLevels[candidate] =
                reader.GetInt16(1);
            character.ZodiacSkillGridSkillIds[candidate] =
                reader.GetInt32(2);
        }

        return character;
    }

    private static async Task<bool> IsZodiacSkillFamilyLearnedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int selectedSkillKind,
        CancellationToken cancellationToken)
    {
        var first =
            ZodiacSkillGridSelectionCatalog.SkillFamilyFirstRuntimeId(
                selectedSkillKind);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM character_skills
                WHERE user_id = @characterId
                  AND skill_id >= @firstSkillId
                  AND skill_id <= @lastSkillId
            );
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("firstSkillId", first);
        command.Parameters.AddWithValue("lastSkillId", first + 4);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync(cancellationToken));
    }
}
