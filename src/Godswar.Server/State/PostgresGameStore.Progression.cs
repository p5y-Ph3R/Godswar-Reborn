using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task SaveCharacterPositionAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE character_base
            SET "Map" = @currentMap,
                "Pos_X" = @positionX,
                "Pos_Z" = @positionZ
            WHERE id = @characterId
              AND account_id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("currentMap", (short)currentMap);
        command.Parameters.AddWithValue("positionX", positionX);
        command.Parameters.AddWithValue("positionZ", positionZ);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCharacterVitalsAsync(
        int accountId,
        int characterId,
        int currentHp,
        int currentMp,
        long vitalsRevision,
        CancellationToken cancellationToken = default)
    {
        var persistenceLock = _vitalsPersistenceLocks.GetOrAdd(
            characterId,
            static _ => new SemaphoreSlim(1, 1));
        await persistenceLock.WaitAsync(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand("""
                UPDATE character_base
                SET "curHP" = GREATEST(0, @currentHp),
                    "curMP" = GREATEST(0, @currentMp),
                    vitals_revision = @vitalsRevision
                WHERE id = @characterId
                  AND account_id = @accountId
                  AND vitals_revision < @vitalsRevision;
                """);
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("currentHp", currentHp);
            command.Parameters.AddWithValue("currentMp", currentMp);
            command.Parameters.AddWithValue("vitalsRevision", vitalsRevision);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            persistenceLock.Release();
        }
    }

    public async Task<CharacterProgressionResult?> ApplyMonsterKillRewardAsync(
        int accountId,
        int characterId,
        int experience,
        int talentExperience,
        CancellationToken cancellationToken = default)
    {
        experience = Math.Max(0, experience);
        talentExperience = Math.Max(0, talentExperience);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int previousLevel;
        int previousExperience;
        int previousTalentExperience;
        int previousTalentPoints;
        await using (var command = new NpgsqlCommand("""
            SELECT fighter_job_lv, fighter_job_exp, "SkillExp", "SkillPoint"
            FROM character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            previousLevel = reader.GetInt32(0);
            previousExperience = reader.GetInt32(1);
            previousTalentExperience = reader.GetInt32(2);
            previousTalentPoints = reader.GetInt32(3);
        }

        var fighterProgression = PlayerExperienceCatalog.Apply(
            previousLevel,
            previousExperience,
            experience);
        var accumulatedTalentExperience = checked(previousTalentExperience + talentExperience);
        var talentPointsGained = accumulatedTalentExperience / 100;
        var currentTalentExperience = accumulatedTalentExperience % 100;
        var currentTalentPoints = checked(previousTalentPoints + talentPointsGained);

        await using (var command = new NpgsqlCommand("""
            UPDATE character_base
            SET fighter_job_lv = @level,
                fighter_job_exp = @experience,
                "SkillPoint" = @talentPoints,
                "SkillExp" = @talentExperience
            WHERE id = @characterId
              AND account_id = @accountId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("level", fighterProgression.Level);
            command.Parameters.AddWithValue("experience", fighterProgression.Experience);
            command.Parameters.AddWithValue("talentPoints", currentTalentPoints);
            command.Parameters.AddWithValue("talentExperience", currentTalentExperience);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new CharacterProgressionResult(
            fighterProgression.ExperienceGained,
            previousLevel,
            fighterProgression.Level,
            fighterProgression.Experience,
            PlayerExperienceCatalog.GetNextLevelExperience(fighterProgression.Level),
            fighterProgression.LevelUps,
            talentExperience,
            currentTalentExperience,
            talentPointsGained,
            currentTalentPoints);
    }

    public async Task<ZodiacAccumulationResult?> AddZodiacAccumulationAsync(
        int accountId,
        int characterId,
        int experienceGainX100,
        int talentExperienceGainX100,
        CancellationToken cancellationToken = default)
    {
        experienceGainX100 = Math.Max(0, experienceGainX100);
        talentExperienceGainX100 = Math.Max(0, talentExperienceGainX100);

        await using var command = _dataSource.CreateCommand("""
            UPDATE character_base
            SET zodiac_accumulated_exp_x100 = zodiac_accumulated_exp_x100 + @experienceGainX100,
                zodiac_accumulated_talent_exp_x100 =
                    zodiac_accumulated_talent_exp_x100 + @talentExperienceGainX100
            WHERE id = @characterId
              AND account_id = @accountId
            RETURNING zodiac_accumulated_exp_x100, zodiac_accumulated_talent_exp_x100;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("experienceGainX100", experienceGainX100);
        command.Parameters.AddWithValue("talentExperienceGainX100", talentExperienceGainX100);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ZodiacAccumulationResult(
            experienceGainX100,
            talentExperienceGainX100,
            reader.GetInt32(0),
            reader.GetInt32(1));
    }

    public async Task<ZodiacEnergyAccrualResult?> ApplyZodiacOnlineTimeAsync(
        int accountId,
        int characterId,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil,
        ZodiacEnergyPolicy policy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        GameCharacter character;
        await using (var command = new NpgsqlCommand("""
            SELECT zodiac_level, zodiac_energy, zodiac_energy_remainder_x100,
                   zodiac_online_day, zodiac_online_duration_ticks,
                   zodiac_last_online_at, zodiac_last_compensation_day
            FROM character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            character = new GameCharacter
            {
                ZodiacLevel = (byte)reader.GetInt16(0),
                ZodiacEnergy = reader.GetInt32(1),
                ZodiacEnergyRemainderX100 = reader.GetInt32(2),
                ZodiacOnlineDay = reader.IsDBNull(3)
                    ? null
                    : reader.GetFieldValue<DateOnly>(3),
                ZodiacOnlineDurationTicksToday = reader.GetInt64(4),
                ZodiacLastOnlineAt = reader.IsDBNull(5)
                    ? null
                    : new DateTimeOffset(reader.GetDateTime(5).ToUniversalTime()),
                ZodiacLastCompensationDay = reader.IsDBNull(6)
                    ? null
                    : reader.GetFieldValue<DateOnly>(6)
            };
        }

        var result = ZodiacEnergyAccrual.Apply(
            character,
            onlineFrom,
            onlineUntil,
            policy);

        await using (var command = new NpgsqlCommand("""
            UPDATE character_base
            SET zodiac_energy = @energy,
                zodiac_energy_remainder_x100 = @energyRemainderX100,
                zodiac_online_day = @onlineDay,
                zodiac_online_duration_ticks = @onlineDurationTicks,
                zodiac_last_online_at = @lastOnlineAt,
                zodiac_last_compensation_day = @lastCompensationDay
            WHERE id = @characterId
              AND account_id = @accountId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("energy", result.CurrentEnergy);
            command.Parameters.AddWithValue("energyRemainderX100", result.CurrentEnergyRemainderX100);
            command.Parameters.Add(new NpgsqlParameter("onlineDay", NpgsqlDbType.Date)
            {
                Value = result.OnlineDay
            });
            command.Parameters.AddWithValue("onlineDurationTicks", result.OnlineDurationTicksToday);
            command.Parameters.Add(new NpgsqlParameter("lastOnlineAt", NpgsqlDbType.TimestampTz)
            {
                Value = result.LastOnlineAt.UtcDateTime
            });
            command.Parameters.Add(new NpgsqlParameter("lastCompensationDay", NpgsqlDbType.Date)
            {
                Value = result.LastCompensationDay.HasValue
                    ? result.LastCompensationDay.Value
                    : DBNull.Value
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ZodiacLevelUpgradeResult?> UpgradeZodiacLevelAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        GameCharacter? character = null;
        await using (var command = new NpgsqlCommand("""
            SELECT fighter_job_lv, zodiac_level, zodiac_energy,
                   zodiac_energy_remainder_x100
            FROM character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                character = new GameCharacter
                {
                    Level = reader.GetInt32(0),
                    ZodiacLevel = checked((byte)reader.GetInt16(1)),
                    ZodiacEnergy = reader.GetInt32(2),
                    ZodiacEnergyRemainderX100 = reader.GetInt32(3)
                };
            }
        }

        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var result = ZodiacLevelUpgrade.Apply(character);
        if (!result.Committed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        await using (var command = new NpgsqlCommand("""
            UPDATE character_base
            SET zodiac_level = @zodiacLevel,
                zodiac_energy = @zodiacEnergy,
                zodiac_energy_remainder_x100 = @zodiacEnergyRemainderX100
            WHERE id = @characterId
              AND account_id = @accountId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("zodiacLevel", checked((short)result.CurrentLevel));
            command.Parameters.AddWithValue("zodiacEnergy", result.CurrentEnergy);
            command.Parameters.AddWithValue(
                "zodiacEnergyRemainderX100",
                result.CurrentEnergyRemainderX100);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

}
