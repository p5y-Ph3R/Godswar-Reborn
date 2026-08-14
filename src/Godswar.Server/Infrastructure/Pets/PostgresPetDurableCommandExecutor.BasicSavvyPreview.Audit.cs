using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task WriteBasicSavvyResetAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetBasicSavvyResetCommand> envelope,
        LockedBasicSavvyResetPet pet,
        LockedFairyFeather feather,
        IReadOnlyList<BasicSavvyResetStat> before,
        IReadOnlyList<BasicSavvyResetStat> after,
        PetBasicSavvyRedistributionRoll roll,
        CancellationToken cancellationToken)
    {
        var total = before.Sum(static value => value.InitialSavvy);
        await using var command = CreateCommand(
            """
            INSERT INTO public.pet_operation_audit (
                request_id, user_id, user_id_snapshot,
                pet_id, pet_id_snapshot, operation, outcome,
                before_state, after_state, consumed_items, reason_code
            )
            VALUES (
                @requestId, @characterId, @characterId,
                @petId, @petId, 'reset_basic_savvy', 'committed',
                jsonb_build_object(
                    'stage', 'before_reset',
                    'pet_revision', @petRevision,
                    'completed_pet_merges', @completedPetMerges,
                    'hatch_baseline_total', @hatchBaselineTotal,
                    'merge_gain_total', @mergeGainTotal,
                    'basic_savvy', @beforeSavvy::jsonb),
                jsonb_build_object(
                    'stage', 'committed',
                    'policy_version', @policyVersion,
                    'roll_tier', @rollTier,
                    'primary_focus', @primaryFocus,
                    'secondary_focus', @secondaryFocus,
                    'tertiary_focus', @tertiaryFocus,
                    'quaternary_focus', @quaternaryFocus,
                    'expected_basic_total', @expectedBasicTotal,
                    'basic_savvy', @afterSavvy::jsonb),
                jsonb_build_array(jsonb_build_object(
                    'item_id', @itemId,
                    'item_instance_id', @itemInstanceId,
                    'quantity', 1,
                    'kit_bag_slot', @kitBagSlot)),
                'fairy_basic_savvy_reset'
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
        command.Parameters.AddWithValue("petRevision", pet.Revision);
        command.Parameters.AddWithValue(
            "completedPetMerges",
            pet.CompletedPetMerges);
        command.Parameters.AddWithValue(
            "hatchBaselineTotal",
            pet.HatchBaselineTotal!.Value);
        command.Parameters.AddWithValue(
            "mergeGainTotal",
            total - pet.HatchBaselineTotal.Value);
        command.Parameters.AddWithValue(
            "beforeSavvy",
            SerializeBasicSavvy(before));
        command.Parameters.AddWithValue(
            "policyVersion",
            PetBasicSavvyRedistributionPolicy.Version);
        command.Parameters.AddWithValue("rollTier", roll.Tier.ToString());
        command.Parameters.AddWithValue(
            "primaryFocus",
            roll.PrimaryFocus.ToString());
        command.Parameters.AddWithValue(
            "secondaryFocus",
            roll.SecondaryFocus.ToString());
        command.Parameters.AddWithValue(
            "tertiaryFocus",
            roll.TertiaryFocus.ToString());
        command.Parameters.AddWithValue(
            "quaternaryFocus",
            roll.QuaternaryFocus.ToString());
        command.Parameters.AddWithValue("expectedBasicTotal", total);
        command.Parameters.AddWithValue(
            "afterSavvy",
            SerializeBasicSavvy(after));
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)PetItemCatalog.FairyFeather));
        command.Parameters.AddWithValue(
            "itemInstanceId",
            feather.Item.ItemId);
        command.Parameters.AddWithValue("kitBagSlot", feather.BagSlot);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The pet Basic-Savvy reset was not audited exactly once.");
        }
    }

    private async Task WriteBasicSavvyAcceptAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetBasicSavvyResetCommand> envelope,
        LockedBasicSavvyResetPet pet,
        LockedBasicSavvyPreview preview,
        IReadOnlyList<BasicSavvyResetStat> before,
        IReadOnlyList<BasicSavvyResetStat> after,
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
                @petId, @petId, 'reset_basic_savvy', 'committed',
                jsonb_build_object(
                    'stage', 'previewed',
                    'pet_revision', @petRevision,
                    'hatch_baseline_total', @hatchBaselineTotal,
                    'merge_gain_total', @mergeGainTotal,
                    'basic_savvy', @beforeSavvy::jsonb),
                jsonb_build_object(
                    'stage', 'accepted',
                    'preview_operation_id', @previewOperationId,
                    'policy_version', @policyVersion,
                    'roll_tier', @rollTier,
                    'primary_focus', @primaryFocus,
                    'secondary_focus', @secondaryFocus,
                    'expected_basic_total', @expectedBasicTotal,
                    'basic_savvy', @afterSavvy::jsonb),
                '[]'::jsonb,
                'fairy_basic_savvy_accept'
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
        command.Parameters.AddWithValue("petRevision", pet.Revision);
        command.Parameters.AddWithValue(
            "hatchBaselineTotal",
            pet.HatchBaselineTotal!.Value);
        command.Parameters.AddWithValue(
            "mergeGainTotal",
            preview.ExpectedBasicTotal - pet.HatchBaselineTotal.Value);
        command.Parameters.AddWithValue(
            "beforeSavvy",
            SerializeBasicSavvy(before));
        command.Parameters.AddWithValue(
            "previewOperationId",
            preview.PreviewOperationId);
        command.Parameters.AddWithValue(
            "policyVersion",
            preview.PolicyVersion);
        command.Parameters.AddWithValue("rollTier", preview.RollTier);
        command.Parameters.AddWithValue(
            "primaryFocus",
            preview.PrimaryFocus);
        command.Parameters.AddWithValue(
            "secondaryFocus",
            preview.SecondaryFocus);
        command.Parameters.AddWithValue(
            "expectedBasicTotal",
            preview.ExpectedBasicTotal);
        command.Parameters.AddWithValue(
            "afterSavvy",
            SerializeBasicSavvy(after));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The accepted pet Basic-Savvy preview was not audited exactly once.");
        }
    }

    private static string SerializeBasicSavvy(
        IReadOnlyList<BasicSavvyResetStat> values) =>
        JsonSerializer.Serialize(values.Select(static value => new
        {
            stat_code = value.StatCode,
            basic_savvy = value.InitialSavvy,
            birth_initial_savvy = value.BirthInitialSavvy,
            rarity_added_savvy = value.RarityAddedSavvy,
            revision = value.Revision
        }));
}
