using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task SetFixturePetBoundAsync(
        NpgsqlDataSource dataSource,
        long petId,
        bool isBound)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET bound = @isBound
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("isBound", isBound);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "pet bind fixture sets bound state exactly");
    }

    private static async Task SetFixturePetMergedAndUnboundAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET bound = false,
                contributes_to_character = true
            WHERE id = @petId
              AND is_carried
              AND is_summoned;
            """);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "pet bind fixture marks the summoned pet owner-merged");
    }

    private static async Task<PetBindDatabaseState> ReadPetBindStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.bound,
                pet.revision,
                pet.is_summoned,
                (
                    to_jsonb(pet) - 'bound' - 'revision' - 'updated_at'
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
                character_row.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events event
                    WHERE event.aggregate_type = 'character_inventory'
                      AND event.aggregate_key = @inventoryKey
                ),
                (
                    SELECT count(*)
                    FROM public.command_audit audit
                    WHERE audit.aggregate_key = @aggregateKey
                      AND audit.command_family = 'pet_bind'
                ),
                (
                    SELECT count(*)
                    FROM public.command_inbox inbox
                    WHERE inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = 'pet_bind'
                ),
                (
                    SELECT COALESCE(sum(inbox.duplicate_count), 0)
                    FROM public.command_inbox inbox
                    WHERE inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = 'pet_bind'
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events event
                    JOIN public.command_inbox inbox
                      ON inbox.id = event.command_inbox_id
                    WHERE inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = 'pet_bind'
                      AND event.consumer_key = 'pet_durable_v1'
                ),
                (
                    SELECT count(*)
                    FROM public.pet_operation_audit audit
                    WHERE audit.user_id_snapshot = @characterId
                      AND audit.operation = 'bind'
                      AND audit.outcome = 'committed'
                ),
                (
                    SELECT count(*)
                    FROM public.pet_operation_audit audit
                    WHERE audit.user_id_snapshot = @characterId
                      AND audit.operation = 'bind'
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
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{characterId}");
        command.Parameters.AddWithValue(
            "inventoryKey",
            $"character:{characterId}:inventory");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException("Pet bind state disappeared.");
        }
        return new PetBindDatabaseState(
            reader.GetBoolean(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetInt64(15));
    }

    private static async Task AssertPetBindAuditEvidenceAsync(
        NpgsqlDataSource dataSource,
        PetDurableReceipt receipt,
        Guid operationId,
        string petContentRevision)
    {
        var auditId = long.TryParse(
            receipt.AuditReference,
            out var value)
            ? value
            : throw new InvalidDataException(
                "Pet bind receipt has an invalid audit reference.");
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                command_audit.command_family = 'pet_bind',
                operation_audit.before_state ->> 'bound' = 'false',
                operation_audit.after_state ->> 'bound' = 'true',
                operation_audit.before_state ->>
                    'pet_content_revision' = @petContentRevision,
                operation_audit.after_state ->>
                    'pet_content_revision' = @petContentRevision,
                operation_audit.before_state ->> 'species_id' =
                    operation_audit.after_state ->> 'species_id',
                operation_audit.before_state ->> 'HasSoulContract' =
                    operation_audit.after_state ->> 'HasSoulContract',
                operation_audit.consumed_items = '[]'::jsonb
            FROM public.command_audit command_audit
            JOIN public.pet_operation_audit operation_audit
              ON operation_audit.user_id_snapshot = @characterId
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
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            Enumerable.Range(0, 8).All(reader.GetBoolean),
            "bind audit proves bound-only mutation, no items, and pinned pet content");
    }

    private sealed record PetBindDatabaseState(
        bool IsBound,
        long PetRevision,
        bool IsSummoned,
        string ImmutablePetJson,
        string StatValuesJson,
        string SkillsJson,
        string CharacterBonusesJson,
        long InventoryRevision,
        long InventoryLedgerCount,
        long InventoryOutboxCount,
        long CommandAuditCount,
        long InboxCount,
        long DuplicateCount,
        long PetOutboxCount,
        long CommittedOperationAuditCount,
        long RejectedOperationAuditCount);
}
