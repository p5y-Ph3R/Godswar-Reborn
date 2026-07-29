using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private async Task<InventoryMutation> UpdateTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolyStoneCommandContext context,
        LockedItem target,
        CompactItemEntry after,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET holy_socket_count = @socketCount,
                holy_socket1_effect_id = @socket1Effect,
                holy_socket1_level = @socket1Level,
                holy_socket2_effect_id = @socket2Effect,
                holy_socket2_level = @socket2Level,
                holy_socket3_effect_id = @socket3Effect,
                holy_socket3_level = @socket3Level,
                holy_socket4_effect_id = @socket4Effect,
                holy_socket4_level = @socket4Level,
                holy_socket5_effect_id = NULL,
                holy_socket5_level = NULL,
                holy_socket6_effect_id = NULL,
                holy_socket6_level = NULL,
                updated_at = now()
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = @itemLocation
              AND slot_index = @slotIndex
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            target.ItemInstanceId);
        command.Parameters.AddWithValue(
            "characterId",
            context.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "itemLocation",
            target.Location);
        command.Parameters.AddWithValue("slotIndex", target.Slot);
        command.Parameters.AddWithValue(
            "socketCount",
            after.SocketCount);
        AddNullableSmallint(
            command,
            "socket1Effect",
            after.Socket1EffectId);
        AddNullableSmallint(
            command,
            "socket1Level",
            after.Socket1Level);
        AddNullableSmallint(
            command,
            "socket2Effect",
            after.Socket2EffectId);
        AddNullableSmallint(
            command,
            "socket2Level",
            after.Socket2Level);
        AddNullableSmallint(
            command,
            "socket3Effect",
            after.Socket3EffectId);
        AddNullableSmallint(
            command,
            "socket3Level",
            after.Socket3Level);
        AddNullableSmallint(
            command,
            "socket4Effect",
            after.Socket4EffectId);
        AddNullableSmallint(
            command,
            "socket4Level",
            after.Socket4Level);
        var afterState =
            await command.ExecuteScalarAsync(cancellationToken)
                as string ??
            throw new InvalidDataException(
                "The locked Holy Stone target was not updated exactly once.");
        return new InventoryMutation(
            target.ItemInstanceId,
            "update",
            target.BeforeState,
            afterState);
    }

    private async Task<InventoryMutation> ConsumeStoneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolyStoneCommandContext context,
        LockedItem stone,
        CompactItemEntry after,
        CancellationToken cancellationToken)
    {
        if (after.IsEmpty)
        {
            await using var delete = CreateCommand(
                """
                WITH deleted AS (
                    DELETE FROM public.character_items
                    WHERE id = @itemInstanceId
                      AND user_id = @characterId
                      AND item_location = 1
                      AND slot_index = @slotIndex
                      AND stack = 1
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
                    'holy-stone-mount',
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
            delete.Parameters.AddWithValue(
                "itemInstanceId",
                stone.ItemInstanceId);
            delete.Parameters.AddWithValue(
                "characterId",
                context.Subject.CharacterId);
            delete.Parameters.AddWithValue("slotIndex", stone.Slot);
            if (await delete.ExecuteNonQueryAsync(
                    cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The consumed Holy Stone stack was not deleted " +
                    "exactly once.");
            }
            return new InventoryMutation(
                stone.ItemInstanceId,
                "delete",
                stone.BeforeState,
                null);
        }

        await using var update = CreateCommand(
            """
            UPDATE public.character_items
            SET stack = @stackAfter,
                updated_at = now()
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @slotIndex
              AND stack = @stackBefore
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue(
            "stackAfter",
            after.Stack);
        update.Parameters.AddWithValue(
            "stackBefore",
            stone.Item.Stack);
        update.Parameters.AddWithValue(
            "itemInstanceId",
            stone.ItemInstanceId);
        update.Parameters.AddWithValue(
            "characterId",
            context.Subject.CharacterId);
        update.Parameters.AddWithValue("slotIndex", stone.Slot);
        var afterState =
            await update.ExecuteScalarAsync(cancellationToken)
                as string ??
            throw new InvalidDataException(
                "The consumed Holy Stone stack was not decremented " +
                "exactly once.");
        return new InventoryMutation(
            stone.ItemInstanceId,
            "update",
            stone.BeforeState,
            afterState);
    }

    private async Task<long> ReserveItemInstanceIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT nextval(
                pg_get_serial_sequence(
                    'public.character_items',
                    'id'));
            """,
            connection,
            transaction);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long itemId && itemId > 0
            ? itemId
            : throw new InvalidDataException(
                "No Holy Stone output item identity was reserved.");
    }

    private async Task<InventoryMutation> InsertOutputAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolyStoneCommandContext context,
        HolyStonePlan plan,
        long itemInstanceId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_items (
                id,
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack,
                item_exp,
                holy_suit_code,
                holy_socket_count
            )
            VALUES (
                @itemInstanceId,
                @characterId,
                1,
                @slotIndex,
                @propId,
                @quality,
                @grade,
                @bound,
                @stack,
                @itemExp,
                @holySuitCode,
                @socketCount
            )
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            itemInstanceId);
        command.Parameters.AddWithValue(
            "characterId",
            context.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "slotIndex",
            checked((short)plan.OutputKitBagSlot));
        command.Parameters.AddWithValue(
            "propId",
            checked((int)plan.OutputItem.Id));
        command.Parameters.AddWithValue(
            "quality",
            plan.OutputItem.Quality);
        command.Parameters.AddWithValue(
            "grade",
            plan.OutputItem.Grade);
        command.Parameters.AddWithValue(
            "bound",
            plan.OutputItem.Bound);
        command.Parameters.AddWithValue(
            "stack",
            plan.OutputItem.Stack);
        command.Parameters.AddWithValue(
            "itemExp",
            plan.OutputItem.Exp);
        command.Parameters.AddWithValue(
            "holySuitCode",
            plan.OutputItem.HolySuitCode);
        command.Parameters.AddWithValue(
            "socketCount",
            plan.OutputItem.SocketCount);
        var afterState =
            await command.ExecuteScalarAsync(cancellationToken)
                as string ??
            throw new InvalidDataException(
                "The removed Holy Stone output was not inserted " +
                "exactly once.");
        return new InventoryMutation(
            itemInstanceId,
            "add",
            null,
            afterState);
    }
}
