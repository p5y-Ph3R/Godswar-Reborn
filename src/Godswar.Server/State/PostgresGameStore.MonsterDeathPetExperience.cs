using System.Data;
using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<PetMonsterExperienceResult>
        ApplyPetMonsterKillExperienceAsync(
            int accountId,
            int characterId,
            Guid deathEventId,
            int experience,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 ||
            deathEventId == Guid.Empty ||
            experience is < 0 or > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(experience));
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockDeathIdentityAsync(
            connection,
            transaction,
            $"pet-exp:{deathEventId:N}",
            cancellationToken);

        var replay = await ReadPetExperienceClaimAsync(
            connection,
            transaction,
            deathEventId,
            accountId,
            characterId,
            experience,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        if (!await CharacterExistsAsync(
                connection,
                transaction,
                accountId,
                characterId,
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                PetMonsterExperienceStatus.CharacterNotFound,
                deathEventId,
                0,
                null,
                null,
                null);
        }

        var pet = await LockSummonedPetAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        if (pet is null || experience == 0)
        {
            await InsertPetExperienceClaimAsync(
                connection,
                transaction,
                deathEventId,
                accountId,
                characterId,
                experience,
                pet: null,
                totalExperience: null,
                petRevision: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                PetMonsterExperienceStatus.NoSummonedPet,
                deathEventId,
                0,
                null,
                null,
                null);
        }

        var awarded = checked((int)Math.Min(
            experience,
            PetExperienceItemPolicy.MaximumNativePetExperience -
                pet.Experience));
        var total = checked(pet.Experience + awarded);
        var revision = pet.Revision;
        if (awarded > 0)
        {
            await using var update = new NpgsqlCommand(
                """
                UPDATE public.character_pets
                SET experience = @experience,
                    revision = revision + 1,
                    updated_at = transaction_timestamp()
                WHERE id = @petId
                  AND user_id = @characterId
                  AND revision = @revision
                  AND activity_state = 'owned'
                  AND is_carried
                  AND is_summoned
                RETURNING revision;
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue("experience", total);
            update.Parameters.AddWithValue("petId", pet.PetId);
            update.Parameters.AddWithValue("characterId", characterId);
            update.Parameters.AddWithValue("revision", pet.Revision);
            revision = await update.ExecuteScalarAsync(cancellationToken)
                is long value && value == checked(pet.Revision + 1)
                ? value
                : throw new InvalidDataException(
                    "Summoned-pet EXP was not advanced exactly once.");
        }

        await InsertPetExperienceClaimAsync(
            connection,
            transaction,
            deathEventId,
            accountId,
            characterId,
            experience,
            pet,
            total,
            revision,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            PetMonsterExperienceStatus.Applied,
            deathEventId,
            awarded,
            pet.PetId,
            total,
            revision);
    }

    private static async Task<bool> CharacterExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1 FROM public.character_base
            WHERE account_id = @accountId AND id = @characterId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<PetMonsterExperienceResult?>
        ReadPetExperienceClaimAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid deathEventId,
            int accountId,
            int characterId,
            int experience,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT account_id, character_id, requested_experience,
                   pet_id, experience_before, experience_after, pet_revision
            FROM public.monster_death_pet_experience
            WHERE death_event_id = @deathEventId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("deathEventId", deathEventId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        if (reader.GetInt32(0) != accountId ||
            reader.GetInt32(1) != characterId ||
            reader.GetInt32(2) != experience)
        {
            return new(
                PetMonsterExperienceStatus.RequestConflict,
                deathEventId,
                0,
                null,
                null,
                null);
        }

        var hasPet = !reader.IsDBNull(3);
        return new(
            PetMonsterExperienceStatus.Duplicate,
            deathEventId,
            hasPet
                ? checked((int)(reader.GetInt64(5) - reader.GetInt64(4)))
                : 0,
            hasPet ? reader.GetInt64(3) : null,
            hasPet ? reader.GetInt64(5) : null,
            hasPet ? reader.GetInt64(6) : null);
    }

    private static async Task<SummonedPet?> LockSummonedPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, experience, revision
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var pet = new SummonedPet(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "More than one summoned pet is authoritative.");
        }
        return pet;
    }

    private static async Task InsertPetExperienceClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deathEventId,
        int accountId,
        int characterId,
        int experience,
        SummonedPet? pet,
        long? totalExperience,
        long? petRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.monster_death_pet_experience (
                death_event_id, account_id, character_id,
                requested_experience, pet_id, experience_before,
                experience_after, pet_revision)
            VALUES (@deathEventId, @accountId, @characterId,
                    @experience, @petId, @before, @after, @revision);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("deathEventId", deathEventId);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("experience", experience);
        command.Parameters.AddWithValue(
            "petId",
            pet is null ? DBNull.Value : pet.PetId);
        command.Parameters.AddWithValue(
            "before",
            pet is null ? DBNull.Value : pet.Experience);
        command.Parameters.AddWithValue(
            "after",
            totalExperience.HasValue ? totalExperience.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "revision",
            petRevision.HasValue ? petRevision.Value : DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Monster pet-EXP claim was not inserted exactly once.");
        }
    }

    private sealed record SummonedPet(
        long PetId,
        long Experience,
        long Revision);
}
