using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    private static async Task<PetMergePersistenceState>
        ReadPetMergeStateAsync(
            NpgsqlDataSource dataSource,
            int characterId,
            long primaryPetId,
            long deputyPetId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                EXISTS(SELECT 1 FROM public.character_pets WHERE id = @primaryPetId),
                EXISTS(SELECT 1 FROM public.character_pets WHERE id = @deputyPetId),
                COALESCE((SELECT rank FROM public.character_pets WHERE id = @primaryPetId), 0),
                COALESCE((SELECT revision FROM public.character_pets WHERE id = @primaryPetId), 0),
                COALESCE((SELECT completed_pet_merges FROM public.character_pets WHERE id = @primaryPetId), 0),
                ARRAY(SELECT initial_savvy FROM public.character_pet_stat_values WHERE pet_id = @primaryPetId ORDER BY stat_code),
                (SELECT inventory_revision FROM public.character_base WHERE id = @characterId),
                (SELECT COALESCE(sum(stack), 0)::bigint FROM public.character_items WHERE user_id = @characterId AND item_location = 1 AND prop_id = 10103),
                (SELECT count(*) FROM public.character_inventory_ledger WHERE character_id = @characterId AND reason_code = 'pet_merge_material_consumed'),
                (SELECT count(*) FROM public.outbox_events event JOIN public.command_inbox inbox ON inbox.id = event.command_inbox_id WHERE inbox.aggregate_key = 'character:' || @characterId AND inbox.command_family = 'pet_to_pet_merge' AND event.event_type = 'inventory.pet_bag_item_activated'),
                (SELECT count(*) FROM public.pet_operation_audit WHERE user_id_snapshot = @characterId AND operation = 'pet_merge'),
                (SELECT count(*) FROM public.pet_operation_audit WHERE user_id_snapshot = @characterId AND operation = 'pet_merge' AND outcome = 'committed'),
                (SELECT COALESCE(sum((entry->>'quantity')::integer), 0)::bigint FROM public.pet_operation_audit audit CROSS JOIN LATERAL jsonb_array_elements(audit.consumed_items) entry WHERE audit.user_id_snapshot = @characterId AND audit.operation = 'pet_merge'),
                (SELECT count(*) FROM public.command_inbox WHERE aggregate_key = 'character:' || @characterId AND command_family = 'pet_to_pet_merge'),
                (SELECT count(*) FROM public.command_audit WHERE aggregate_key = 'character:' || @characterId AND command_family = 'pet_to_pet_merge'),
                (SELECT count(*) FROM public.outbox_events WHERE aggregate_key = 'character:' || @characterId AND event_type = 'pet.merged'),
                (SELECT count(*) FROM public.pet_durable_command_evidence WHERE aggregate_key = 'character:' || @characterId AND command_family = 'pet_to_pet_merge'),
                COALESCE((SELECT result_contract_version FROM public.command_inbox WHERE aggregate_key = 'character:' || @characterId AND command_family = 'pet_to_pet_merge'), 0);
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("primaryPetId", primaryPetId);
        command.Parameters.AddWithValue("deputyPetId", deputyPetId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Pet Merge persistence state was not returned.");
        }
        return new PetMergePersistenceState(
            primaryPetId, deputyPetId,
            reader.GetBoolean(0), reader.GetBoolean(1),
            reader.GetDecimal(2), reader.GetInt64(3), reader.GetInt32(4),
            reader.GetFieldValue<decimal[]>(5),
            reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
            reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11),
            reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14),
            reader.GetInt64(15), reader.GetInt64(16),
            Convert.ToInt16(reader.GetValue(17)));
    }

    private static bool PetMergeStateEquals(
        PetMergePersistenceState left,
        PetMergePersistenceState right) =>
        left.PrimaryPetId == right.PrimaryPetId &&
        left.DeputyPetId == right.DeputyPetId &&
        left.PrimaryExists == right.PrimaryExists &&
        left.DeputyExists == right.DeputyExists &&
        left.PrimaryRank == right.PrimaryRank &&
        left.PrimaryRevision == right.PrimaryRevision &&
        left.CompletedMerges == right.CompletedMerges &&
        left.InitialSavvy.SequenceEqual(right.InitialSavvy) &&
        left.InventoryRevision == right.InventoryRevision &&
        left.MaterialCount == right.MaterialCount &&
        left.InventoryLedgerCount == right.InventoryLedgerCount &&
        left.InventoryOutboxCount == right.InventoryOutboxCount &&
        left.PetMergeAuditCount == right.PetMergeAuditCount &&
        left.PetMergeCommittedAuditCount ==
            right.PetMergeCommittedAuditCount &&
        left.ConsumedAuditQuantity == right.ConsumedAuditQuantity &&
        left.CommandInboxCount == right.CommandInboxCount &&
        left.CommandAuditCount == right.CommandAuditCount &&
        left.CommandOutboxCount == right.CommandOutboxCount &&
        left.EvidenceViewCount == right.EvidenceViewCount &&
        left.InboxContractVersion == right.InboxContractVersion;

    private sealed record PetMergePersistenceState(
        long PrimaryPetId,
        long DeputyPetId,
        bool PrimaryExists,
        bool DeputyExists,
        decimal PrimaryRank,
        long PrimaryRevision,
        int CompletedMerges,
        decimal[] InitialSavvy,
        long InventoryRevision,
        long MaterialCount,
        long InventoryLedgerCount,
        long InventoryOutboxCount,
        long PetMergeAuditCount,
        long PetMergeCommittedAuditCount,
        long ConsumedAuditQuantity,
        long CommandInboxCount,
        long CommandAuditCount,
        long CommandOutboxCount,
        long EvidenceViewCount,
        short InboxContractVersion);
}
