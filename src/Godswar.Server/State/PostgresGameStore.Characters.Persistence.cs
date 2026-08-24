using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private async Task<GameCharacter> InsertCharacterAsync(GameCharacter character, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var ensureMembership = connection.CreateCommand())
        {
            ensureMembership.Transaction = transaction;
            ensureMembership.CommandText =
                """
                INSERT INTO account_realm (account_id, realm_id)
                SELECT account_row.id, realm.id
                FROM accounts account_row
                CROSS JOIN server realm
                WHERE account_row.id = @accountId
                  AND realm.id = @realmId
                  AND realm.enabled
                ON CONFLICT (account_id, realm_id) DO NOTHING;
                """;
            ensureMembership.Parameters.AddWithValue(
                "accountId",
                character.AccountId);
            ensureMembership.Parameters.AddWithValue(
                "realmId",
                character.RealmId.Value);
            await ensureMembership.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var lockAccount = connection.CreateCommand())
        {
            lockAccount.Transaction = transaction;
            lockAccount.CommandText =
                """
                SELECT membership.character_lifecycle_version
                FROM account_realm membership
                JOIN server realm
                  ON realm.id = membership.realm_id
                 AND realm.enabled
                WHERE membership.account_id = @accountId
                  AND membership.realm_id = @realmId
                FOR UPDATE;
                """;
            lockAccount.Parameters.AddWithValue(
                "accountId",
                character.AccountId);
            lockAccount.Parameters.AddWithValue(
                "realmId",
                character.RealmId.Value);
            if (await lockAccount.ExecuteScalarAsync(
                    cancellationToken) is null)
            {
                throw new InvalidOperationException(
                    "Character creation requires an enabled account realm.");
            }
        }

        await using (var guardStream = connection.CreateCommand())
        {
            guardStream.Transaction = transaction;
            guardStream.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM outbox_consumer_positions
                    WHERE consumer_key = @consumerKey
                      AND aggregate_type = @aggregateType
                      AND aggregate_key = @aggregateKey
                    UNION ALL
                    SELECT 1
                    FROM outbox_events
                    WHERE consumer_key = @consumerKey
                      AND aggregate_type = @aggregateType
                      AND aggregate_key = @aggregateKey
                );
                """;
            guardStream.Parameters.AddWithValue(
                "consumerKey",
                Infrastructure.Characters.CharacterLifecyclePersistenceCodec
                    .ConsumerKeyFor(character.RealmId));
            guardStream.Parameters.AddWithValue(
                "aggregateType",
                Infrastructure.Characters.CharacterLifecyclePersistenceCodec
                    .AggregateTypeFor(character.RealmId));
            guardStream.Parameters.AddWithValue(
                "aggregateKey",
                Infrastructure.Characters.CharacterLifecyclePersistenceCodec
                    .AggregateKey(
                        character.AccountId,
                        character.RealmId,
                        CharacterLifecyclePolicy.SingleCharacterSlot));
            if (await guardStream.ExecuteScalarAsync(
                    cancellationToken) is true)
            {
                throw new
                    CharacterLifecycleDurableStreamActiveException();
            }
        }

        await using (var checkSlot = connection.CreateCommand())
        {
            checkSlot.Transaction = transaction;
            checkSlot.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM character_base
                    WHERE account_id = @accountId
                      AND server_id = @realmId
                      AND character_slot = @characterSlot
                      AND lifecycle_state = 'active'
                );
                """;
            checkSlot.Parameters.AddWithValue(
                "accountId",
                character.AccountId);
            checkSlot.Parameters.AddWithValue(
                "realmId",
                character.RealmId.Value);
            checkSlot.Parameters.AddWithValue(
                "characterSlot",
                CharacterLifecyclePolicy.SingleCharacterSlot);
            if (await checkSlot.ExecuteScalarAsync(
                    cancellationToken) is true)
            {
                throw new CharacterSlotOccupiedException();
            }
        }

        long lifecycleVersion;
        await using (var reserveVersion = connection.CreateCommand())
        {
            reserveVersion.Transaction = transaction;
            reserveVersion.CommandText =
                """
                UPDATE account_realm
                SET character_lifecycle_version =
                        character_lifecycle_version + 1
                WHERE account_id = @accountId
                  AND realm_id = @realmId
                RETURNING character_lifecycle_version;
                """;
            reserveVersion.Parameters.AddWithValue(
                "accountId",
                character.AccountId);
            reserveVersion.Parameters.AddWithValue(
                "realmId",
                character.RealmId.Value);
            var value = await reserveVersion.ExecuteScalarAsync(
                cancellationToken);
            lifecycleVersion = value is long version
                ? version
                : Convert.ToInt64(value);
        }
        if (character.RealmId == RealmId.Tempest)
        {
            await using var mirrorVersion = connection.CreateCommand();
            mirrorVersion.Transaction = transaction;
            mirrorVersion.CommandText =
                """
                UPDATE accounts
                SET character_lifecycle_version = @lifecycleVersion
                WHERE id = @accountId;
                """;
            mirrorVersion.Parameters.AddWithValue(
                "accountId",
                character.AccountId);
            mirrorVersion.Parameters.AddWithValue(
                "lifecycleVersion",
                lifecycleVersion);
            if (await mirrorVersion.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The Tempest lifecycle mirror was not updated.");
            }
        }
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
                store_num, bag_num, warehouse_capacity, warehouse_revision,
                hair_style, face_shap, "Map", "Pos_X", "Pos_Z", "Money",
                "Stone", "SkillPoint", "SkillExp", holy_suit_points, "MaxHP", "MaxMP", "Register_time",
                "LastLogin_time", mutetime, character_slot,
                lifecycle_state, lifecycle_version
            )
            VALUES (
                @accountId, @realmId, @name, @gender, 0, @camp,
                @profession, @level, 0, @experience, 0, @currentHp,
                @currentMp, 0, @faith, @zodiacType, @zodiacLuckyStatus,
                @zodiacLuckyExpiresAt, @zodiacLevel, @zodiacEnergy,
                @zodiacEnergyRemainderX100, @zodiacOnlineDay,
                @zodiacOnlineDurationTicks,
                @zodiacLastOnlineAt, @zodiacLastCompensationDay,
                @zodiacAccumulatedExperienceX100, @zodiacAccumulatedTalentExperienceX100,
                0, 0, 0, 0, 0,
                10, 1, 40, 0, @hair, @face, @currentMap, @positionX, @positionZ, @silver,
                @gold, @talentPoints, @talentExperience, @holySuitPoints, @maxHp, @maxMp, @createdUtc, @createdUtc, 0,
                @characterSlot, 'active', @lifecycleVersion
            )
            RETURNING id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", character.AccountId);
            command.Parameters.AddWithValue(
                "realmId",
                character.RealmId.Value);
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
            command.Parameters.AddWithValue(
                "characterSlot",
                CharacterLifecyclePolicy.SingleCharacterSlot);
            command.Parameters.AddWithValue(
                "lifecycleVersion",
                lifecycleVersion);

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
            FROM gameplay_skill_combat_definitions st
            WHERE @profession = ANY(st.class_ids)
              AND st.revision = COALESCE(
                  @gameplayContentRevision,
                  (
                      SELECT publication.revision
                      FROM gameplay_content_publication publication
                      WHERE publication.family = 'gameplay'
                  )
              )
              AND st.previous_skill_id IS NULL
              AND COALESCE(st.min_level, 1) <= @level
              AND st.skill_level = 1
              AND NOT EXISTS (
                  SELECT 1
                  FROM character_skills existing
                  JOIN gameplay_skill_combat_definitions existing_template
                    ON existing_template.skill_id = existing.skill_id
                   AND existing_template.revision = st.revision
                  WHERE existing.user_id = @characterId
                    AND existing_template.base_name = st.base_name
                    AND COALESCE(existing_template.skill_level, 0) > COALESCE(st.skill_level, 0)
              )
            ON CONFLICT (user_id, skill_id) DO NOTHING;

            INSERT INTO character_skills (user_id, skill_id, skill_level, source)
            SELECT @characterId, st.skill_id, 1, 'mount-compatibility'
            FROM gameplay_skill_combat_definitions st
            WHERE st.skill_id = 4904
              AND st.revision = COALESCE(
                  @gameplayContentRevision,
                  (
                      SELECT publication.revision
                      FROM gameplay_content_publication publication
                      WHERE publication.family = 'gameplay'
                  )
              )
              AND @profession = ANY(st.class_ids)
            ON CONFLICT (user_id, skill_id) DO NOTHING;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("profession", (short)character.Profession);
            command.Parameters.AddWithValue("level", character.Level);
            AddGameplayContentRevisionParameter(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await GetCharacterByIdAsync(characterId, cancellationToken)
            ?? throw new InvalidOperationException("Inserted character could not be reloaded.");
    }

}
