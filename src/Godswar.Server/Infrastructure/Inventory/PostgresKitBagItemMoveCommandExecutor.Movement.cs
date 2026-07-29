using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresKitBagItemMoveCommandExecutor
{
    private async Task<MovementStates> MoveItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedKitBagSlots slots,
        int sourceSlot,
        int destinationSlot,
        CancellationToken cancellationToken)
    {
        var source = slots.Source ??
            throw new InvalidDataException(
                "The movement source item is missing.");
        await InsertCompatibilityAuditAsync(
            connection,
            transaction,
            characterId,
            source,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage
                .CompatibilityAuditInserted,
            0,
            cancellationToken);

        if (slots.Destination is null)
        {
            await UpdateItemPositionAsync(
                connection,
                transaction,
                characterId,
                source,
                sourceLocation: 1,
                sourceSlot,
                destinationLocation: 1,
                destinationSlot,
                cancellationToken);
            await ReachAsync(
                PostgresKitBagItemMoveCommandStage
                    .SourceMovedToDestinationSlot,
                0,
                cancellationToken);
            var sourceAfter = await ReadFullItemStateAsync(
                connection,
                transaction,
                characterId,
                source.ItemInstanceId,
                destinationSlot,
                cancellationToken);
            return new MovementStates(sourceAfter, null);
        }

        var destination = slots.Destination;
        await InsertCompatibilityAuditAsync(
            connection,
            transaction,
            characterId,
            destination,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage
                .CompatibilityAuditInserted,
            1,
            cancellationToken);
        var temporarySlot = await FindPrivateTemporarySlotAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        await UpdateItemPositionAsync(
            connection,
            transaction,
            characterId,
            source,
            sourceLocation: 1,
            sourceSlot,
            destinationLocation: 2,
            temporarySlot,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage
                .SourceMovedToTemporarySlot,
            0,
            cancellationToken);
        await UpdateItemPositionAsync(
            connection,
            transaction,
            characterId,
            destination,
            sourceLocation: 1,
            destinationSlot,
            destinationLocation: 1,
            sourceSlot,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage
                .DestinationMovedToSourceSlot,
            1,
            cancellationToken);
        await UpdateItemPositionAsync(
            connection,
            transaction,
            characterId,
            source,
            sourceLocation: 2,
            temporarySlot,
            destinationLocation: 1,
            destinationSlot,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage
                .SourceMovedToDestinationSlot,
            0,
            cancellationToken);

        var finalSource = await ReadFullItemStateAsync(
            connection,
            transaction,
            characterId,
            source.ItemInstanceId,
            destinationSlot,
            cancellationToken);
        var finalDestination = await ReadFullItemStateAsync(
            connection,
            transaction,
            characterId,
            destination.ItemInstanceId,
            sourceSlot,
            cancellationToken);
        return new MovementStates(finalSource, finalDestination);
    }

    private async Task UpdateItemPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedKitBagItem item,
        short sourceLocation,
        int sourceSlot,
        short destinationLocation,
        int destinationSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET item_location = @destinationLocation,
                slot_index = @destinationSlot,
                updated_at = now()
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = @sourceLocation
              AND slot_index = @sourceSlot;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "destinationLocation",
            destinationLocation);
        command.Parameters.AddWithValue(
            "destinationSlot",
            checked((short)destinationSlot));
        command.Parameters.AddWithValue(
            "itemInstanceId",
            item.ItemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "sourceLocation",
            sourceLocation);
        command.Parameters.AddWithValue(
            "sourceSlot",
            checked((short)sourceSlot));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "A locked kit-bag item did not move exactly once.");
        }
    }

    private async Task InsertCompatibilityAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedKitBagItem item,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
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
            VALUES (
                'client-bag-move',
                'move',
                @characterId,
                1,
                @slotIndex,
                @propId,
                @itemQuality,
                @itemGrade,
                @itemExp,
                @oldItem
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "slotIndex",
            item.SlotIndex);
        command.Parameters.AddWithValue(
            "propId",
            checked((int)item.Item.Id));
        command.Parameters.AddWithValue(
            "itemQuality",
            item.Item.Quality);
        command.Parameters.AddWithValue(
            "itemGrade",
            item.Item.Grade);
        command.Parameters.AddWithValue("itemExp", item.Item.Exp);
        command.Parameters.Add(
            "oldItem",
            NpgsqlDbType.Jsonb).Value = item.BeforeState;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The kit-bag move compatibility audit was not exact.");
        }
    }

    private async Task<string> ReadFullItemStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long itemInstanceId,
        int expectedSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT to_jsonb(character_items)::text
            FROM public.character_items
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @expectedSlot;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            itemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "expectedSlot",
            checked((short)expectedSlot));
        return await command.ExecuteScalarAsync(cancellationToken)
            as string ??
            throw new InvalidDataException(
                "The moved item has no authoritative final state.");
    }

    private sealed record MovementStates(
        string SourceAfterState,
        string? DestinationAfterState);
}
