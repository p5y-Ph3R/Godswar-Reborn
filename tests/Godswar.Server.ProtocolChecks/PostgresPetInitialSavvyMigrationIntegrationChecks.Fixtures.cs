using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetInitialSavvyMigrationIntegrationChecks
{
    private static async Task CheckSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        Check.True(
            await ScalarAsync<bool>(
                connection,
                transaction,
                """
                SELECT column_default IS NULL
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'character_pet_stat_values'
                  AND column_name = 'initial_savvy';
                """),
            "migration removes the unsafe implicit initial-savvy default");
    }

    private static async Task<Fixture> InsertFixturesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string token,
        string username)
    {
        var accountId = await ScalarAsync<int>(
            connection,
            transaction,
            """
            INSERT INTO public.accounts (username)
            VALUES (@username)
            RETURNING id;
            """,
            ("username", username));
        var ownerId = await ScalarAsync<int>(
            connection,
            transaction,
            """
            INSERT INTO public.character_base (account_id, name)
            VALUES (@accountId, @name)
            RETURNING id;
            """,
            ("accountId", accountId),
            ("name", $"Savvy{token}"));
        var zeroPetId = await InsertPetAsync(
            connection,
            transaction,
            ownerId,
            $"Zero{token}",
            aptitude: (short)PetAptitude.Rational,
            revision: 101);
        var progressedPetId = await InsertPetAsync(
            connection,
            transaction,
            ownerId,
            $"Progress{token}",
            aptitude: (short)PetAptitude.Transcendent,
            revision: 201);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO public.character_pet_stat_values (
                pet_id,
                stat_code,
                initial_savvy,
                added_savvy,
                growth_acceleration,
                revision,
                base_growth_rate
            )
            SELECT
                @zeroPetId,
                stat_code,
                0,
                100 + stat_code + 0.25,
                200 + stat_code + 0.50,
                1000 + stat_code,
                0.20 + stat_code * 0.01
            FROM generate_series(1, 6) AS stat(stat_code)
            UNION ALL
            SELECT
                @progressedPetId,
                stat_code,
                900 + stat_code * 10,
                300 + stat_code + 0.25,
                400 + stat_code + 0.50,
                2000 + stat_code,
                13 + stat_code * 0.10
            FROM generate_series(1, 6) AS stat(stat_code);
            """,
            ("zeroPetId", zeroPetId),
            ("progressedPetId", progressedPetId));

        return new Fixture(ownerId, zeroPetId, progressedPetId);
    }

    private static Task<long> InsertPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int ownerId,
        string name,
        short aptitude,
        long revision) =>
        ScalarAsync<long>(
            connection,
            transaction,
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
                completed_rebirths,
                rebirths_remaining,
                completed_pet_merges,
                has_soul_contract,
                has_owner_merge_talent,
                talent_mask,
                current_energy,
                maximum_energy,
                amity,
                satiety,
                remaining_lifetime,
                available_stat_points,
                growth_revealed,
                bound,
                activity_state,
                is_carried,
                is_summoned,
                contributes_to_character,
                revision,
                created_at,
                updated_at
            )
            VALUES (
                @ownerId,
                1,
                @name,
                1,
                42,
                1234567,
                @aptitude,
                12.3456,
                2,
                3,
                4,
                true,
                true,
                16,
                77,
                100,
                234,
                88,
                9876,
                9,
                true,
                true,
                'owned',
                false,
                false,
                false,
                @revision,
                TIMESTAMPTZ '2026-07-20 01:02:03+00',
                TIMESTAMPTZ '2026-07-20 04:05:06+00'
            )
            RETURNING id;
            """,
            ("ownerId", ownerId),
            ("name", name),
            ("aptitude", aptitude),
            ("revision", revision));
}
