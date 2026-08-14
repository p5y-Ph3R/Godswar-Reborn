using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<long> PersistPetRebirthPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedRebirthPet pet,
        PetRebirthStats before,
        PetRebirthPlan plan,
        CancellationToken cancellationToken)
    {
        var added = ToRebirthValues(plan.PetAfter.AddedSavvy);
        var acceleration =
            ToRebirthValues(plan.PetAfter.GrowthAcceleration);
        for (var index = 0; index < before.Rows.Count; index++)
        {
            var row = before.Rows[index];
            await using var updateStat = CreateCommand(
                """
                UPDATE public.character_pet_stat_values
                SET added_savvy = @addedSavvy,
                    growth_acceleration = @growthAcceleration,
                    revision = revision + 1
                WHERE pet_id = @petId
                  AND stat_code = @statCode
                  AND initial_savvy = @initialSavvy
                  AND added_savvy = @oldAddedSavvy
                  AND base_growth_rate = @baseGrowthRate
                  AND growth_acceleration = @oldGrowthAcceleration
                  AND rarity_added_savvy = @rarityAddedSavvy
                  AND revision = @revision;
                """,
                connection,
                transaction);
            updateStat.Parameters.AddWithValue(
                "addedSavvy",
                added[index]);
            updateStat.Parameters.AddWithValue(
                "growthAcceleration",
                acceleration[index]);
            updateStat.Parameters.AddWithValue("petId", pet.PetId);
            updateStat.Parameters.AddWithValue("statCode", row.StatCode);
            updateStat.Parameters.AddWithValue(
                "initialSavvy",
                row.InitialSavvy);
            updateStat.Parameters.AddWithValue(
                "oldAddedSavvy",
                row.AddedSavvy);
            updateStat.Parameters.AddWithValue(
                "baseGrowthRate",
                row.BaseGrowthRate);
            updateStat.Parameters.AddWithValue(
                "oldGrowthAcceleration",
                row.GrowthAcceleration);
            updateStat.Parameters.AddWithValue(
                "rarityAddedSavvy",
                row.RarityAddedSavvy);
            updateStat.Parameters.AddWithValue("revision", row.Revision);
            if (await updateStat.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    $"Pet {pet.PetId} rebirth stat {row.StatCode} was not updated exactly once.");
            }
        }

        await using var updatePet = CreateCommand(
            """
            UPDATE public.character_pets
            SET level = @level,
                experience = @experience,
                rank = @rank,
                completed_rebirths = @completedRebirths,
                rebirths_remaining = @rebirthsRemaining,
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
        updatePet.Parameters.AddWithValue(
            "level",
            checked((short)plan.PetAfter.Level));
        updatePet.Parameters.AddWithValue(
            "experience",
            plan.PetAfter.Experience);
        updatePet.Parameters.AddWithValue("rank", plan.PetAfter.Rank);
        updatePet.Parameters.AddWithValue(
            "completedRebirths",
            checked((short)plan.PetAfter.CompletedRebirths));
        updatePet.Parameters.AddWithValue(
            "rebirthsRemaining",
            checked((short)plan.PetAfter.RebirthsRemaining));
        updatePet.Parameters.AddWithValue("petId", pet.PetId);
        updatePet.Parameters.AddWithValue("characterId", characterId);
        updatePet.Parameters.AddWithValue("revision", pet.Revision);
        return await updatePet.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(pet.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "The reborn pet revision was not advanced exactly once.");
    }

    private async Task WritePetRebirthAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetRebirthCommand> envelope,
        LockedRebirthPet pet,
        PetRebirthStats before,
        PetRebirthPlan plan,
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
        await InsertPetRebirthAuditAsync(
            connection,
            transaction,
            envelope,
            pet,
            outcome: "committed",
            reasonCode: null,
            SerializePetRebirthBefore(
                pet,
                before,
                envelope.Command,
                plan),
            SerializePetRebirthAfter(pet, plan),
            consumedJson,
            cancellationToken);
    }

    private async Task<PetTransition> RejectPetRebirthAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetRebirthCommand> envelope,
        PetDurableReceiptStatus status,
        LockedRebirthPet? pet,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        await InsertPetRebirthAuditAsync(
            connection,
            transaction,
            envelope,
            pet,
            outcome: "rejected",
            reasonCode,
            pet is null
                ? JsonSerializer.Serialize(new
                {
                    selected_material_template_id =
                        envelope.Command.MaterialTemplateId,
                    selected_material_quantity =
                        envelope.Command.Quantity
                })
                : JsonSerializer.Serialize(new
                {
                    pet_id = pet.PetId,
                    pet.Level,
                    pet.Experience,
                    pet.CompletedRebirths,
                    pet.RebirthsRemaining,
                    pet.Revision,
                    selected_material_template_id =
                        envelope.Command.MaterialTemplateId,
                    selected_material_quantity =
                        envelope.Command.Quantity
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

    private async Task InsertPetRebirthAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetRebirthCommand> envelope,
        LockedRebirthPet? pet,
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
                @petId, @petId, 'rebirth', @outcome,
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
        AddNullableJson(command, "beforeState", beforeState);
        AddNullableJson(command, "afterState", afterState);
        command.Parameters.Add(
            "consumedItems",
            NpgsqlDbType.Jsonb).Value = consumedItems;
        command.Parameters.AddWithValue(
            "reasonCode",
            reasonCode is null ? DBNull.Value : reasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The pet rebirth audit was not appended exactly once.");
        }
    }

    private static void AddNullableJson(
        NpgsqlCommand command,
        string name,
        string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Jsonb).Value =
            value is null ? DBNull.Value : value;

    private static string SerializePetRebirthBefore(
        LockedRebirthPet pet,
        PetRebirthStats stats,
        PetRebirthCommand request,
        PetRebirthPlan plan) =>
        JsonSerializer.Serialize(new
        {
            pet_id = pet.PetId,
            pet.Level,
            pet.Experience,
            pet.Rank,
            pet.CompletedRebirths,
            pet.RebirthsRemaining,
            pet.Revision,
            selected_material_template_id = request.MaterialTemplateId,
            selected_material_quantity = request.Quantity,
            added_savvy = stats.Added,
            growth_acceleration = stats.GrowthAcceleration,
            surplus_level_count = Math.Max(
                0,
                pet.Level - plan.RequiredLevel),
            historical_surplus_experience =
                plan.PetAfter.Experience - pet.Experience,
            pre_rebirth_unspent_experience = pet.Experience,
            carried_experience = plan.PetAfter.Experience,
            stat_revisions = stats.Rows.Select(static row => new
            {
                stat_code = row.StatCode,
                revision = row.Revision
            })
        });

    private static string SerializePetRebirthAfter(
        LockedRebirthPet pet,
        PetRebirthPlan plan) =>
        JsonSerializer.Serialize(new
        {
            pet_id = plan.PetAfter.PetId,
            plan.PetAfter.Level,
            plan.PetAfter.Experience,
            plan.PetAfter.Rank,
            plan.PetAfter.CompletedRebirths,
            plan.PetAfter.RebirthsRemaining,
            added_savvy = plan.PetAfter.AddedSavvy,
            growth_acceleration = plan.PetAfter.GrowthAcceleration,
            required_level = plan.RequiredLevel,
            surplus_level_count = Math.Max(
                0,
                pet.Level - plan.RequiredLevel),
            historical_surplus_experience =
                plan.PetAfter.Experience - pet.Experience,
            pre_rebirth_unspent_experience = pet.Experience,
            carried_experience = plan.PetAfter.Experience
        });

    private static decimal[] ToRebirthValues(PetSavvy value) =>
    [
        value.Agility,
        value.Strength,
        value.Accuracy,
        value.Technique,
        value.Wisdom,
        value.Luck
    ];

    private static PetContentStatVector ToGrowthVector(PetSavvy value) =>
        new(
            value.Agility,
            value.Strength,
            value.Accuracy,
            value.Technique,
            value.Wisdom,
            value.Luck);
}
