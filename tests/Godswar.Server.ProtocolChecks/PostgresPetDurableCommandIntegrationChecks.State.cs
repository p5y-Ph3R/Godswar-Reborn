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
                    SELECT count(*)
                    FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND slot_index = @eggSlot
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
            reader.GetInt64(1),
            reader.GetFieldValue<string[]>(2));
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
            reader.GetFieldValue<string[]>(3));
    }

    private static async Task<EvidenceState> ReadEvidenceAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        var aggregateKey = $"character:{characterId}";
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                stream.current_version,
                (
                    SELECT count(*)
                    FROM public.command_audit
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                ),
                (
                    SELECT count(*)
                    FROM public.command_inbox
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                ),
                position.current_version,
                (
                    SELECT coalesce(sum(duplicate_count), 0)
                    FROM public.command_inbox
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                ),
                (
                    SELECT coalesce(sum(request_conflict_count), 0)
                    FROM public.command_inbox
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                ),
                ARRAY(
                    SELECT aggregate_version
                    FROM public.outbox_events
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
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
        long EggCount,
        string[] StatValues);

    private sealed record LevelState(
        short Level,
        long Experience,
        long Revision,
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
