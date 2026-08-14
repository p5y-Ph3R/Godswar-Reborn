using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task<HatchState> ReadHatchStateAsync(
        NpgsqlDataSource dataSource,
        PetFixture fixture,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                (
                    SELECT count(*)
                    FROM public.character_pets
                    WHERE user_id = @characterId
                ),
                (
                    SELECT pet_shed_capacity
                    FROM public.character_base
                    WHERE id = @characterId
                ),
                (
                    SELECT pet_shed_revision
                    FROM public.character_base
                    WHERE id = @characterId
                ),
                (
                    SELECT count(*)
                    FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND slot_index = @eggSlot
                ),
                (
                    SELECT aptitude
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT opened_skill_slots
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT available_skill_slots
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT count(*)
                    FROM public.character_pet_skills
                    WHERE pet_id = @petId
                ),
                (
                    SELECT is_carried
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT is_summoned
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT talent_mask
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT has_owner_merge_talent
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT rank
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT birth_rank
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT hatch_rank_roll
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT hatch_rank_outcome_order
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                (
                    SELECT hatch_rank_content_revision
                    FROM public.character_pets
                    WHERE id = @petId
                ),
                ARRAY(
                    SELECT concat_ws(
                        ':',
                        stat_code,
                        initial_savvy,
                        added_savvy,
                        base_growth_rate,
                        birth_initial_savvy,
                        rarity_added_savvy
                    )
                    FROM public.character_pet_stat_values
                    WHERE pet_id = @petId
                    ORDER BY stat_code
                );
            """);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue("eggSlot", (short)fixture.EggSlot);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Pet hatch state was not returned.");
        }
        return new HatchState(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt16(4),
            reader.GetInt16(5),
            reader.GetInt16(6),
            reader.GetInt64(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.GetInt16(10),
            reader.GetBoolean(11),
            reader.GetDecimal(12),
            reader.GetDecimal(13),
            reader.GetInt16(14),
            reader.GetInt16(15),
            reader.GetString(16),
            reader.GetFieldValue<string[]>(17));
    }

    private static async Task<LevelState> ReadLevelStateAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                level,
                experience,
                revision,
                NOT EXISTS (
                    SELECT 1
                    FROM public.character_pet_stat_values stat
                    WHERE stat.pet_id = pet.id
                      AND stat.added_savvy IS DISTINCT FROM
                            (
                                stat.base_growth_rate +
                                stat.growth_acceleration
                            ) * pet.level
                ),
                ARRAY(
                    SELECT concat_ws(
                        ':',
                        stat_code,
                        initial_savvy,
                        revision
                    )
                    FROM public.character_pet_stat_values
                    WHERE pet_id = pet.id
                    ORDER BY stat_code
                )
            FROM public.character_pets pet
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Pet level state disappeared.");
        }
        return new LevelState(
            reader.GetInt16(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetBoolean(3),
            reader.GetFieldValue<string[]>(4));
    }

    private static async Task<EvidenceState> ReadEvidenceAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long[] auditIds)
    {
        var aggregateKey = $"character:{characterId}";
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                stream.current_version,
                (
                    SELECT count(*)
                    FROM public.command_audit
                    WHERE id = ANY(@auditIds)
                ),
                (
                    SELECT count(*)
                    FROM public.command_inbox
                    WHERE audit_id = ANY(@auditIds)
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                      AND command_inbox_id IN (
                          SELECT id
                          FROM public.command_inbox
                          WHERE audit_id = ANY(@auditIds)
                      )
                ),
                position.current_version,
                (
                    SELECT coalesce(sum(duplicate_count), 0)
                    FROM public.command_inbox
                    WHERE audit_id = ANY(@auditIds)
                ),
                (
                    SELECT coalesce(sum(request_conflict_count), 0)
                    FROM public.command_inbox
                    WHERE audit_id = ANY(@auditIds)
                ),
                ARRAY(
                    SELECT aggregate_version
                    FROM public.outbox_events
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                      AND command_inbox_id IN (
                          SELECT id
                          FROM public.command_inbox
                          WHERE audit_id = ANY(@auditIds)
                      )
                    ORDER BY aggregate_version
                )
            FROM public.pet_durable_stream_versions stream
            JOIN public.outbox_consumer_positions position
              ON position.consumer_key = 'pet_durable_v1'
             AND position.aggregate_type = 'character_pet_value'
             AND position.aggregate_key = @aggregateKey
            WHERE stream.character_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue("auditIds", auditIds);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Pet durable evidence disappeared.");
        }
        return new EvidenceState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetFieldValue<long[]>(7));
    }

    private static async Task<InventoryState> ReadInventoryStateAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        var aggregateKey = $"character:{characterId}:inventory";
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                character_row.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.character_inventory_ledger
                    WHERE character_id = @characterId
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events
                    WHERE consumer_key = 'inventory_projection_v1'
                      AND aggregate_type = 'character_inventory'
                      AND aggregate_key = @aggregateKey
                      AND event_type =
                          'inventory.pet_bag_item_activated'
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events
                    WHERE consumer_key = 'inventory_projection_v1'
                      AND aggregate_type = 'character_inventory'
                      AND aggregate_key = @aggregateKey
                      AND event_type =
                          'inventory.pet_bag_item_activated'
                      AND delivered_at IS NOT NULL
                ),
                COALESCE((
                    SELECT current_version
                    FROM public.outbox_consumer_positions
                    WHERE consumer_key = 'inventory_projection_v1'
                      AND aggregate_type = 'character_inventory'
                      AND aggregate_key = @aggregateKey
                ), -1),
                reconciliation.is_reconciled,
                ARRAY(
                    SELECT aggregate_version
                    FROM public.outbox_events
                    WHERE consumer_key = 'inventory_projection_v1'
                      AND aggregate_type = 'character_inventory'
                      AND aggregate_key = @aggregateKey
                      AND event_type =
                          'inventory.pet_bag_item_activated'
                    ORDER BY aggregate_version
                )
            FROM public.character_base character_row
            JOIN public.character_inventory_reconciliation reconciliation
              ON reconciliation.character_id = character_row.id
            WHERE character_row.id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Pet bag inventory evidence disappeared.");
        }

        return new InventoryState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetBoolean(5),
            reader.GetFieldValue<long[]>(6));
    }

    private sealed record HatchState(
        long PetCount,
        short PetShedCapacity,
        long PetShedRevision,
        long EggCount,
        short Aptitude,
        short OpenedSkillSlots,
        short AvailableSkillSlots,
        long LearnedSkillCount,
        bool IsCarried,
        bool IsSummoned,
        short TalentMask,
        bool HasOwnerMergeTalent,
        decimal Rank,
        decimal BirthRank,
        short HatchRankRoll,
        short HatchRankOutcomeOrder,
        string HatchRankContentRevision,
        string[] StatValues);

    private sealed record LevelState(
        short Level,
        long Experience,
        long Revision,
        bool HasExactScaledAdded,
        string[] StatValues);

    private sealed record EvidenceState(
        long StreamVersion,
        long AuditCount,
        long InboxCount,
        long OutboxCount,
        long PositionVersion,
        long DuplicateCount,
        long ConflictCount,
        long[] OutboxVersions);

    private sealed record InventoryState(
        long InventoryRevision,
        long LedgerEntryCount,
        long OutboxCount,
        long DeliveredOutboxCount,
        long PositionVersion,
        bool IsReconciled,
        long[] OutboxVersions);
}
