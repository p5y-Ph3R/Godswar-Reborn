using Godswar.Server.Application.Commands;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresKitBagItemMoveCommandExecutor
{
    private async Task<long?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT inventory_revision
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long revision && revision >= 0
            ? revision
            : null;
    }

    private async Task<LockedKitBagSlots> LockKitBagSlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int sourceSlot,
        int destinationSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, slot_index, prop_id,
                attribute1, attribute2, attribute3, attribute4,
                attribute5,
                attribute_level1, attribute_level2,
                attribute_level3, attribute_level4,
                attribute_level5,
                item_quality, item_grade, bound, stack, item_exp,
                holy_suit_code, holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level,
                holy_socket4_effect_id, holy_socket4_level,
                holy_socket5_effect_id, holy_socket5_level,
                holy_socket6_effect_id, holy_socket6_level,
                to_jsonb(character_items)::text,
                class_attribute1, class_attribute2,
                elemental_attribute1, elemental_attribute2
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index IN (@firstSlot, @secondSlot)
            ORDER BY slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "firstSlot",
            checked((short)Math.Min(sourceSlot, destinationSlot)));
        command.Parameters.AddWithValue(
            "secondSlot",
            checked((short)Math.Max(sourceSlot, destinationSlot)));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        LockedKitBagItem? source = null;
        LockedKitBagItem? destination = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new LockedKitBagItem(
                reader.GetInt64(0),
                reader.GetInt16(1),
                ReadCompactItem(reader),
                reader.GetString(32));
            if (item.SlotIndex == sourceSlot)
            {
                source = item;
            }
            else if (item.SlotIndex == destinationSlot)
            {
                destination = item;
            }
            else
            {
                throw new InvalidDataException(
                    "The locked kit-bag item has an unexpected slot.");
            }
        }
        return new LockedKitBagSlots(source, destination);
    }

    private async Task<short> FindPrivateTemporarySlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT candidate.slot_index::smallint
            FROM generate_series(-32768, -1)
                AS candidate(slot_index)
            WHERE NOT EXISTS (
                SELECT 1
                FROM public.character_items
                WHERE user_id = @characterId
                  AND item_location = 2
                  AND slot_index = candidate.slot_index
            )
            ORDER BY candidate.slot_index
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is short slot && slot < 0
            ? slot
            : throw new InvalidDataException(
                "No private temporary inventory slot is available.");
    }
}
