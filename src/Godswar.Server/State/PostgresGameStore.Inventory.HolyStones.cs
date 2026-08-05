using Godswar.Server.Game;
using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private static async Task ApplyHolyStoneSlotMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        HolyStoneSlotMutation mutation,
        CancellationToken cancellationToken)
    {
        var itemLocation = mutation.IsKitBag ? ItemLocationKitBag : ItemLocationEquipment;
        if (mutation.Before.IsEmpty)
        {
            if (mutation.After.IsEmpty)
            {
                throw new InvalidOperationException("Holy-stone plan contains an empty-to-empty mutation.");
            }

            await InsertCharacterItemIntoEmptySlotAsync(
                connection,
                transaction,
                characterId,
                itemLocation,
                mutation.Slot,
                mutation.After,
                cancellationToken);
            return;
        }

        if (mutation.After.IsEmpty)
        {
            await DeleteCharacterItemSlotAsync(
                connection,
                transaction,
                characterId,
                itemLocation,
                mutation.Slot,
                "holy-stone-consume",
                cancellationToken);
            return;
        }

        if (IsSingleHolyStoneStackConsumption(mutation))
        {
            await UpdateHolyStoneMaterialStackAsync(
                connection,
                transaction,
                characterId,
                mutation.Slot,
                mutation.Before,
                mutation.After,
                cancellationToken);
            return;
        }

        if (WithoutHolyStoneSocketState(mutation.Before) != WithoutHolyStoneSocketState(mutation.After))
        {
            throw new InvalidOperationException(
                $"Holy-stone plan attempted to change non-socket item data at location {itemLocation}, slot {mutation.Slot}.");
        }

        await UpdateHolyStoneSocketStateAsync(
            connection,
            transaction,
            characterId,
            itemLocation,
            mutation.Slot,
            mutation.Before.Id,
            mutation.After,
            cancellationToken);
    }

    private static bool IsSingleHolyStoneStackConsumption(
        HolyStoneSlotMutation mutation) =>
        mutation.IsKitBag &&
        mutation.Before.Stack > 1 &&
        mutation.After.Stack == mutation.Before.Stack - 1 &&
        WithoutStack(mutation.Before) == WithoutStack(mutation.After);

    private static CompactItemEntry WithoutStack(
        CompactItemEntry item) =>
        item with { Stack = 0 };

    private static async Task UpdateHolyStoneMaterialStackAsync(
        DbConnection connection,
        DbTransaction transaction,
        int characterId,
        int slotIndex,
        CompactItemEntry before,
        CompactItemEntry after,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE character_items
            SET stack = @stackAfter,
                updated_at = now()
            WHERE user_id = @characterId
              AND item_location = @itemLocation
              AND slot_index = @slotIndex
              AND prop_id = @expectedItemId
              AND stack = @stackBefore;
            """;
        AddHolyStoneStackParameter(command, "stackAfter", after.Stack);
        AddHolyStoneStackParameter(command, "stackBefore", before.Stack);
        AddHolyStoneStackParameter(command, "characterId", characterId);
        AddHolyStoneStackParameter(
            command,
            "itemLocation",
            ItemLocationKitBag);
        AddHolyStoneStackParameter(
            command,
            "slotIndex",
            checked((short)slotIndex));
        AddHolyStoneStackParameter(
            command,
            "expectedItemId",
            checked((int)before.Id));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "Holy-stone material stack was not decremented exactly once.");
        }
    }

    private static void AddHolyStoneStackParameter(
        DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static CompactItemEntry WithoutHolyStoneSocketState(CompactItemEntry item)
    {
        return item with
        {
            SocketCount = 0,
            Socket1EffectId = null,
            Socket1Level = null,
            Socket2EffectId = null,
            Socket2Level = null,
            Socket3EffectId = null,
            Socket3Level = null,
            Socket4EffectId = null,
            Socket4Level = null,
            Socket1Value = null,
            Socket2Value = null,
            Socket3Value = null,
            Socket4Value = null,
            Socket5EffectId = null,
            Socket5Level = null,
            Socket6EffectId = null,
            Socket6Level = null
        };
    }

    private static async Task UpdateHolyStoneSocketStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short itemLocation,
        int slotIndex,
        uint expectedItemId,
        CompactItemEntry item,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE character_items
            SET holy_socket_count = @holySocketCount,
                holy_socket1_effect_id = @holySocket1EffectId,
                holy_socket1_level = @holySocket1Level,
                holy_socket2_effect_id = @holySocket2EffectId,
                holy_socket2_level = @holySocket2Level,
                holy_socket3_effect_id = @holySocket3EffectId,
                holy_socket3_level = @holySocket3Level,
                holy_socket4_effect_id = @holySocket4EffectId,
                holy_socket4_level = @holySocket4Level,
                holy_socket1_value = @holySocket1Value,
                holy_socket2_value = @holySocket2Value,
                holy_socket3_value = @holySocket3Value,
                holy_socket4_value = @holySocket4Value,
                holy_socket5_effect_id = @holySocket5EffectId,
                holy_socket5_level = @holySocket5Level,
                holy_socket6_effect_id = @holySocket6EffectId,
                holy_socket6_level = @holySocket6Level,
                updated_at = now()
            WHERE user_id = @characterId
              AND item_location = @itemLocation
              AND slot_index = @slotIndex
              AND prop_id = @expectedItemId;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemLocation", itemLocation);
        command.Parameters.AddWithValue("slotIndex", (short)slotIndex);
        command.Parameters.AddWithValue("expectedItemId", checked((int)expectedItemId));
        AddHolyStoneParameters(command, item);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Holy-stone target changed at location {itemLocation}, slot {slotIndex}.");
        }
    }

    private static async Task ReplaceCharacterItemsFromCompactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        string? equipment,
        string? kitBag,
        CancellationToken cancellationToken)
    {
        if (equipment is not null)
        {
            await ApplyCharacterItemsFromCompactAsync(
                connection,
                transaction,
                characterId,
                ItemLocationEquipment,
                equipment,
                EquipmentProjectionSlots,
                "compact-equipment",
                cancellationToken);
        }

        if (kitBag is not null)
        {
            await ApplyCharacterItemsFromCompactAsync(
                connection,
                transaction,
                characterId,
                ItemLocationKitBag,
                kitBag,
                KitBagProjectionSlots,
                "compact-kitbag",
                cancellationToken);
        }
    }

    private static async Task ApplyCharacterItemsFromCompactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short itemLocation,
        string compact,
        int maxSlots,
        string source,
        CancellationToken cancellationToken)
    {
        foreach (var (slot, item) in EnumerateCompactSlots(compact, maxSlots))
        {
            if (item.IsEmpty)
            {
                await DeleteCharacterItemSlotAsync(
                    connection,
                    transaction,
                    characterId,
                    itemLocation,
                    slot,
                    source,
                    cancellationToken);
                continue;
            }

            await InsertCharacterItemAsync(
                connection,
                transaction,
                characterId,
                itemLocation,
                slot,
                item,
                cancellationToken);
        }
    }

}
