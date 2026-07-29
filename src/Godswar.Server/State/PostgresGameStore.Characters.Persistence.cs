using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;
using System.Data.Common;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private async Task<GameCharacter> InsertCharacterAsync(GameCharacter character, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await RequireAvailableCharacterSlotAsync(
            connection,
            transaction,
            character.AccountId,
            cancellationToken);
        var characterId = 0;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO character_base (
                account_id, server_id, name, gender, "GM", camp, profession, fighter_job_lv,
                scholar_job_lv, fighter_job_exp, scholar_job_exp, "curHP", "curMP", status,
                belief, zodiac_type, zodiac_lucky_status, zodiac_lucky_expires_at, zodiac_level,
                zodiac_energy, zodiac_energy_remainder_x100, zodiac_online_day,
                zodiac_online_duration_ticks, zodiac_last_online_at, zodiac_last_compensation_day,
                zodiac_accumulated_exp_x100, zodiac_accumulated_talent_exp_x100,
                prestige, earl_rank, consortia, consortia_job, consortia_contribute,
                store_num, bag_num, hair_style, face_shap, "Map", "Pos_X", "Pos_Z", "Money",
                "Stone", "SkillPoint", "SkillExp", holy_suit_points, "MaxHP", "MaxMP", "Register_time",
                "LastLogin_time", mutetime
            )
            VALUES (
                @accountId, 1, @name, @gender, 0, @camp, @profession, @level,
                0, @experience, 0, @currentHp, @currentMp, 0, @faith, @zodiacType,
                @zodiacLuckyStatus, @zodiacLuckyExpiresAt, @zodiacLevel, @zodiacEnergy,
                @zodiacEnergyRemainderX100, @zodiacOnlineDay, @zodiacOnlineDurationTicks,
                @zodiacLastOnlineAt, @zodiacLastCompensationDay,
                @zodiacAccumulatedExperienceX100, @zodiacAccumulatedTalentExperienceX100,
                0, 0, 0, 0, 0,
                10, 1, @hair, @face, @currentMap, @positionX, @positionZ, @silver,
                @gold, @talentPoints, @talentExperience, @holySuitPoints, @maxHp, @maxMp, @createdUtc, @createdUtc, 0
            )
            RETURNING id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", character.AccountId);
            command.Parameters.AddWithValue("name", character.Name);
            command.Parameters.AddWithValue("gender", character.Gender == 0 ? "female" : "male");
            command.Parameters.AddWithValue("camp", (short)character.Camp);
            command.Parameters.AddWithValue("profession", (short)character.Profession);
            command.Parameters.AddWithValue("level", character.Level);
            command.Parameters.AddWithValue("experience", character.Experience);
            command.Parameters.AddWithValue("silver", Math.Max(0, character.Silver));
            command.Parameters.AddWithValue("gold", Math.Max(0, character.Gold));
            command.Parameters.AddWithValue("currentHp", character.CurrentHp);
            command.Parameters.AddWithValue("currentMp", character.CurrentMp);
            command.Parameters.AddWithValue("faith", (short)character.Faith);
            command.Parameters.AddWithValue("zodiacType", (short)character.ZodiacType);
            command.Parameters.AddWithValue("zodiacLuckyStatus", character.ZodiacLuckyStatus);
            command.Parameters.Add(new NpgsqlParameter("zodiacLuckyExpiresAt", NpgsqlDbType.TimestampTz)
            {
                Value = character.ZodiacLuckyExpiresAt.HasValue
                    ? character.ZodiacLuckyExpiresAt.Value.UtcDateTime
                    : DBNull.Value
            });
            command.Parameters.AddWithValue("zodiacLevel", (short)character.ZodiacLevel);
            command.Parameters.AddWithValue("zodiacEnergy", character.ZodiacEnergy);
            command.Parameters.AddWithValue(
                "zodiacEnergyRemainderX100",
                character.ZodiacEnergyRemainderX100);
            command.Parameters.Add(new NpgsqlParameter("zodiacOnlineDay", NpgsqlDbType.Date)
            {
                Value = character.ZodiacOnlineDay.HasValue
                    ? character.ZodiacOnlineDay.Value
                    : DBNull.Value
            });
            command.Parameters.AddWithValue(
                "zodiacOnlineDurationTicks",
                character.ZodiacOnlineDurationTicksToday);
            command.Parameters.Add(new NpgsqlParameter("zodiacLastOnlineAt", NpgsqlDbType.TimestampTz)
            {
                Value = character.ZodiacLastOnlineAt.HasValue
                    ? character.ZodiacLastOnlineAt.Value.UtcDateTime
                    : DBNull.Value
            });
            command.Parameters.Add(new NpgsqlParameter("zodiacLastCompensationDay", NpgsqlDbType.Date)
            {
                Value = character.ZodiacLastCompensationDay.HasValue
                    ? character.ZodiacLastCompensationDay.Value
                    : DBNull.Value
            });
            command.Parameters.AddWithValue(
                "zodiacAccumulatedExperienceX100",
                character.ZodiacAccumulatedExperienceX100);
            command.Parameters.AddWithValue(
                "zodiacAccumulatedTalentExperienceX100",
                character.ZodiacAccumulatedTalentExperienceX100);
            command.Parameters.AddWithValue("hair", (short)character.Hair);
            command.Parameters.AddWithValue("face", (short)character.Face);
            command.Parameters.AddWithValue("currentMap", (short)character.CurrentMap);
            command.Parameters.AddWithValue("positionX", character.PositionX);
            command.Parameters.AddWithValue("positionZ", character.PositionZ);
            command.Parameters.AddWithValue("talentPoints", character.TalentPoints);
            command.Parameters.AddWithValue("talentExperience", character.TalentExperience);
            command.Parameters.AddWithValue("holySuitPoints", character.HolySuitPoints);
            command.Parameters.AddWithValue("maxHp", character.MaxHp);
            command.Parameters.AddWithValue("maxMp", character.MaxMp);
            command.Parameters.AddWithValue("createdUtc", DateTime.SpecifyKind(character.CreatedUtc, DateTimeKind.Utc));

            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            characterId = scalar is int id ? id : Convert.ToInt32(scalar);
        }

        await ReplaceCharacterItemsFromCompactAsync(
            connection,
            transaction,
            characterId,
            character.Equipment,
            GameDefaults.StarterKitBag,
            cancellationToken);

        await SeedCharacterCreationEconomyBaselineAsync(
            connection,
            transaction,
            characterId,
            character.AccountId,
            cancellationToken);

        await using (var command = new NpgsqlCommand("""
            INSERT INTO character_skills (user_id, skill_id, skill_level, source)
            SELECT
                @characterId,
                st.skill_id,
                st.skill_level,
                'starter'
            FROM skill_templates st
            WHERE @profession = ANY(st.class_ids)
              AND st.previous_skill_id IS NULL
              AND COALESCE(st.min_level, 1) <= @level
              AND st.skill_level = 1
              AND NOT EXISTS (
                  SELECT 1
                  FROM character_skills existing
                  JOIN skill_templates existing_template ON existing_template.skill_id = existing.skill_id
                  WHERE existing.user_id = @characterId
                    AND existing_template.base_name = st.base_name
                    AND COALESCE(existing_template.skill_level, 0) > COALESCE(st.skill_level, 0)
              )
            ON CONFLICT (user_id, skill_id) DO NOTHING;

            INSERT INTO character_skills (user_id, skill_id, skill_level, source)
            SELECT @characterId, st.skill_id, 1, 'mount-compatibility'
            FROM skill_templates st
            WHERE st.skill_id = 4904
              AND @profession = ANY(st.class_ids)
            ON CONFLICT (user_id, skill_id) DO NOTHING;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("profession", (short)character.Profession);
            command.Parameters.AddWithValue("level", character.Level);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await GetCharacterByIdAsync(characterId, cancellationToken)
            ?? throw new InvalidOperationException("Inserted character could not be reloaded.");
    }

    private static async Task RequireAvailableCharacterSlotAsync(
        DbConnection connection,
        DbTransaction transaction,
        int accountId,
        CancellationToken cancellationToken)
    {
        await using (var lockAccount = connection.CreateCommand())
        {
            lockAccount.Transaction = transaction;
            lockAccount.CommandText = """
                SELECT id
                FROM accounts
                WHERE id = @accountId
                FOR UPDATE;
                """;
            var accountIdParameter = lockAccount.CreateParameter();
            accountIdParameter.ParameterName = "accountId";
            accountIdParameter.Value = accountId;
            lockAccount.Parameters.Add(accountIdParameter);
            if (await lockAccount.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new InvalidOperationException(
                    "Character creation requires an existing account.");
            }
        }

        // This is deliberately a second READ COMMITTED statement. A creator
        // that waited on the account-row lock must observe the prior creator's
        // committed character before deciding whether the slot is empty.
        await using var checkSlot = connection.CreateCommand();
        checkSlot.Transaction = transaction;
        checkSlot.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM character_base
                WHERE account_id = @accountId
            );
            """;
        var checkAccountIdParameter = checkSlot.CreateParameter();
        checkAccountIdParameter.ParameterName = "accountId";
        checkAccountIdParameter.Value = accountId;
        checkSlot.Parameters.Add(checkAccountIdParameter);
        if (await checkSlot.ExecuteScalarAsync(cancellationToken) is true)
        {
            throw new CharacterSlotOccupiedException();
        }
    }

    private async Task<GameCharacter?> GetCharacterByIdAsync(int id, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand($"""
            SELECT {CharacterColumns}
            FROM character_base cb
            LEFT JOIN character_item_loadout ck ON ck.user_id = cb.id
            WHERE cb.id = @id;
            """);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    private static async Task<GameCharacter?> GetCharacterByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int id,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {CharacterColumns}
            FROM character_base cb
            LEFT JOIN character_item_loadout ck ON ck.user_id = cb.id
            WHERE cb.id = @id;
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    private static GameAccount ReadAccount(NpgsqlDataReader reader)
    {
        return new GameAccount
        {
            Id = reader.GetInt32(0),
            Username = reader.GetString(1),
            Password = string.Empty,
            CreatedUtc = reader.GetDateTime(3).ToUniversalTime(),
            VipTier = (VipTier)reader.GetInt16(4),
            VipExpiresAt = reader.IsDBNull(5)
                ? null
                : new DateTimeOffset(reader.GetDateTime(5).ToUniversalTime())
        };
    }

    private static GameCharacter ReadCharacter(NpgsqlDataReader reader)
    {
        return new GameCharacter
        {
            Id = reader.GetInt32(0),
            AccountId = reader.GetInt32(1),
            Name = reader.GetString(2),
            Gender = reader.GetString(3) == "female" ? (byte)0 : (byte)1,
            Camp = (byte)reader.GetInt16(4),
            Profession = (byte)reader.GetInt16(5),
            Hair = (byte)reader.GetInt16(6),
            Face = (byte)reader.GetInt16(7),
            Faith = (byte)reader.GetInt16(8),
            CurrentMap = (byte)reader.GetInt16(9),
            Level = reader.GetInt32(10),
            MaxHp = reader.GetInt32(11),
            MaxMp = reader.GetInt32(12),
            CurrentHp = reader.GetInt32(13),
            CurrentMp = reader.GetInt32(14),
            PositionX = reader.GetFloat(15),
            PositionZ = reader.GetFloat(16),
            CreatedUtc = reader.GetDateTime(17).ToUniversalTime(),
            Equipment = reader.GetString(18),
            KitBag = reader.GetString(19),
            TalentPoints = reader.GetInt32(20),
            TalentExperience = reader.GetInt32(21),
            HolySuitPoints = reader.GetInt32(22),
            WeaponRank = reader.GetInt16(23),
            WeaponAuraEffect = reader.GetInt32(24),
            ArmorRank = reader.GetInt16(25),
            ArmorAuraEffect = reader.GetInt32(26),
            Experience = reader.GetInt32(27),
            VitalsRevision = reader.GetInt64(28),
            ZodiacType = (byte)reader.GetInt16(29),
            ZodiacLuckyStatus = reader.GetInt32(30),
            ZodiacLuckyExpiresAt = reader.IsDBNull(31)
                ? null
                : new DateTimeOffset(reader.GetDateTime(31).ToUniversalTime()),
            ZodiacLevel = (byte)reader.GetInt16(32),
            ZodiacEnergy = reader.GetInt32(33),
            ZodiacAccumulatedExperienceX100 = reader.GetInt32(34),
            ZodiacAccumulatedTalentExperienceX100 = reader.GetInt32(35),
            ZodiacEnergyRemainderX100 = reader.GetInt32(36),
            ZodiacOnlineDay = reader.IsDBNull(37)
                ? null
                : reader.GetFieldValue<DateOnly>(37),
            ZodiacOnlineDurationTicksToday = reader.GetInt64(38),
            ZodiacLastOnlineAt = reader.IsDBNull(39)
                ? null
                : new DateTimeOffset(reader.GetDateTime(39).ToUniversalTime()),
            ZodiacLastCompensationDay = reader.IsDBNull(40)
                ? null
                : reader.GetFieldValue<DateOnly>(40),
            Silver = reader.GetInt32(41),
            Gold = reader.GetInt32(42),
            ZodiacSkillGridLevels =
                ZodiacSkillGridActivation.NormalizeLevels(
                    reader.GetFieldValue<int[]>(43)),
            ZodiacSkillGridSkillIds =
                ZodiacSkillGridActivation.NormalizeSkillIds(
                    reader.GetFieldValue<int[]>(44))
        };
    }

    private static CharacterStats ReadCharacterStats(NpgsqlDataReader reader)
    {
        return new CharacterStats
        {
            CharacterId = reader.GetInt32(0),
            AccountId = reader.GetInt32(1),
            Name = reader.GetString(2),
            Level = reader.GetInt32(4),
            MaxHp = reader.GetInt32(5),
            MaxMp = reader.GetInt32(6),
            CurrentHp = reader.GetInt32(7),
            CurrentMp = reader.GetInt32(8),
            PhysicalAttack = reader.GetInt32(9),
            PhysicalDefense = reader.GetInt32(10),
            MagicAttack = reader.GetInt32(11),
            MagicDefense = reader.GetInt32(12),
            Hit = reader.GetInt32(13),
            Dodge = reader.GetInt32(14),
            Critical = reader.GetInt32(15),
            CriticalResistance = reader.GetInt32(16),
            DamageAbsorb = reader.GetInt32(17),
            PhysicalDamageBonus = reader.GetInt32(18),
            MagicDamageBonus = reader.GetInt32(19),
            CureBonus = reader.GetInt32(20),
            BeCureBonus = reader.GetInt32(21),
            HpRecovery = reader.GetInt32(22),
            MpRecovery = reader.GetInt32(23),
            IgnorePhysicalDefense = reader.GetInt32(24),
            IgnoreMagicDefense = reader.GetInt32(25),
            PhysicalAppendDamage = reader.GetInt32(26),
            MagicAppendDamage = reader.GetInt32(27),
            CriticalDamagePercent = reader.GetInt32(28),
            CriticalDamageFlat = reader.GetInt32(29),
            WeaponScore = reader.GetInt32(30),
            WeaponRank = reader.GetInt16(31),
            WeaponAuraEffect = reader.GetInt32(32),
            ArmorScore = reader.GetInt32(33),
            ArmorRank = reader.GetInt16(34),
            ArmorAuraEffect = reader.GetInt32(35),
            LearnedSkillCount = reader.GetInt32(36)
        };
    }

}
