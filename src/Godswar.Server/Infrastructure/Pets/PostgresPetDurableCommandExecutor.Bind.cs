using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetBindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetBindCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        _ = character;
        var pet = await LockSummonedPetForBindAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null || !pet.IsCarried || !pet.IsSummoned ||
            pet.ContributesToCharacter ||
            !string.Equals(
                pet.ActivityState,
                "owned",
                StringComparison.Ordinal))
        {
            await WritePetBindAuditAsync(
                connection,
                transaction,
                envelope,
                pet,
                PetDurableReceiptStatus.PetBindPetNotSummoned,
                outcome: "rejected",
                reasonCode: "summoned_owned_pet_not_found",
                nextRevision: null,
                cancellationToken);
            return FromPetBind(
                PetDurableReceiptStatus.PetBindPetNotSummoned,
                pet);
        }
        if (pet.IsBound)
        {
            await WritePetBindAuditAsync(
                connection,
                transaction,
                envelope,
                pet,
                PetDurableReceiptStatus.PetAlreadyBound,
                outcome: "rejected",
                reasonCode: "pet_already_bound",
                nextRevision: null,
                cancellationToken);
            return FromPetBind(
                PetDurableReceiptStatus.PetAlreadyBound,
                pet);
        }

        var nextRevision = await UpdatePetBoundAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            pet,
            cancellationToken);
        await WritePetBindAuditAsync(
            connection,
            transaction,
            envelope,
            pet,
            PetDurableReceiptStatus.PetBound,
            outcome: "committed",
            reasonCode: "pet_bound",
            nextRevision,
            cancellationToken);
        return new PetTransition(
            PetDurableReceiptStatus.PetBound,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: nextRevision,
            IsCarried: true,
            IsSummoned: true);
    }

    private async Task<LockedBindPet?> LockSummonedPetForBindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, species_id, level, experience, revision,
                bound, is_carried, is_summoned, activity_state,
                contributes_to_character, has_soul_contract
            FROM public.character_pets
            WHERE user_id = @characterId
              AND is_summoned
              AND NOT contributes_to_character
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
        var pet = new LockedBindPet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            reader.GetString(8),
            reader.GetBoolean(9),
            reader.GetBoolean(10));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "A character has more than one summoned pet.");
        }
        return pet;
    }

    private async Task<long> UpdatePetBoundAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedBindPet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET bound = true,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @expectedRevision
              AND NOT bound
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
              AND NOT contributes_to_character
            RETURNING revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "expectedRevision",
            pet.Revision);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(pet.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "The summoned pet was not bound exactly once.");
    }

    private async Task WritePetBindAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetBindCommand> envelope,
        LockedBindPet? pet,
        PetDurableReceiptStatus status,
        string outcome,
        string reasonCode,
        long? nextRevision,
        CancellationToken cancellationToken)
    {
        var beforeState = pet is null
            ? null
            : JsonSerializer.Serialize(new
            {
                pet_id = pet.PetId,
                species_id = pet.SpeciesId,
                pet.Level,
                pet.Experience,
                pet_revision = pet.Revision,
                bound = pet.IsBound,
                pet.IsCarried,
                pet.IsSummoned,
                pet.ActivityState,
                pet.ContributesToCharacter,
                pet.HasSoulContract,
                pet_content_revision = _petContent.Revision.Sha256
            });
        var afterState = nextRevision is null || pet is null
            ? null
            : JsonSerializer.Serialize(new
            {
                pet_id = pet.PetId,
                species_id = pet.SpeciesId,
                pet_revision = nextRevision.Value,
                bound = true,
                pet.HasSoulContract,
                pet_content_revision = _petContent.Revision.Sha256
            });

        await using var command = CreateCommand(
            """
            INSERT INTO public.pet_operation_audit (
                request_id, user_id, user_id_snapshot,
                pet_id, pet_id_snapshot, operation, outcome,
                before_state, after_state, consumed_items, reason_code
            )
            VALUES (
                @requestId, @characterId, @characterId,
                @petId, @petId, 'bind', @outcome,
                @beforeState, @afterState, '[]'::jsonb, @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "requestId",
            envelope.Command.Identity.OperationId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "petId",
            pet is null ? DBNull.Value : pet.PetId);
        command.Parameters.AddWithValue("outcome", outcome);
        AddNullableJson(command, "beforeState", beforeState);
        AddNullableJson(command, "afterState", afterState);
        command.Parameters.AddWithValue("reasonCode", reasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                $"Pet bind status {status} was not audited exactly once.");
        }
    }

    private static PetTransition FromPetBind(
        PetDurableReceiptStatus status,
        LockedBindPet? pet) =>
        new(
            status,
            PetId: pet?.PetId ?? 0,
            PetLevel: pet?.Level ?? 0,
            PetExperience: pet?.Experience ?? 0,
            PetRevision: pet?.Revision ?? 0,
            IsCarried: pet?.IsCarried ?? false,
            IsSummoned: pet?.IsSummoned ?? false);

    private sealed record LockedBindPet(
        long PetId,
        short SpeciesId,
        short Level,
        long Experience,
        long Revision,
        bool IsBound,
        bool IsCarried,
        bool IsSummoned,
        string ActivityState,
        bool ContributesToCharacter,
        bool HasSoulContract);
}
