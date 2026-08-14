using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task SeedFairyBasicSavvyResetAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pet_stat_values
            SET initial_savvy = initial_savvy +
                    CASE stat_code
                        WHEN 1 THEN 3.25
                        WHEN 2 THEN 2.75
                        ELSE 0
                    END,
                revision = revision + 1
            WHERE pet_id = @petId;

            UPDATE public.character_pets
            SET completed_pet_merges = completed_pet_merges + 1,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId;

            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @featherSlot, 11000,
                1, 1, 1, 7, 0, 0
            );
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("featherSlot", FairyFeatherSlot);
        Check.Equal(
            8,
            await command.ExecuteNonQueryAsync(),
            "Fairy fixture preserves six stats, advances one pet, and inserts one stack");
    }

    private static async Task DeleteFairyFeathersAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            DELETE FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = 11000;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "Fairy fixture removes its remaining feather stack");
    }

    private static async Task<PetBasicSavvyResetState>
        ReadPetBasicSavvyResetStateAsync(
            NpgsqlDataSource dataSource,
            int characterId,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.revision,
                pet.completed_pet_merges,
                pet.initial_savvy_baseline_total,
                ARRAY(
                    SELECT initial_savvy
                    FROM public.character_pet_stat_values ordered
                    WHERE ordered.pet_id = pet.id
                    ORDER BY stat_code
                ),
                ARRAY(
                    SELECT birth_initial_savvy
                    FROM public.character_pet_stat_values ordered
                    WHERE ordered.pet_id = pet.id
                    ORDER BY stat_code
                ),
                ARRAY(
                    SELECT rarity_added_savvy
                    FROM public.character_pet_stat_values ordered
                    WHERE ordered.pet_id = pet.id
                    ORDER BY stat_code
                ),
                ARRAY(
                    SELECT revision
                    FROM public.character_pet_stat_values ordered
                    WHERE ordered.pet_id = pet.id
                    ORDER BY stat_code
                ),
                base.inventory_revision,
                coalesce((
                    SELECT sum(stack)
                    FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND prop_id = 11000
                ), 0)::bigint,
                (
                    SELECT count(*)
                    FROM public.pet_operation_audit
                    WHERE user_id = @characterId
                      AND pet_id = @petId
                      AND operation = 'reset_basic_savvy'
                      AND outcome = 'committed'
                ),
                (
                    SELECT preview_operation_id
                    FROM public.character_pet_basic_savvy_previews
                    WHERE user_id = @characterId
                ),
                (
                    SELECT expected_basic_total
                    FROM public.character_pet_basic_savvy_previews
                    WHERE user_id = @characterId
                ),
                (
                    SELECT policy_version
                    FROM public.character_pet_basic_savvy_previews
                    WHERE user_id = @characterId
                ),
                (
                    SELECT count(*)
                    FROM public.pet_durable_command_evidence
                    WHERE aggregate_key = @aggregateKey
                      AND command_family = 'pet_basic_savvy_reset'
                ),
                (
                    SELECT after_state ->> 'policy_version'
                    FROM public.pet_operation_audit
                    WHERE user_id = @characterId
                      AND pet_id = @petId
                      AND operation = 'reset_basic_savvy'
                    ORDER BY id DESC
                    LIMIT 1
                ),
                (
                    SELECT reason_code
                    FROM public.pet_operation_audit
                    WHERE user_id = @characterId
                      AND pet_id = @petId
                      AND operation = 'reset_basic_savvy'
                    ORDER BY id DESC
                    LIMIT 1
                ),
                (
                    SELECT (before_state ->> 'merge_gain_total')::numeric
                    FROM public.pet_operation_audit
                    WHERE user_id = @characterId
                      AND pet_id = @petId
                      AND operation = 'reset_basic_savvy'
                    ORDER BY id DESC
                    LIMIT 1
                ),
                (
                    SELECT (after_state ->> 'expected_basic_total')::numeric
                    FROM public.pet_operation_audit
                    WHERE user_id = @characterId
                      AND pet_id = @petId
                      AND operation = 'reset_basic_savvy'
                    ORDER BY id DESC
                    LIMIT 1
                ),
                (
                    SELECT after_state ->> 'tertiary_focus'
                    FROM public.pet_operation_audit
                    WHERE user_id = @characterId
                      AND pet_id = @petId
                      AND operation = 'reset_basic_savvy'
                    ORDER BY id DESC
                    LIMIT 1
                ),
                (
                    SELECT after_state ->> 'quaternary_focus'
                    FROM public.pet_operation_audit
                    WHERE user_id = @characterId
                      AND pet_id = @petId
                      AND operation = 'reset_basic_savvy'
                    ORDER BY id DESC
                    LIMIT 1
                ),
                (
                    SELECT count(*)
                    FROM public.character_inventory_ledger
                    WHERE character_id = @characterId
                      AND reason_code = 'pet_basic_savvy_reset'
                )
            FROM public.character_pets pet
            JOIN public.character_base base ON base.id = pet.user_id
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{characterId}");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Fairy Basic-Savvy state was not returned.");
        }
        return new PetBasicSavvyResetState(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetFieldValue<decimal[]>(3),
            reader.GetFieldValue<decimal[]>(4),
            reader.GetFieldValue<decimal[]>(5),
            reader.GetFieldValue<long[]>(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10),
            reader.IsDBNull(11) ? null : reader.GetDecimal(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetInt64(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetDecimal(16),
            reader.IsDBNull(17) ? null : reader.GetDecimal(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.GetInt64(20));
    }

    private static bool SamePetBasicSavvyValues(
        PetBasicSavvyResetState left,
        PetBasicSavvyResetState right) =>
        left.PetRevision == right.PetRevision &&
        left.CompletedPetMerges == right.CompletedPetMerges &&
        left.HatchBaselineTotal == right.HatchBaselineTotal &&
        left.BasicValues.SequenceEqual(right.BasicValues) &&
        left.BirthValues.SequenceEqual(right.BirthValues) &&
        left.RarityValues.SequenceEqual(right.RarityValues) &&
        left.StatRevisions.SequenceEqual(right.StatRevisions);

    private static bool SameBasicSavvyResetState(
        PetBasicSavvyResetState left,
        PetBasicSavvyResetState right,
        bool compareFeatherStack = true) =>
        SamePetBasicSavvyValues(left, right) &&
        left.InventoryRevision == right.InventoryRevision &&
        (!compareFeatherStack || left.FeatherStack == right.FeatherStack) &&
        left.BasicSavvyAuditCount == right.BasicSavvyAuditCount &&
        left.ResetLedgerCount == right.ResetLedgerCount &&
        left.PendingPreviewOperationId ==
            right.PendingPreviewOperationId &&
        left.PendingExpectedTotal == right.PendingExpectedTotal &&
        left.PendingPolicyVersion == right.PendingPolicyVersion;

    private sealed record PetBasicSavvyResetState(
        long PetRevision,
        int CompletedPetMerges,
        int HatchBaselineTotal,
        decimal[] BasicValues,
        decimal[] BirthValues,
        decimal[] RarityValues,
        long[] StatRevisions,
        long InventoryRevision,
        long FeatherStack,
        long BasicSavvyAuditCount,
        Guid? PendingPreviewOperationId,
        decimal? PendingExpectedTotal,
        string? PendingPolicyVersion,
        long EvidenceCount,
        string? LatestAuditPolicyVersion,
        string? LatestAuditReasonCode,
        decimal? LatestAuditMergeGainTotal,
        decimal? LatestAuditExpectedTotal,
        string? LatestAuditTertiaryFocus,
        string? LatestAuditQuaternaryFocus,
        long ResetLedgerCount)
    {
        public decimal BasicTotal => BasicValues.Sum();
    }
}
