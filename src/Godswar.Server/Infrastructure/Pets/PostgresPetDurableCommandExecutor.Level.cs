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
                StringComparison.Ordinal) ||
            pet.ContributesToCharacter)
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
                PetSavvyRuntimeSemantics.SourceVersion,
                StringComparison.Ordinal) ||
            await CountValidPetStatsAsync(
                connection,
                transaction,
                pet.PetId,
                pet.Level,
                cancellationToken) != 6)
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has invalid level-growth provenance.");
        }

        var nextLevel = checked((short)(pet.Level + 1));
        await using (var stats = CreateCommand(
            """
            UPDATE public.character_pet_stat_values
            SET added_savvy =
                    (base_growth_rate + growth_acceleration) * @level,
                revision = revision + 1
            WHERE pet_id = @petId;
            """,
            connection,
            transaction))
        {
            stats.Parameters.AddWithValue("petId", pet.PetId);
            stats.Parameters.AddWithValue("level", nextLevel);
            if (await stats.ExecuteNonQueryAsync(cancellationToken) != 6)
            {
                throw new InvalidDataException(
                    "The pet level stat update was not exact.");
            }
        }

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
        short petLevel,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT count(*)::integer
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
              AND stat_code BETWEEN 1 AND 6
              AND base_growth_rate > 0
              AND birth_initial_savvy > 0
              AND rarity_added_savvy IS NOT NULL
              AND birth_initial_savvy = rarity_added_savvy
              AND initial_savvy > 0
              AND growth_acceleration >= 0
              AND added_savvy =
                    (base_growth_rate + growth_acceleration) * @level
              AND revision >= 0;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("level", petLevel);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
    }
}
