using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresEquipmentBagTransferCommandExecutor
{
    private async Task<string> MoveItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedItem source,
        EquipmentBagTransferResultStatus status,
        int equipmentSlot,
        int kitBagSlot,
        CancellationToken cancellationToken)
    {
        var equipped =
            status == EquipmentBagTransferResultStatus.Equipped;
        var sourceLocation = checked((short)(equipped ? 1 : 0));
        var sourceSlot = equipped ? kitBagSlot : equipmentSlot;
        var destinationLocation =
            checked((short)(equipped ? 0 : 1));
        var destinationSlot = equipped ? equipmentSlot : kitBagSlot;
        if (source.Location != sourceLocation ||
            source.SlotIndex != sourceSlot)
        {
            throw new InvalidDataException(
                "The transfer source does not match its direction.");
        }

        await InsertCompatibilityAuditAsync(
            connection,
            transaction,
            characterId,
            source,
            status,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentBagTransferCommandStage
                .CompatibilityAuditInserted,
            0,
            cancellationToken);
        await UpdateItemPositionAsync(
            connection,
            transaction,
            characterId,
            source,
            sourceLocation,
            sourceSlot,
            destinationLocation,
            destinationSlot,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentBagTransferCommandStage.ItemMoved,
            0,
            cancellationToken);
        return await ReadFullItemStateAsync(
            connection,
            transaction,
            characterId,
            source.ItemInstanceId,
            destinationLocation,
            destinationSlot,
            cancellationToken);
    }

    private async Task UpdateItemPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedItem item,
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
                "The locked equipment transfer item did not move " +
                "exactly once.");
        }
    }

    private async Task InsertCompatibilityAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedItem item,
        EquipmentBagTransferResultStatus status,
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
                'client-equipment-bag-transfer',
                @action,
                @characterId,
                @itemLocation,
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
        command.Parameters.AddWithValue(
            "action",
            status == EquipmentBagTransferResultStatus.Equipped
                ? "equip"
                : "unequip");
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "itemLocation",
            item.Location);
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
                "The equipment transfer compatibility audit was " +
                "not exact.");
        }
    }

    private async Task<string> ReadFullItemStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long itemInstanceId,
        short expectedLocation,
        int expectedSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT to_jsonb(character_items)::text
            FROM public.character_items
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = @expectedLocation
              AND slot_index = @expectedSlot;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            itemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "expectedLocation",
            expectedLocation);
        command.Parameters.AddWithValue(
            "expectedSlot",
            checked((short)expectedSlot));
        return await command.ExecuteScalarAsync(cancellationToken)
            as string ??
            throw new InvalidDataException(
                "The transferred item has no authoritative final " +
                "state.");
    }
}
