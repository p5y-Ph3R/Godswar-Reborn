using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task InsertPetManagerUtilityAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        PetTransition transition,
        CancellationToken cancellationToken)
    {
        var evidence = transition.PetManagerUtility ??
            throw new InvalidDataException(
                "Pet Manager utility transition has no evidence.");
        var operation = envelope.Command.Operation switch
        {
            PetManagerUtilityOperation.CheckGrowth => "check_growth",
            PetManagerUtilityOperation.Seal => "seal",
            PetManagerUtilityOperation.Unseal => "unseal",
            PetManagerUtilityOperation.ClaimPetCall => "claim_pet_call",
            PetManagerUtilityOperation.ClaimMerge => "claim_merge",
            PetManagerUtilityOperation.ChangeGender => "change_gender",
            _ => throw new ArgumentOutOfRangeException()
        };
        var consumed = JsonSerializer.Serialize(
            (transition.InventoryMutations ?? [])
                .Where(static mutation =>
                    mutation.MutationKind != "add")
                .Select(static mutation => new
                {
                    item_instance_id = mutation.ItemInstanceId,
                    mutation_kind = mutation.MutationKind,
                    reason_code = mutation.ReasonCode
                }));
        var evidenceState = new
        {
            operation = operation,
            status = (byte)transition.Status,
            pet_id = evidence.PetId,
            item_id = evidence.ItemTemplateId,
            item_instance_id = evidence.ItemInstanceId,
            kit_bag_slot = evidence.KitBagSlot,
            previous_sex = evidence.PreviousSex,
            new_sex = evidence.NewSex,
            growth = evidence.Growth
        };
        var beforeState = JsonSerializer.Serialize(new
        {
            evidence = evidenceState,
            pet = evidence.BeforePetState,
            inventory = (transition.InventoryMutations ?? [])
                .Select(static mutation => new
                {
                    item_instance_id = mutation.ItemInstanceId,
                    state = mutation.BeforeState
                })
        });
        var afterState = JsonSerializer.Serialize(new
        {
            evidence = evidenceState,
            pet = evidence.AfterPetState,
            inventory = (transition.InventoryMutations ?? [])
                .Select(static mutation => new
                {
                    item_instance_id = mutation.ItemInstanceId,
                    state = mutation.AfterState
                })
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
                @petId, @petId, @operation, @outcome,
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
            evidence.PetId > 0 ? evidence.PetId : DBNull.Value);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue(
            "outcome",
            transition.Succeeded ? "committed" : "rejected");
        command.Parameters.Add(
            "beforeState",
            NpgsqlDbType.Jsonb).Value = beforeState;
        command.Parameters.Add(
            "afterState",
            NpgsqlDbType.Jsonb).Value = afterState;
        command.Parameters.Add(
            "consumedItems",
            NpgsqlDbType.Jsonb).Value = consumed;
        command.Parameters.AddWithValue(
            "reasonCode",
            transition.Succeeded
                ? DBNull.Value
                : transition.Status.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Pet Manager utility audit was not appended once.");
        }
    }
}
