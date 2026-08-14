using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task WritePetGrowthPreviewAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetGrowthResetCommand> envelope,
        LockedGrowthResetPet pet,
        LockedPhoenixFeather feather,
        IReadOnlyList<GrowthResetStat> before,
        decimal[] rates,
        decimal[] rebirthModifiers,
        decimal totalGrowth,
        CancellationToken cancellationToken)
    {
        var effectiveRates = rates.Zip(
            rebirthModifiers,
            static (rate, modifier) => rate + modifier).ToArray();
        await using var command = CreateCommand(
            """
            INSERT INTO public.pet_operation_audit (
                request_id, user_id, user_id_snapshot,
                pet_id, pet_id_snapshot, operation, outcome,
                before_state, after_state, consumed_items, reason_code
            )
            VALUES (
                @requestId, @characterId, @characterId,
                @petId, @petId, 'reveal_growth', 'committed',
                jsonb_build_object(
                    'stage', 'before_preview',
                    'growth_revealed', @growthRevealed,
                    'growth', @beforeGrowth::jsonb),
                jsonb_build_object(
                    'stage', 'preview',
                    'aptitude', @aptitude,
                    'rate_semantics', @rateSemantics,
                    'completed_rebirths', @completedRebirths,
                    'nature_total_growth', @totalGrowth,
                    'nature_growth_rates', @growthRates::jsonb,
                    'rebirth_modifier_min', @rebirthModifierMin,
                    'rebirth_modifier_max', @rebirthModifierMax,
                    'rebirth_modifiers', @rebirthModifiers::jsonb,
                    'effective_total_growth', @effectiveTotalGrowth,
                    'effective_growth_rates', @effectiveGrowthRates::jsonb,
                    'total_growth', @totalGrowth,
                    'growth_rates', @growthRates::jsonb),
                jsonb_build_array(jsonb_build_object(
                    'item_id', @itemId,
                    'item_instance_id', @itemInstanceId,
                    'quantity', 1,
                    'kit_bag_slot', @kitBagSlot)),
                'phoenix_growth_preview'
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
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue(
            "growthRevealed",
            pet.GrowthRevealed);
        command.Parameters.AddWithValue("beforeGrowth", SerializeGrowth(before));
        command.Parameters.AddWithValue("aptitude", pet.Aptitude);
        command.Parameters.AddWithValue(
            "rateSemantics",
            CountWidenedRateSemantics);
        command.Parameters.AddWithValue(
            "completedRebirths",
            pet.CompletedRebirths);
        command.Parameters.AddWithValue("totalGrowth", totalGrowth);
        command.Parameters.AddWithValue(
            "growthRates",
            System.Text.Json.JsonSerializer.Serialize(rates));
        command.Parameters.AddWithValue(
            "rebirthModifierMin",
            pet.CompletedRebirths *
                PetPhoenixRebirthModifierPolicy.MinimumPerRebirth);
        command.Parameters.AddWithValue(
            "rebirthModifierMax",
            pet.CompletedRebirths *
                PetPhoenixRebirthModifierPolicy.MaximumPerRebirth);
        command.Parameters.AddWithValue(
            "rebirthModifiers",
            System.Text.Json.JsonSerializer.Serialize(rebirthModifiers));
        command.Parameters.AddWithValue(
            "effectiveTotalGrowth",
            effectiveRates.Sum());
        command.Parameters.AddWithValue(
            "effectiveGrowthRates",
            System.Text.Json.JsonSerializer.Serialize(effectiveRates));
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)PetItemCatalog.PhoenixFeather));
        command.Parameters.AddWithValue(
            "itemInstanceId",
            feather.Item.ItemId);
        command.Parameters.AddWithValue("kitBagSlot", feather.BagSlot);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The pet Growth preview was not audited exactly once.");
        }
    }

    private async Task WritePetGrowthAcceptAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetGrowthResetCommand> envelope,
        LockedGrowthResetPet pet,
        LockedPetGrowthPreview preview,
        IReadOnlyList<GrowthResetStat> before,
        IReadOnlyList<GrowthResetStat> after,
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
                @petId, @petId, 'reveal_growth', 'committed',
                jsonb_build_object(
                    'stage', 'previewed',
                    'growth_revealed', @oldGrowthRevealed,
                    'growth', @beforeGrowth::jsonb),
                jsonb_build_object(
                    'stage', 'accepted',
                    'preview_operation_id', @previewOperationId,
                    'rate_semantics', @rateSemantics,
                    'completed_rebirths', @completedRebirths,
                    'rebirth_modifiers', @rebirthModifiers::jsonb,
                    'growth_revealed', true,
                    'growth', @afterGrowth::jsonb),
                '[]'::jsonb,
                'phoenix_growth_accept'
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
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue(
            "oldGrowthRevealed",
            pet.GrowthRevealed);
        command.Parameters.AddWithValue("beforeGrowth", SerializeGrowth(before));
        command.Parameters.AddWithValue("afterGrowth", SerializeGrowth(after));
        command.Parameters.AddWithValue(
            "previewOperationId",
            envelope.Command.PreviewOperationId);
        command.Parameters.AddWithValue(
            "rateSemantics",
            preview.RateSemantics);
        command.Parameters.AddWithValue(
            "completedRebirths",
            preview.CompletedRebirths ?? -1);
        command.Parameters.AddWithValue(
            "rebirthModifiers",
            System.Text.Json.JsonSerializer.Serialize(
                preview.RebirthModifiers ?? []));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The accepted pet Growth preview was not audited exactly once.");
        }
    }
}
