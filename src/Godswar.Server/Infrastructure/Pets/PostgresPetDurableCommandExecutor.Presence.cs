using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Commands;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetPresenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetPresenceTransitionCommand> envelope,
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
                PetId: envelope.Command.PetId,
                PresenceOperation:
                    checked((byte)((byte)envelope.Command.Operation + 1)));
        }
        if (!string.Equals(
                pet.ActivityState,
                "owned",
                StringComparison.Ordinal))
        {
            return FromPet(
                PetDurableReceiptStatus.PetUnavailable,
                pet,
                envelope.Command.Operation);
        }
        if (envelope.Command.Operation is
                PetPresenceCommandOperation.CallOut or
                PetPresenceCommandOperation.Recall &&
            !pet.IsCarried)
        {
            return FromPet(
                PetDurableReceiptStatus.PetNotTaken,
                pet,
                envelope.Command.Operation);
        }

        var carried = pet.IsCarried;
        var summoned = pet.IsSummoned;
        if (envelope.Command.Operation ==
            PetPresenceCommandOperation.Take)
        {
            await ClearOtherCarriedPetsAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                pet.PetId,
                cancellationToken);
            carried = true;
            summoned = false;
        }
        else
        {
            summoned =
                envelope.Command.Operation ==
                    PetPresenceCommandOperation.CallOut;
        }

        await using var update = CreateCommand(
            """
            UPDATE public.character_pets
            SET is_carried = @carried,
                is_summoned = @summoned,
                contributes_to_character =
                    CASE WHEN @summoned
                         THEN contributes_to_character
                         ELSE false
                    END,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
            RETURNING revision;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("carried", carried);
        update.Parameters.AddWithValue("summoned", summoned);
        update.Parameters.AddWithValue("petId", pet.PetId);
        update.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        update.Parameters.AddWithValue("revision", pet.Revision);
        var revision =
            await update.ExecuteScalarAsync(cancellationToken) as long? ??
            throw new InvalidDataException(
                "The locked pet changed during presence transition.");
        return new(
            PetDurableReceiptStatus.PresenceChanged,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: revision,
            IsCarried: carried,
            IsSummoned: summoned,
            PresenceOperation:
                checked((byte)((byte)envelope.Command.Operation + 1)));
    }

    private async Task ClearOtherCarriedPetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET is_carried = false,
                is_summoned = false,
                contributes_to_character = false,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE user_id = @characterId
              AND id <> @petId
              AND (is_carried OR is_summoned OR contributes_to_character);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<LockedPet?> LockPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, level, experience, activity_state, revision,
                is_carried, is_summoned, initial_savvy_source_version
            FROM public.character_pets
            WHERE id = @petId
              AND user_id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedPet(
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7))
            : null;
    }

    private static PetTransition FromPet(
        PetDurableReceiptStatus status,
        LockedPet pet,
        PetPresenceCommandOperation? operation = null) =>
        new(
            status,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: pet.Revision,
            IsCarried: pet.IsCarried,
            IsSummoned: pet.IsSummoned,
            PresenceOperation: operation.HasValue
                ? checked((byte)((byte)operation.Value + 1))
                : (byte)0);

    private sealed record LockedPet(
        long PetId,
        short Level,
        long Experience,
        string ActivityState,
        long Revision,
        bool IsCarried,
        bool IsSummoned,
        string? InitialSavvySourceVersion);
}
