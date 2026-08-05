using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresGearEnhancementCommandExecutor
{
    private async Task<InventoryMutation> UpdateItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        GearEnhancementCommandItemRole role,
        LockedInventoryItem locked,
        CompactItemEntry item,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET prop_id = @itemId,
                attribute1 = @attribute1,
                attribute2 = @attribute2,
                attribute3 = @attribute3,
                attribute4 = @attribute4,
                attribute5 = @attribute5,
                class_attribute1 = @classAttribute1,
                class_attribute2 = @classAttribute2,
                elemental_attribute1 = @elementalAttribute1,
                elemental_attribute2 = @elementalAttribute2,
                attribute_level1 = @attributeLevel1,
                attribute_level2 = @attributeLevel2,
                attribute_level3 = @attributeLevel3,
                attribute_level4 = @attributeLevel4,
                attribute_level5 = @attributeLevel5,
                item_quality = @itemQuality,
                item_grade = @itemGrade,
                bound = @bound,
                stack = @stack,
                item_exp = @itemExp,
                holy_suit_code = @holySuitCode,
                holy_socket_count = @holySocketCount,
                holy_socket1_effect_id = @holySocket1EffectId,
                holy_socket1_level = @holySocket1Level,
                holy_socket2_effect_id = @holySocket2EffectId,
                holy_socket2_level = @holySocket2Level,
                holy_socket3_effect_id = @holySocket3EffectId,
                holy_socket3_level = @holySocket3Level,
                holy_socket4_effect_id = @holySocket4EffectId,
                holy_socket4_level = @holySocket4Level,
                holy_socket5_effect_id = @holySocket5EffectId,
                holy_socket5_level = @holySocket5Level,
                holy_socket6_effect_id = @holySocket6EffectId,
                holy_socket6_level = @holySocket6Level,
                holy_socket1_value = @holySocket1Value,
                holy_socket2_value = @holySocket2Value,
                holy_socket3_value = @holySocket3Value,
                holy_socket4_value = @holySocket4Value,
                updated_at = now()
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @slotIndex
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            locked.ItemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", locked.Slot);
        AddItemParameters(command, item);
        var afterState =
            await command.ExecuteScalarAsync(cancellationToken)
                as string ??
            throw new InvalidDataException(
                "The locked Gear Enhancement item was not updated " +
                "exactly once.");
        return new InventoryMutation(
            role,
            locked.ItemInstanceId,
            "update",
            locked.BeforeState,
            afterState);
    }

    private async Task<InventoryMutation> DeleteItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        GearEnhancementCommandItemRole role,
        LockedInventoryItem locked,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            WITH deleted AS (
                DELETE FROM public.character_items
                WHERE id = @itemInstanceId
                  AND user_id = @characterId
                  AND item_location = 1
                  AND slot_index = @slotIndex
                RETURNING *
            )
            INSERT INTO public.character_item_audit (
                source,
                action,
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                item_exp,
                old_item
            )
            SELECT
                'gear-enhancement',
                'delete',
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                item_exp,
                to_jsonb(deleted)
            FROM deleted;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            locked.ItemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", locked.Slot);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The locked Gear Enhancement material was not deleted " +
                "exactly once.");
        }

        return new InventoryMutation(
            role,
            locked.ItemInstanceId,
            "delete",
            locked.BeforeState,
            AfterState: null);
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CommandFamily family,
        string aggregateKey,
        long inventoryRevision,
        Guid eventId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_events (
                event_id,
                command_inbox_id,
                consumer_key,
                aggregate_type,
                aggregate_key,
                aggregate_version,
                event_type,
                contract_version,
                ordering_policy,
                payload,
                max_attempts
            )
            VALUES (
                @eventId,
                @inboxId,
                @consumerKey,
                @aggregateType,
                @aggregateKey,
                @aggregateVersion,
                @eventType,
                @contractVersion,
                @orderingPolicy,
                @payload,
                @maxAttempts
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "consumerKey",
            GearEnhancementPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            GearEnhancementPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "eventType",
            GearEnhancementPersistenceCodec.EventType(family));
        command.Parameters.AddWithValue(
            "contractVersion",
            GearEnhancementPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            GearEnhancementPersistenceCodec.OrderingPolicy);
        command.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue(
            "maxAttempts",
            _maximumOutboxAttempts);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Gear Enhancement outbox insert was not exact.");
        }
    }
}
