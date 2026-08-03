using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private async Task SyncCharacterEquipAsync(CancellationToken cancellationToken)
    {
        // character_equip is now a compatibility view over character_items.
        await Task.CompletedTask;
    }

    private static async Task<int?> ResolveRequestedEmptyKitBagSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int requestedSlot,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT @requestedSlot::integer
            WHERE @requestedSlot BETWEEN 0 AND @maxSlot
              AND NOT EXISTS (
                SELECT 1
                FROM character_items ci
                WHERE ci.user_id = @characterId
                  AND ci.item_location = @kitBagLocation
                  AND ci.slot_index = @requestedSlot
              );
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("requestedSlot", requestedSlot);
        command.Parameters.AddWithValue("maxSlot", KitBagProjectionSlots - 1);
        command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null || scalar is DBNull ? null : Convert.ToInt32(scalar);
    }

    private static async Task<int> AllocateTempItemSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT candidate.slot_index
            FROM generate_series(-32768, -1) AS candidate(slot_index)
            WHERE NOT EXISTS (
                SELECT 1
                FROM character_items ci
                WHERE ci.user_id = @characterId
                  AND ci.item_location = @tempLocation
                  AND ci.slot_index = candidate.slot_index
            )
            ORDER BY candidate.slot_index
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("tempLocation", (short)2);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar is DBNull)
        {
            throw new InvalidOperationException($"No temporary item slot is available for character {characterId}.");
        }

        return Convert.ToInt32(scalar);
    }

    private static async Task UpdateCharacterItemSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long itemRowId,
        short itemLocation,
        int slotIndex,
        CancellationToken cancellationToken,
        bool recomputeHolySuitPoints = false)
    {
        await using var command = new NpgsqlCommand("""
            WITH moved AS (
                UPDATE character_items
                SET item_location = @itemLocation,
                    slot_index = @slotIndex,
                    updated_at = now()
                WHERE id = @itemRowId
                RETURNING user_id
            )
            SELECT CASE
                WHEN @recomputeHolySuitPoints THEN
                    public.recompute_character_holy_suit_points(user_id)
                ELSE user_id
            END
            FROM moved;
            """, connection, transaction);
        command.Parameters.AddWithValue("itemRowId", itemRowId);
        command.Parameters.AddWithValue("itemLocation", itemLocation);
        command.Parameters.AddWithValue("slotIndex", (short)slotIndex);
        command.Parameters.AddWithValue(
            "recomputeHolySuitPoints",
            recomputeHolySuitPoints);
        if (await command.ExecuteScalarAsync(cancellationToken) is not int)
        {
            throw new InvalidDataException(
                "The moved item's Holy Suit points could not be recomputed.");
        }
    }

    private static async Task<(string Equipment, string KitBag)> LoadAuthoritativeItemProjectionsForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        var equipment = new CompactItemEntry[EquipmentProjectionSlots];
        var kitBag = new CompactItemEntry[KitBagProjectionSlots];

        await using var command = new NpgsqlCommand("""
            SELECT
                item_location, slot_index, prop_id,
                attribute1, attribute2, attribute3, attribute4, attribute5,
                attribute_level1, attribute_level2, attribute_level3, attribute_level4, attribute_level5,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code,
                holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level,
                holy_socket4_effect_id, holy_socket4_level,
                holy_socket5_effect_id, holy_socket5_level,
                holy_socket6_effect_id, holy_socket6_level,
                class_attribute1, class_attribute2,
                elemental_attribute1, elemental_attribute2
            FROM character_items
            WHERE user_id = @characterId
              AND item_location IN (@equipmentLocation, @kitBagLocation)
            ORDER BY item_location, slot_index
            FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("equipmentLocation", ItemLocationEquipment);
        command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var location = reader.GetInt16(0);
            var slot = reader.GetInt16(1);
            var item = ReadAuthoritativeCompactItem(reader);
            if (location == ItemLocationEquipment && slot is >= 0 and < EquipmentProjectionSlots)
            {
                equipment[slot] = item;
            }
            else if (location == ItemLocationKitBag && slot is >= 0 and < KitBagProjectionSlots)
            {
                kitBag[slot] = item;
            }
        }

        return (BuildCompactItemProjection(equipment), BuildCompactItemProjection(kitBag));
    }

    private static CompactItemEntry ReadAuthoritativeCompactItem(NpgsqlDataReader reader)
    {
        return new CompactItemEntry(
            checked((uint)reader.GetInt32(2)),
            ReadNullableAttribute(reader, 3),
            ReadNullableAttribute(reader, 4),
            ReadNullableAttribute(reader, 5),
            ReadNullableAttribute(reader, 6),
            ReadNullableAttribute(reader, 7),
            reader.GetInt16(13),
            reader.GetInt16(14),
            reader.GetInt16(15),
            reader.GetInt16(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            ReadNullableSmallint(reader, 8),
            ReadNullableSmallint(reader, 9),
            ReadNullableSmallint(reader, 10),
            ReadNullableSmallint(reader, 11),
            ReadNullableSmallint(reader, 12),
            reader.GetInt16(19),
            ReadNullableSmallint(reader, 20),
            ReadNullableSmallint(reader, 21),
            ReadNullableSmallint(reader, 22),
            ReadNullableSmallint(reader, 23),
            ReadNullableSmallint(reader, 24),
            ReadNullableSmallint(reader, 25),
            ReadNullableSmallint(reader, 26),
            ReadNullableSmallint(reader, 27),
            ReadNullableSmallint(reader, 28),
            ReadNullableSmallint(reader, 29),
            ReadNullableSmallint(reader, 30),
            ReadNullableSmallint(reader, 31))
        {
            ClassAttribute1 = ReadNullableAttribute(reader, 32),
            ClassAttribute2 = ReadNullableAttribute(reader, 33),
            ElementalAttribute1 = ReadNullableAttribute(reader, 34),
            ElementalAttribute2 = ReadNullableAttribute(reader, 35)
        };
    }

    private static int? ReadNullableAttribute(NpgsqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);
    }

    private static short? ReadNullableSmallint(NpgsqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);
    }

    private static string BuildCompactItemProjection(IEnumerable<CompactItemEntry> items)
    {
        return string.Join('#', items.Select(static item => item.ToCompactString())) + '#';
    }

    private static Task ApplyForgeSlotMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        ForgeSlotMutation mutation,
        CancellationToken cancellationToken)
    {
        return ApplyKitBagSlotMutationAsync(
            connection,
            transaction,
            characterId,
            mutation.Slot,
            mutation.Before,
            mutation.After,
            "Forge",
            "forge-consume",
            cancellationToken);
    }

    private static Task ApplyGearEnhancementSlotMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        GearEnhancementSlotMutation mutation,
        CancellationToken cancellationToken)
    {
        return ApplyKitBagSlotMutationAsync(
            connection,
            transaction,
            characterId,
            mutation.KitBagSlot,
            mutation.Before,
            mutation.After,
            "Gear-enhancement",
            "gear-enhancement-consume",
            cancellationToken);
    }

    private static Task ApplyGearMentorSlotMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        GearMentorSlotMutation mutation,
        CancellationToken cancellationToken)
    {
        return ApplyKitBagSlotMutationAsync(
            connection,
            transaction,
            characterId,
            mutation.KitBagSlot,
            mutation.Before,
            mutation.After,
            "Gear Mentor",
            "gear-mentor-consume",
            cancellationToken);
    }

    private static async Task ApplyKitBagSlotMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int slot,
        CompactItemEntry before,
        CompactItemEntry after,
        string operationName,
        string consumeAuditSource,
        CancellationToken cancellationToken)
    {
        if (before.IsEmpty)
        {
            if (after.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"{operationName} plan contains an empty-to-empty kit-bag mutation at slot {slot}.");
            }

            await InsertCharacterItemIntoEmptySlotAsync(
                connection,
                transaction,
                characterId,
                ItemLocationKitBag,
                slot,
                after,
                cancellationToken);
            return;
        }

        if (before == after)
        {
            return;
        }

        if (after.IsEmpty)
        {
            var deleted = await DeleteCharacterItemSlotAsync(
                connection,
                transaction,
                characterId,
                ItemLocationKitBag,
                slot,
                consumeAuditSource,
                cancellationToken);
            if (deleted != 1)
            {
                throw new InvalidOperationException(
                    $"{operationName} kit-bag slot {slot} disappeared after it was locked.");
            }

            return;
        }

        await using var command = new NpgsqlCommand("""
            UPDATE character_items
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
                updated_at = now()
            WHERE user_id = @characterId
              AND item_location = @itemLocation
              AND slot_index = @slotIndex;
            """, connection, transaction);
        AddCharacterItemParameters(
            command,
            characterId,
            ItemLocationKitBag,
            slot,
            after);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"{operationName} kit-bag slot {slot} changed after it was locked.");
        }
    }

}
