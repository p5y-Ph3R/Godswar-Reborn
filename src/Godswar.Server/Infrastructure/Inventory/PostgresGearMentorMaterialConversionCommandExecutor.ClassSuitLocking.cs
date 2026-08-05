using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresGearMentorMaterialConversionCommandExecutor
{
    private async Task<LockedClassSuitInventory>
        LockClassSuitInventoryAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            ClassSuitCommandSelection gear,
            CancellationToken cancellationToken)
    {
        var equipmentSlot = gear.Location ==
            ClassSuitItemLocation.Equipment
            ? gear.KitBagSlot
            : -1;
        var projection = new CompactItemEntry[96];
        var bagItems = new Dictionary<short, LockedInventoryItem>();
        LockedInventoryItem? equipment = null;

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
                elemental_attribute1, elemental_attribute2,
                holy_socket1_value, holy_socket2_value,
                holy_socket3_value, holy_socket4_value,
                item_location
            FROM public.character_items
            WHERE user_id = @characterId
              AND (
                (
                    @equipmentSlot >= 0
                    AND item_location = 0
                    AND slot_index = @equipmentSlot
                )
                OR (
                    item_location = 1
                    AND slot_index BETWEEN 0 AND 95
                )
              )
            ORDER BY item_location, slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "equipmentSlot",
            checked((short)equipmentSlot));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(1);
            var location = reader.GetInt16(41);
            var locked = new LockedInventoryItem(
                reader.GetInt64(0),
                location,
                slot,
                ReadCompactItem(reader),
                reader.GetString(32));
            if (location == 0)
            {
                if (slot != equipmentSlot || equipment is not null)
                {
                    throw new InvalidDataException(
                        "The authoritative Class Suit equipment lock is inconsistent.");
                }
                equipment = locked;
                continue;
            }
            if (location != 1 ||
                !bagItems.TryAdd(slot, locked))
            {
                throw new InvalidDataException(
                    "The authoritative Class Suit bag contains an invalid or duplicate slot.");
            }
            projection[slot] = locked.Item;
        }

        var compactProjection = string.Join(
            '#',
            projection.Select(static item => item.ToCompactString())) + '#';
        return new LockedClassSuitInventory(
            new LockedKitBag(compactProjection, bagItems),
            equipment);
    }
}
