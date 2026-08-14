using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task
        AssertTakeSwitchesCompanionAtomicallyAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        long previousPetId)
    {
        var nextPetId = await SeedPresenceSwitchPetAsync(
            dataSource,
            subject.CharacterId,
            previousPetId);

        var switched = await CheckPresenceReplayAsync(
            executor,
            restarted,
            subject,
            correlation,
            nextPetId,
            PetPresenceCommandOperation.Take,
            isCarried: true,
            isSummoned: true);
        var switchedState = await ReadPresenceSwitchStateAsync(
            dataSource,
            subject.CharacterId,
            previousPetId,
            nextPetId);
        Check.True(
            switchedState is
            {
                PreviousIsCarried: false,
                PreviousIsSummoned: false,
                PreviousContributes: false,
                NextIsCarried: true,
                NextIsSummoned: true,
                CarriedCount: 1,
                SummonedCount: 1
            } &&
            switched.Receipt.IsCarried &&
            switched.Receipt.IsSummoned &&
            switched.Receipt.PetRevision ==
                switchedState.NextRevision,
            "Take of another pet atomically clears the previous companion, summons the selected pet, and receipts the committed revision");

        _ = await CheckPresenceReplayAsync(
            executor,
            restarted,
            subject,
            correlation,
            nextPetId,
            PetPresenceCommandOperation.Recall,
            isCarried: true,
            isSummoned: false);
        var repeatedTake = await CheckPresenceReplayAsync(
            executor,
            restarted,
            subject,
            correlation,
            nextPetId,
            PetPresenceCommandOperation.Take,
            isCarried: true,
            isSummoned: false);
        var repeatedState = await ReadPresenceSwitchStateAsync(
            dataSource,
            subject.CharacterId,
            previousPetId,
            nextPetId);
        Check.True(
            !repeatedTake.Receipt.IsSummoned &&
            repeatedTake.Receipt.PetRevision ==
                repeatedState.NextRevision &&
            repeatedState is
            {
                PreviousIsCarried: false,
                PreviousIsSummoned: false,
                NextIsCarried: true,
                NextIsSummoned: false,
                CarriedCount: 1,
                SummonedCount: 0
            },
            "Take of the already-carried recalled pet preserves its presentation and returns the current revision");
    }

    private static async Task<long> SeedPresenceSwitchPetAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long previousPetId)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var clear = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET is_carried = false,
                is_summoned = false,
                contributes_to_character = false
            WHERE user_id = @characterId;
            """,
            connection,
            transaction))
        {
            clear.Parameters.AddWithValue("characterId", characterId);
            Check.True(
                await clear.ExecuteNonQueryAsync() >= 1,
                "presence-switch fixture clears prior projections");
        }
        await using (var summon = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET is_carried = true,
                is_summoned = true
            WHERE id = @petId
              AND user_id = @characterId;
            """,
            connection,
            transaction))
        {
            summon.Parameters.AddWithValue("petId", previousPetId);
            summon.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(
                1,
                await summon.ExecuteNonQueryAsync(),
                "presence-switch fixture summons the previous pet");
        }
        long nextPetId;
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO public.character_pets (
                user_id, species_id, name, sex, level, experience,
                aptitude, remaining_lifetime, bound, activity_state,
                growth_revealed, growth_activation_policy_version,
                is_carried, is_summoned, contributes_to_character,
                initial_savvy_baseline_total,
                rarity_added_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version,
                completed_pet_merges,
                birth_rank, hatch_rank_roll,
                hatch_rank_outcome_order,
                hatch_rank_content_revision
            )
            SELECT
                user_id, species_id, 'Presence Switch Fixture', sex,
                level, experience, aptitude, remaining_lifetime, bound, 'owned',
                growth_revealed, growth_activation_policy_version,
                false, false, false,
                initial_savvy_baseline_total,
                rarity_added_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version,
                completed_pet_merges,
                birth_rank, hatch_rank_roll,
                hatch_rank_outcome_order,
                hatch_rank_content_revision
            FROM public.character_pets
            WHERE id = @petId
              AND user_id = @characterId
            RETURNING id;
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("petId", previousPetId);
            insert.Parameters.AddWithValue("characterId", characterId);
            nextPetId = Convert.ToInt64(
                await insert.ExecuteScalarAsync());
        }
        await using (var stats = new NpgsqlCommand(
            """
            INSERT INTO public.character_pet_stat_values (
                pet_id, stat_code, initial_savvy, added_savvy,
                base_growth_rate, growth_acceleration, revision,
                birth_initial_savvy, rarity_added_savvy
            )
            SELECT
                @nextPetId, stat_code, initial_savvy, added_savvy,
                base_growth_rate, growth_acceleration, revision,
                birth_initial_savvy, rarity_added_savvy
            FROM public.character_pet_stat_values
            WHERE pet_id = @previousPetId;
            """,
            connection,
            transaction))
        {
            stats.Parameters.AddWithValue("nextPetId", nextPetId);
            stats.Parameters.AddWithValue("previousPetId", previousPetId);
            Check.Equal(
                6,
                await stats.ExecuteNonQueryAsync(),
                "presence-switch fixture clones six growth rows");
        }
        await transaction.CommitAsync();
        return nextPetId;
    }

    private static async Task<PresenceSwitchState>
        ReadPresenceSwitchStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long previousPetId,
        long nextPetId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                previous.is_carried,
                previous.is_summoned,
                previous.contributes_to_character,
                next.is_carried,
                next.is_summoned,
                next.revision,
                (
                    SELECT count(*)
                    FROM public.character_pets
                    WHERE user_id = @characterId
                      AND is_carried
                ),
                (
                    SELECT count(*)
                    FROM public.character_pets
                    WHERE user_id = @characterId
                      AND is_summoned
                )
            FROM public.character_pets previous
            JOIN public.character_pets next
              ON next.id = @nextPetId
             AND next.user_id = previous.user_id
            WHERE previous.id = @previousPetId
              AND previous.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("previousPetId", previousPetId);
        command.Parameters.AddWithValue("nextPetId", nextPetId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The presence-switch fixture disappeared.");
        }
        return new PresenceSwitchState(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
    }

    private sealed record PresenceSwitchState(
        bool PreviousIsCarried,
        bool PreviousIsSummoned,
        bool PreviousContributes,
        bool NextIsCarried,
        bool NextIsSummoned,
        long NextRevision,
        long CarriedCount,
        long SummonedCount);
}
