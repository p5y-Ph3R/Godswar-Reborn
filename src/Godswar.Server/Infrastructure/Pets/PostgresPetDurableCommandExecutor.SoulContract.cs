using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetSoulContractAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetSoulContractCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var pet = await LockActivePetForSoulContractAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null)
        {
            return await RejectPetSoulContractAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetSoulContractPetNotSummoned,
                pet: null,
                "active_pet_not_found",
                cancellationToken);
        }
        if (!string.Equals(
                pet.ActivityState,
                "owned",
                StringComparison.Ordinal) ||
            pet.ContributesToCharacter ||
            pet.SoulContractStage > PetSoulContractPolicy.MaximumStage)
        {
            return await RejectPetSoulContractAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetSoulContractInvalidState,
                pet,
                "invalid_pet_state",
                cancellationToken);
        }
        if (envelope.Command.MaterialTemplateId !=
                PetSoulContractPolicy.ContractSpiritItemId ||
            envelope.Command.Quantity is < 0 or >
                PetSoulContractPolicy.MaximumSpiritCount)
        {
            return await RejectPetSoulContractAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetSoulContractInvalidMaterial,
                pet,
                "invalid_material_selection",
                cancellationToken);
        }

        IReadOnlyList<LockedRebirthMaterial> stacks = [];
        IReadOnlyList<ConsumedRebirthMaterial> consumed = [];
        if (envelope.Command.Quantity > 0)
        {
            stacks = await LockRebirthMaterialStacksAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                envelope.Command.MaterialTemplateId,
                cancellationToken);
            if (stacks.Sum(static value => (int)value.Item.Stack) <
                envelope.Command.Quantity)
            {
                return await RejectPetSoulContractAsync(
                    connection,
                    transaction,
                    envelope,
                    PetDurableReceiptStatus
                        .PetSoulContractInsufficientMaterial,
                    pet,
                    "insufficient_material",
                    cancellationToken);
            }
            consumed = await ConsumeRebirthMaterialsAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                stacks,
                envelope.Command.Quantity,
                cancellationToken);
        }

        var newStage = PetSoulContractPolicy.StageForSpiritCount(
            envelope.Command.Quantity);
        var nextPetRevision = await PersistPetSoulContractAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            pet,
            newStage,
            cancellationToken);
        var evidence = new PetSoulContractEvidence(
            pet.PetId,
            pet.SoulContractStage,
            newStage,
            envelope.Command.MaterialTemplateId,
            checked((byte)envelope.Command.Quantity),
            PetSoulContractPolicy.BasicSavvyIncreaseHundredths(newStage));

        IReadOnlyList<InventoryMutation> mutations = [];
        if (consumed.Count > 0)
        {
            var inventoryRevision = await AdvanceInventoryRevisionAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                character.InventoryRevision,
                cancellationToken);
            mutations = consumed
                .Select(value => new InventoryMutation(
                    value.Item.ItemId,
                    value.MutationKind,
                    value.Item.BeforeState,
                    value.AfterState,
                    "pet_soul_contract",
                    inventoryRevision))
                .ToArray();
        }

        await WritePetSoulContractAuditAsync(
            connection,
            transaction,
            envelope,
            pet,
            evidence,
            consumed,
            cancellationToken);
        return new PetTransition(
            PetDurableReceiptStatus.PetSoulContractSigned,
            KitBagSlot: consumed.Count == 0 ? -1 : consumed[0].BagSlot,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: nextPetRevision,
            IsCarried: pet.IsCarried,
            IsSummoned: pet.IsSummoned,
            InventoryMutations: mutations,
            SoulContract: evidence);
    }

    private async Task<LockedSoulContractPet?>
        LockActivePetForSoulContractAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, level, experience, soul_contract_stage,
                is_carried, is_summoned, activity_state,
                contributes_to_character, revision
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned'
              AND is_summoned
              AND NOT contributes_to_character
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
        var pet = new LockedSoulContractPet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt64(2),
            checked((byte)reader.GetInt16(3)),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.GetInt64(8));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "More than one summoned Soul Contract pet is authoritative.");
        }
        return pet;
    }

    private async Task<long> PersistPetSoulContractAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedSoulContractPet pet,
        byte stage,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET soul_contract_stage = @stage,
                has_soul_contract = true,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
              AND NOT contributes_to_character
            RETURNING revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("stage", checked((short)stage));
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("revision", pet.Revision);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(pet.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "The Soul Contract pet revision was not advanced exactly once.");
    }

    private async Task WritePetSoulContractAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetSoulContractCommand> envelope,
        LockedSoulContractPet pet,
        PetSoulContractEvidence evidence,
        IReadOnlyList<ConsumedRebirthMaterial> consumed,
        CancellationToken cancellationToken)
    {
        var consumedJson = JsonSerializer.Serialize(
            consumed.Select(value => new
            {
                item_id = value.Item.PropId,
                item_instance_id = value.Item.ItemId,
                quantity = value.Quantity,
                kit_bag_slot = value.BagSlot
            }));
        await InsertPetSoulContractAuditAsync(
            connection,
            transaction,
            envelope,
            pet,
            "committed",
            reasonCode: null,
            JsonSerializer.Serialize(new
            {
                pet_id = pet.PetId,
                soul_contract_stage = pet.SoulContractStage,
                selected_material_template_id =
                    envelope.Command.MaterialTemplateId,
                selected_material_quantity = envelope.Command.Quantity,
                pet.Revision
            }),
            JsonSerializer.Serialize(new
            {
                pet_id = pet.PetId,
                soul_contract_stage = evidence.NewStage,
                selected_material_template_id =
                    envelope.Command.MaterialTemplateId,
                selected_material_quantity = envelope.Command.Quantity,
                basic_savvy_increase_hundredths =
                    evidence.BasicSavvyIncreaseHundredths
            }),
            consumedJson,
            cancellationToken);
    }

    private async Task<PetTransition> RejectPetSoulContractAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetSoulContractCommand> envelope,
        PetDurableReceiptStatus status,
        LockedSoulContractPet? pet,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        await InsertPetSoulContractAuditAsync(
            connection,
            transaction,
            envelope,
            pet,
            "rejected",
            reasonCode,
            pet is null
                ? JsonSerializer.Serialize(new
                {
                    selected_material_template_id =
                        envelope.Command.MaterialTemplateId,
                    selected_material_quantity = envelope.Command.Quantity
                })
                : JsonSerializer.Serialize(new
                {
                    pet_id = pet.PetId,
                    soul_contract_stage = pet.SoulContractStage,
                    selected_material_template_id =
                        envelope.Command.MaterialTemplateId,
                    selected_material_quantity = envelope.Command.Quantity,
                    pet.Revision
                }),
            afterState: null,
            consumedItems: "[]",
            cancellationToken);
        return new PetTransition(
            status,
            PetId: pet?.PetId ?? 0,
            PetLevel: pet?.Level ?? 0,
            PetExperience: pet?.Experience ?? 0,
            PetRevision: pet?.Revision ?? 0,
            IsCarried: pet?.IsCarried ?? false,
            IsSummoned: pet?.IsSummoned ?? false);
    }

    private async Task InsertPetSoulContractAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetSoulContractCommand> envelope,
        LockedSoulContractPet? pet,
        string outcome,
        string? reasonCode,
        string? beforeState,
        string? afterState,
        string consumedItems,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.pet_operation_audit (
                request_id, user_id, user_id_snapshot,
                pet_id, pet_id_snapshot, operation, outcome,
                before_state, after_state, consumed_items, reason_code
            )
            VALUES (
                @requestId, @characterId, @characterId,
                @petId, @petId, 'soul_contract', @outcome,
                @beforeState, @afterState, @consumedItems, @reasonCode
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
        command.Parameters.Add(
            "beforeState",
            NpgsqlDbType.Jsonb).Value =
            beforeState is null ? DBNull.Value : beforeState;
        command.Parameters.Add(
            "afterState",
            NpgsqlDbType.Jsonb).Value =
            afterState is null ? DBNull.Value : afterState;
        command.Parameters.Add(
            "consumedItems",
            NpgsqlDbType.Jsonb).Value = consumedItems;
        command.Parameters.AddWithValue(
            "reasonCode",
            reasonCode is null ? DBNull.Value : reasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Soul Contract audit was not appended exactly once.");
        }
    }

    private sealed record LockedSoulContractPet(
        long PetId,
        short Level,
        long Experience,
        byte SoulContractStage,
        bool IsCarried,
        bool IsSummoned,
        string ActivityState,
        bool ContributesToCharacter,
        long Revision);
}
