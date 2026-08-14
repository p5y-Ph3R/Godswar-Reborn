using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task<long> SeedMagicJadeAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @slot, @propId,
                0, 1, 0, 2, 0, 0
            )
            RETURNING id;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", MagicJadeSlot);
        command.Parameters.AddWithValue("propId", CupidMagicJadeId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync() ??
            throw new InvalidDataException(
                "Magic Jade fixture insert returned no item."));
    }

    private static async Task<PetAppearanceDatabaseState>
        ReadPetAppearanceStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.species_id,
                pet.revision,
                pet.bound,
                pet.is_summoned,
                (
                    to_jsonb(pet) - 'species_id' - 'revision' -
                    'updated_at'
                )::text,
                COALESCE((
                    SELECT jsonb_agg(
                        to_jsonb(stat) ORDER BY stat.stat_code)
                    FROM public.character_pet_stat_values stat
                    WHERE stat.pet_id = pet.id
                ), '[]'::jsonb)::text,
                COALESCE((
                    SELECT jsonb_agg(
                        to_jsonb(skill)
                        ORDER BY skill.slot_index, skill.skill_id)
                    FROM public.character_pet_skills skill
                    WHERE skill.pet_id = pet.id
                ), '[]'::jsonb)::text,
                COALESCE((
                    SELECT jsonb_agg(
                        to_jsonb(bonus) ORDER BY bonus.effect_code)
                    FROM public.character_pet_character_bonuses bonus
                    WHERE bonus.pet_id = pet.id
                ), '[]'::jsonb)::text,
                COALESCE((
                    SELECT item.stack
                    FROM public.character_items item
                    WHERE item.user_id = @characterId
                      AND item.item_location = 1
                      AND item.slot_index = @slot
                      AND item.prop_id = @propId
                ), 0),
                character_row.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.command_audit audit
                    WHERE audit.aggregate_key = @aggregateKey
                      AND audit.command_family = @family
                ),
                (
                    SELECT count(*)
                    FROM public.command_inbox inbox
                    WHERE inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @family
                ),
                (
                    SELECT COALESCE(sum(inbox.duplicate_count), 0)
                    FROM public.command_inbox inbox
                    WHERE inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @family
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events event
                    JOIN public.command_inbox inbox
                      ON inbox.id = event.command_inbox_id
                    WHERE inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @family
                      AND event.consumer_key = 'pet_durable_v1'
                ),
                (
                    SELECT count(*)
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.reason_code =
                          'pet_magic_jade_consumed'
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events event
                    JOIN public.command_inbox inbox
                      ON inbox.id = event.command_inbox_id
                    WHERE inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @family
                      AND event.consumer_key =
                          'inventory_projection_v1'
                ),
                (
                    SELECT count(*)
                    FROM public.pet_operation_audit audit
                    WHERE audit.user_id_snapshot = @characterId
                      AND audit.operation = 'change_appearance'
                      AND audit.outcome = 'committed'
                ),
                (
                    SELECT count(*)
                    FROM public.pet_operation_audit audit
                    WHERE audit.user_id_snapshot = @characterId
                      AND audit.operation = 'change_appearance'
                      AND audit.outcome = 'rejected'
                )
            FROM public.character_pets pet
            JOIN public.character_base character_row
              ON character_row.id = pet.user_id
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("slot", MagicJadeSlot);
        command.Parameters.AddWithValue("propId", CupidMagicJadeId);
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{characterId}");
        command.Parameters.AddWithValue("family", "pet_appearance_change");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Magic Jade pet state disappeared.");
        }
        return new PetAppearanceDatabaseState(
            reader.GetInt16(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt16(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetInt64(15),
            reader.GetInt64(16),
            reader.GetInt64(17));
    }

    private static async Task AssertPetAppearanceAuditEvidenceAsync(
        NpgsqlDataSource dataSource,
        PetDurableReceipt receipt,
        Guid operationId,
        long jadeInstanceId,
        string petContentRevision,
        string itemContentRevision)
    {
        var auditId = long.TryParse(
            receipt.AuditReference,
            out var value)
            ? value
            : throw new InvalidDataException(
                "Appearance receipt has an invalid audit reference.");
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                command_audit.detail_payload #>>
                    '{appearance_change,pet_content_revision}' =
                    @petContentRevision,
                command_audit.detail_payload #>>
                    '{appearance_change,item_content_revision}' =
                    @itemContentRevision,
                (command_audit.detail_payload #>>
                    '{appearance_change,magic_jade_item_instance_id}')::bigint =
                    @jadeInstanceId,
                operation_audit.before_state ->> 'species_id' = '1',
                operation_audit.after_state ->> 'species_id' = '45',
                operation_audit.consumed_items @>
                    jsonb_build_array(jsonb_build_object(
                        'item_id', @propId,
                        'item_instance_id', @jadeInstanceId,
                        'quantity', 1,
                        'kit_bag_slot', @slot))
            FROM public.command_audit command_audit
            JOIN public.pet_operation_audit operation_audit
              ON operation_audit.user_id_snapshot =
                    @characterId
             AND operation_audit.request_id = @operationId
            WHERE command_audit.id = @auditId;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            receipt.CharacterId);
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue("operationId", operationId);
        command.Parameters.AddWithValue(
            "petContentRevision",
            petContentRevision);
        command.Parameters.AddWithValue(
            "itemContentRevision",
            itemContentRevision);
        command.Parameters.AddWithValue("jadeInstanceId", jadeInstanceId);
        command.Parameters.AddWithValue("propId", CupidMagicJadeId);
        command.Parameters.AddWithValue("slot", (int)MagicJadeSlot);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            Enumerable.Range(0, 6).All(reader.GetBoolean),
            "Magic Jade command/audit evidence retains species, selected item, slot, quantity, and content revisions");
    }

    private sealed record PetAppearanceDatabaseState(
        short SpeciesId,
        long PetRevision,
        bool IsBound,
        bool IsSummoned,
        string ImmutablePetJson,
        string StatValuesJson,
        string SkillsJson,
        string CharacterBonusesJson,
        short JadeStack,
        long InventoryRevision,
        long CommandAuditCount,
        long InboxCount,
        long DuplicateCount,
        long PetOutboxCount,
        long InventoryLedgerCount,
        long InventoryOutboxCount,
        long CommittedOperationAuditCount,
        long RejectedOperationAuditCount);
}
