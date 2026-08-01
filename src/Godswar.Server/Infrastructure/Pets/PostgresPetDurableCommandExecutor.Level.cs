using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Commands;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetLevelUpgradeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetLevelUpgradeCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var pet = await LockPetAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.PetId,
            cancellationToken);
        if (pet is null)
        {
            return new(
                PetDurableReceiptStatus.PetNotFound,
                PetId: envelope.Command.PetId);
        }
        if (!string.Equals(
                pet.ActivityState,
                "owned",
                StringComparison.Ordinal))
        {
            return FromPet(
                PetDurableReceiptStatus.PetUnavailable,
                pet);
        }
        if (pet.Level >= _petContent.Settings.MaximumLevel)
        {
            return FromPet(
                PetDurableReceiptStatus.PetMaximumLevel,
                pet);
        }
        var cost = _petContent.RequiredExperienceForNextLevel(pet.Level);
        if (pet.Experience < cost)
        {
            return FromPet(
                PetDurableReceiptStatus.PetInsufficientExperience,
                pet);
        }
        if (!string.Equals(
                pet.InitialSavvySourceVersion,
                "growth-x1-v1",
                StringComparison.Ordinal) ||
            await CountValidPetStatsAsync(
                connection,
                transaction,
                pet.PetId,
                cancellationToken) != 6)
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has invalid level-growth provenance.");
        }

        await using (var stats = CreateCommand(
            """
            UPDATE public.character_pet_stat_values
            SET initial_savvy = initial_savvy + base_growth_rate,
                revision = revision + 1
            WHERE pet_id = @petId;
            """,
            connection,
            transaction))
        {
            stats.Parameters.AddWithValue("petId", pet.PetId);
            if (await stats.ExecuteNonQueryAsync(cancellationToken) != 6)
            {
                throw new InvalidDataException(
                    "The pet level stat update was not exact.");
            }
        }

        var nextLevel = checked((short)(pet.Level + 1));
        var nextExperience = pet.Experience - cost;
        await using var update = CreateCommand(
            """
            UPDATE public.character_pets
            SET level = @level,
                experience = @experience,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND revision = @revision
            RETURNING revision;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("level", nextLevel);
        update.Parameters.AddWithValue("experience", nextExperience);
        update.Parameters.AddWithValue("petId", pet.PetId);
        update.Parameters.AddWithValue("revision", pet.Revision);
        var nextRevision =
            await update.ExecuteScalarAsync(cancellationToken) as long? ??
            throw new InvalidDataException(
                "The locked pet revision changed during level-up.");
        return new(
            PetDurableReceiptStatus.PetLevelUpgraded,
            PetId: pet.PetId,
            PetLevel: nextLevel,
            PetExperience: nextExperience,
            PetRevision: nextRevision,
            IsCarried: pet.IsCarried,
            IsSummoned: pet.IsSummoned);
    }

    private async Task<int> CountValidPetStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT count(*)::integer
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
              AND stat_code BETWEEN 1 AND 6
              AND base_growth_rate > 0
              AND birth_initial_savvy = base_growth_rate
              AND rarity_added_savvy IS NOT NULL;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
    }
}
