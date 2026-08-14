using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCharacterSnapshotReaderIntegrationChecks
{
    private static async Task<SnapshotFixture> CreateAccountFixtureAsync(
        PostgresGameStore store,
        string username)
    {
        var account = await store.LoginOrCreateAccountAsync(
            username,
            string.Empty);
        return new SnapshotFixture(
            account.Id,
            username,
            [],
            null,
            null);
    }

    private static async Task<GameCharacter> CreateCharacterAsync(
        PostgresGameStore store,
        int accountId,
        string name) =>
        await store.CreateCharacterAsync(
            accountId,
            new GameCharacter
            {
                Name = name,
                Gender = 1,
                Camp = GameDefaults.SpartaCamp,
                Profession = 0,
                Hair = 2,
                Face = 1,
                Faith = 2,
                Level = 37,
                Experience = 4_000_000_000L,
                Silver = 654_321,
                Gold = 1_234,
                MaxHp = 2_400,
                MaxMp = 510,
                CurrentHp = 2_123,
                CurrentMp = 456,
                TalentPoints = 111,
                TalentExperience = 2_222,
                HolySuitPoints = 40,
                ZodiacType = 3,
                ZodiacLevel = 4,
                ZodiacEnergy = 5_555,
                ZodiacEnergyRemainderX100 = 17,
                ZodiacOnlineDay = DateOnly.FromDateTime(DateTime.UtcNow),
                ZodiacOnlineDurationTicksToday = 987_000,
                ZodiacAccumulatedExperienceX100 = 7_654,
                ZodiacAccumulatedTalentExperienceX100 = 3_210
            });

    private static async Task<SnapshotFixture> CreateRichFixtureAsync(
        PostgresGameStore store,
        NpgsqlDataSource dataSource,
        string username,
        string characterName)
    {
        var accountFixture =
            await CreateAccountFixtureAsync(store, username);
        var character = await CreateCharacterAsync(
            store,
            accountFixture.AccountId,
            characterName);

        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        var talentId = await UpsertTalentAsync(
            connection,
            transaction,
            character.Id,
            rank: 3);
        var petId = await InsertPetAsync(
            connection,
            transaction,
            character.Id,
            $"Pet{characterName}"[..Math.Min(
                32,
                $"Pet{characterName}".Length)]);
        await UpsertPersonalBoostAsync(
            connection,
            transaction,
            character.Id,
            bonusBasisPoints: 1_000);
        await using (var shed = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET pet_shed_capacity = 3,
                pet_shed_revision = 4
            WHERE id = @characterId;
            """,
            connection,
            transaction))
        {
            shed.Parameters.AddWithValue("characterId", character.Id);
            Check.Equal(
                1,
                await shed.ExecuteNonQueryAsync(),
                "rich fixture persists pet-shed capacity independently");
        }
        await transaction.CommitAsync();

        return accountFixture with
        {
            CharacterIds = [character.Id],
            TalentId = talentId,
            PetId = petId
        };
    }

    private static async Task SealFighterLevelAsync(
        NpgsqlDataSource dataSource,
        SnapshotFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET fighter_job_lv = 89,
                fighter_level_sealed = true
            WHERE id = @characterId
              AND account_id = @accountId;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "accountId",
            fixture.AccountId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "parity snapshot fixture seals one level-89 fighter");
    }

    private static async Task<int> UpsertTalentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int rank)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH selected AS (
                SELECT template.id
                FROM public.character_base character
                INNER JOIN public.talent_templates template
                    ON template.class_id = character.profession
                WHERE character.id = @characterId
                ORDER BY template.tree_order, template.id
                LIMIT 1
            ),
            written AS (
                INSERT INTO public.character_talents (
                    user_id,
                    talent_id,
                    rank,
                    updated_at
                )
                SELECT @characterId, selected.id, @rank, now()
                FROM selected
                ON CONFLICT (user_id, talent_id) DO UPDATE
                SET rank = EXCLUDED.rank,
                    updated_at = EXCLUDED.updated_at
                RETURNING talent_id
            )
            SELECT talent_id
            FROM written;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("rank", (short)rank);
        return (int)(await command.ExecuteScalarAsync()
                     ?? throw new InvalidOperationException(
                         "Character snapshot fixture has no class talent."));
    }

    private static async Task<long> InsertPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        string name)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_pets (
                user_id,
                species_id,
                name,
                sex,
                level,
                experience,
                aptitude,
                rank,
                talent_mask,
                has_owner_merge_talent,
                opened_skill_slots,
                available_skill_slots,
                current_energy,
                maximum_energy,
                amity,
                satiety,
                remaining_lifetime,
                growth_revealed,
                bound,
                activity_state,
                revision,
                initial_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version,
                birth_rank,
                hatch_rank_roll,
                hatch_rank_outcome_order,
                hatch_rank_content_revision
            )
            VALUES (
                @characterId,
                1,
                @name,
                1,
                9,
                100,
                15,
                33.5,
                31,
                true,
                4,
                8,
                80,
                100,
                77,
                66,
                555,
                true,
                true,
                'owned',
                5,
                4203,
                @initialSavvyPolicy,
                4203,
                @initialSavvyPolicy,
                @initialSavvySource,
                (SELECT step.rank
                 FROM public.pet_content_publication publication
                 JOIN public.pet_content_hatch_rank_steps step
                   ON step.revision = publication.revision
                  AND step.aptitude = 15
                  AND step.outcome_order = 0
                 WHERE publication.family = 'pets'),
                0,
                0,
                (SELECT revision
                 FROM public.pet_content_publication
                 WHERE family = 'pets')
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue(
            "initialSavvyPolicy",
            PetInitialSavvyPolicy.Version);
        command.Parameters.AddWithValue(
            "initialSavvySource",
            PetSavvyRuntimeSemantics.SourceVersion);
        var petId =
            (long)(await command.ExecuteScalarAsync()
                   ?? throw new InvalidOperationException(
                       "Character snapshot pet fixture returned no ID."));

        await InsertPetChildrenAsync(
            connection,
            transaction,
            petId);
        return petId;
    }

    private static async Task InsertPetChildrenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_pet_stat_values (
                pet_id,
                stat_code,
                initial_savvy,
                added_savvy,
                base_growth_rate,
                birth_initial_savvy,
                rarity_added_savvy,
                growth_acceleration,
                revision
            )
            SELECT
                @petId,
                stat_code,
                700 + stat_code,
                (3 + 2 * stat_code) * 9,
                2 + stat_code,
                697 + stat_code,
                697 + stat_code,
                1 + stat_code,
                30 + stat_code
            FROM generate_series(1, 6) AS stat(stat_code);

            INSERT INTO public.character_pet_character_bonuses (
                pet_id,
                effect_code,
                effect_value,
                revision
            )
            VALUES (@petId, 0, 12.5, 7);

            INSERT INTO public.character_pet_skills (
                pet_id,
                skill_id,
                slot_index,
                skill_rank,
                skill_experience,
                is_active,
                revision
            )
            VALUES (@petId, 910001, 0, 2, 33, true, 11);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            8,
            await command.ExecuteNonQueryAsync(),
            "rich snapshot fixture inserts every pet child row");
    }

    private static async Task UpsertPersonalBoostAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bonusBasisPoints)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_experience_modifiers (
                character_id,
                status_id,
                kind,
                bonus_basis_points,
                priority,
                source,
                activated_at,
                expires_at,
                remaining_online_ticks
            )
            VALUES (
                @characterId,
                1501,
                1001,
                @bonusBasisPoints,
                2,
                'b06-snapshot-fixture',
                now() - interval '1 minute',
                now() + interval '1 hour',
                36000000000
            )
            ON CONFLICT (character_id, kind) DO UPDATE
            SET bonus_basis_points = EXCLUDED.bonus_basis_points,
                remaining_online_ticks =
                    EXCLUDED.remaining_online_ticks;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "bonusBasisPoints",
            bonusBasisPoints);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "rich snapshot fixture writes one personal boost");
    }

    private static async Task<long> CountCharactersAsync(
        NpgsqlDataSource dataSource,
        int accountId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM public.character_base
            WHERE account_id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Character count returned no value."));
    }

    private static async Task<int> ReadUnusedAccountIdAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT (COALESCE(max(id), 0)::bigint + 100000)::bigint
            FROM public.accounts;
            """);
        var value = (long)(await command.ExecuteScalarAsync()
                           ?? throw new InvalidOperationException(
                               "Unused account ID query returned no value."));
        return checked((int)value);
    }

    private static async Task DeleteFixtureAsync(
        NpgsqlDataSource dataSource,
        SnapshotFixture fixture)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var verify = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = ANY(@characterIds);
            """,
            connection,
            transaction))
        {
            verify.Parameters.AddWithValue(
                "accountId",
                fixture.AccountId);
            verify.Parameters.AddWithValue(
                "characterIds",
                fixture.CharacterIds);
            Check.Equal(
                (long)fixture.CharacterIds.Length,
                (long)(await verify.ExecuteScalarAsync()
                       ?? throw new InvalidOperationException(
                           "Fixture ownership check returned no count.")),
                "snapshot fixture owns every tracked character");
        }

        await using (var delete = new NpgsqlCommand(
            """
            DELETE FROM public.accounts
            WHERE id = @accountId
              AND username = @username;
            """,
            connection,
            transaction))
        {
            delete.Parameters.AddWithValue(
                "accountId",
                fixture.AccountId);
            delete.Parameters.AddWithValue(
                "username",
                fixture.Username);
            Check.Equal(
                1,
                await delete.ExecuteNonQueryAsync(),
                "snapshot fixture cleanup deletes one exact account");
        }

        await transaction.CommitAsync();
    }

    private sealed record SnapshotFixture(
        int AccountId,
        string Username,
        int[] CharacterIds,
        int? TalentId,
        long? PetId)
    {
        public int CharacterId =>
            CharacterIds.Length == 1
                ? CharacterIds[0]
                : throw new InvalidOperationException(
                    "Fixture does not own exactly one character.");
    }
}
