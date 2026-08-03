using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private static IEnumerable<(int Slot, CompactItemEntry Item)> EnumerateCompactSlots(string compact, int maxSlots)
    {
        var slots = compact.Split('#', StringSplitOptions.None).ToList();
        if (slots.Count > 0 && slots[^1].Length == 0)
        {
            slots.RemoveAt(slots.Count - 1);
        }

        for (var slot = 0; slot < slots.Count && slot < maxSlots; slot++)
        {
            var item = CompactItemEntry.Parse(slots[slot]);
            yield return (slot, item);
        }
    }

    private static async Task InsertCharacterItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short itemLocation,
        int slotIndex,
        CompactItemEntry item,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                attribute1, attribute2, attribute3, attribute4, attribute5,
                class_attribute1, class_attribute2,
                attribute_level1, attribute_level2, attribute_level3, attribute_level4, attribute_level5,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code,
                holy_socket_count, holy_socket1_effect_id, holy_socket1_level, holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level, holy_socket4_effect_id, holy_socket4_level,
                holy_socket5_effect_id, holy_socket5_level, holy_socket6_effect_id, holy_socket6_level
            )
            VALUES (
                @characterId, @itemLocation, @slotIndex, @itemId,
                @attribute1, @attribute2, @attribute3, @attribute4, @attribute5,
                @classAttribute1, @classAttribute2,
                @attributeLevel1, @attributeLevel2, @attributeLevel3, @attributeLevel4, @attributeLevel5,
                @itemQuality, @itemGrade, @bound, @stack, @itemExp, @holySuitCode,
                @holySocketCount, @holySocket1EffectId, @holySocket1Level, @holySocket2EffectId, @holySocket2Level,
                @holySocket3EffectId, @holySocket3Level, @holySocket4EffectId, @holySocket4Level,
                @holySocket5EffectId, @holySocket5Level, @holySocket6EffectId, @holySocket6Level
            )
            ON CONFLICT (user_id, item_location, slot_index) DO UPDATE
            SET prop_id = EXCLUDED.prop_id,
                attribute1 = EXCLUDED.attribute1,
                attribute2 = EXCLUDED.attribute2,
                attribute3 = EXCLUDED.attribute3,
                attribute4 = EXCLUDED.attribute4,
                attribute5 = EXCLUDED.attribute5,
                class_attribute1 = EXCLUDED.class_attribute1,
                class_attribute2 = EXCLUDED.class_attribute2,
                attribute_level1 = EXCLUDED.attribute_level1,
                attribute_level2 = EXCLUDED.attribute_level2,
                attribute_level3 = EXCLUDED.attribute_level3,
                attribute_level4 = EXCLUDED.attribute_level4,
                attribute_level5 = EXCLUDED.attribute_level5,
                item_quality = EXCLUDED.item_quality,
                item_grade = EXCLUDED.item_grade,
                bound = EXCLUDED.bound,
                stack = EXCLUDED.stack,
                item_exp = EXCLUDED.item_exp,
                holy_suit_code = EXCLUDED.holy_suit_code,
                holy_socket_count = EXCLUDED.holy_socket_count,
                holy_socket1_effect_id = EXCLUDED.holy_socket1_effect_id,
                holy_socket1_level = EXCLUDED.holy_socket1_level,
                holy_socket2_effect_id = EXCLUDED.holy_socket2_effect_id,
                holy_socket2_level = EXCLUDED.holy_socket2_level,
                holy_socket3_effect_id = EXCLUDED.holy_socket3_effect_id,
                holy_socket3_level = EXCLUDED.holy_socket3_level,
                holy_socket4_effect_id = EXCLUDED.holy_socket4_effect_id,
                holy_socket4_level = EXCLUDED.holy_socket4_level,
                holy_socket5_effect_id = EXCLUDED.holy_socket5_effect_id,
                holy_socket5_level = EXCLUDED.holy_socket5_level,
                holy_socket6_effect_id = EXCLUDED.holy_socket6_effect_id,
                holy_socket6_level = EXCLUDED.holy_socket6_level,
                updated_at = now();
            """, connection, transaction);
        AddCharacterItemParameters(command, characterId, itemLocation, slotIndex, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCharacterItemIntoEmptySlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short itemLocation,
        int slotIndex,
        CompactItemEntry item,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                attribute1, attribute2, attribute3, attribute4, attribute5,
                class_attribute1, class_attribute2,
                attribute_level1, attribute_level2, attribute_level3, attribute_level4, attribute_level5,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code,
                holy_socket_count, holy_socket1_effect_id, holy_socket1_level, holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level, holy_socket4_effect_id, holy_socket4_level,
                holy_socket5_effect_id, holy_socket5_level, holy_socket6_effect_id, holy_socket6_level
            )
            VALUES (
                @characterId, @itemLocation, @slotIndex, @itemId,
                @attribute1, @attribute2, @attribute3, @attribute4, @attribute5,
                @classAttribute1, @classAttribute2,
                @attributeLevel1, @attributeLevel2, @attributeLevel3, @attributeLevel4, @attributeLevel5,
                @itemQuality, @itemGrade, @bound, @stack, @itemExp, @holySuitCode,
                @holySocketCount, @holySocket1EffectId, @holySocket1Level, @holySocket2EffectId, @holySocket2Level,
                @holySocket3EffectId, @holySocket3Level, @holySocket4EffectId, @holySocket4Level,
                @holySocket5EffectId, @holySocket5Level, @holySocket6EffectId, @holySocket6Level
            )
            ON CONFLICT (user_id, item_location, slot_index) DO NOTHING;
            """, connection, transaction);
        AddCharacterItemParameters(command, characterId, itemLocation, slotIndex, item);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Holy-stone destination changed at location {itemLocation}, slot {slotIndex}.");
        }
    }

    private static void AddCharacterItemParameters(
        NpgsqlCommand command,
        int characterId,
        short itemLocation,
        int slotIndex,
        CompactItemEntry item)
    {
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemLocation", itemLocation);
        command.Parameters.AddWithValue("slotIndex", (short)slotIndex);
        command.Parameters.AddWithValue("itemId", (int)item.Id);
        AddAttributeParameter(command, "attribute1", item.Attribute1);
        AddAttributeParameter(command, "attribute2", item.Attribute2);
        AddAttributeParameter(command, "attribute3", item.Attribute3);
        AddAttributeParameter(command, "attribute4", item.Attribute4);
        AddAttributeParameter(command, "attribute5", item.Attribute5);
        AddAttributeParameter(command, "classAttribute1", item.ClassAttribute1);
        AddAttributeParameter(command, "classAttribute2", item.ClassAttribute2);
        AddNullableSmallintParameter(command, "attributeLevel1", item.AttributeLevel1);
        AddNullableSmallintParameter(command, "attributeLevel2", item.AttributeLevel2);
        AddNullableSmallintParameter(command, "attributeLevel3", item.AttributeLevel3);
        AddNullableSmallintParameter(command, "attributeLevel4", item.AttributeLevel4);
        AddNullableSmallintParameter(command, "attributeLevel5", item.AttributeLevel5);
        command.Parameters.AddWithValue("itemQuality", item.Quality);
        command.Parameters.AddWithValue("itemGrade", item.Grade);
        command.Parameters.AddWithValue("bound", item.Bound);
        command.Parameters.AddWithValue("stack", item.Stack);
        command.Parameters.AddWithValue("itemExp", item.Exp);
        command.Parameters.AddWithValue("holySuitCode", item.HolySuitCode);
        AddHolyStoneParameters(command, item);
    }

    private static void AddHolyStoneParameters(NpgsqlCommand command, CompactItemEntry item)
    {
        command.Parameters.AddWithValue(
            "holySocketCount",
            Math.Clamp(item.SocketCount, (short)0, (short)HolyStoneItemMutator.MaxSockets));
        AddNullableSmallintParameter(command, "holySocket1EffectId", item.Socket1EffectId);
        AddNullableSmallintParameter(command, "holySocket1Level", item.Socket1Level);
        AddNullableSmallintParameter(command, "holySocket2EffectId", item.Socket2EffectId);
        AddNullableSmallintParameter(command, "holySocket2Level", item.Socket2Level);
        AddNullableSmallintParameter(command, "holySocket3EffectId", item.Socket3EffectId);
        AddNullableSmallintParameter(command, "holySocket3Level", item.Socket3Level);
        AddNullableSmallintParameter(command, "holySocket4EffectId", item.Socket4EffectId);
        AddNullableSmallintParameter(command, "holySocket4Level", item.Socket4Level);
        AddNullableSmallintParameter(command, "holySocket5EffectId", item.Socket5EffectId);
        AddNullableSmallintParameter(command, "holySocket5Level", item.Socket5Level);
        AddNullableSmallintParameter(command, "holySocket6EffectId", item.Socket6EffectId);
        AddNullableSmallintParameter(command, "holySocket6Level", item.Socket6Level);
    }

    private static async Task<int> DeleteCharacterItemSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short itemLocation,
        int slotIndex,
        string source,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH deleted AS (
                DELETE FROM character_items
                WHERE user_id = @characterId
                  AND item_location = @itemLocation
                  AND slot_index = @slotIndex
                RETURNING *
            )
            INSERT INTO character_item_audit (
                source, action, user_id, item_location, slot_index,
                prop_id, item_quality, item_grade, item_exp, old_item
            )
            SELECT
                @source,
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
            """, connection, transaction);

        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemLocation", itemLocation);
        command.Parameters.AddWithValue("slotIndex", (short)slotIndex);
        command.Parameters.AddWithValue("source", source);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SyncCharacterStarterSkillsAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO character_skills (user_id, skill_id, skill_level, source)
            SELECT
                cb.id,
                st.skill_id,
                st.skill_level,
                'starter'
            FROM character_base cb
            JOIN gameplay_skill_combat_definitions st
              ON cb.profession = ANY(st.class_ids)
             AND st.revision = COALESCE(
                 @gameplayContentRevision,
                 (
                     SELECT publication.revision
                     FROM gameplay_content_publication publication
                     WHERE publication.family = 'gameplay'
                 )
             )
            WHERE st.previous_skill_id IS NULL
              AND COALESCE(st.min_level, 1) <= cb.fighter_job_lv
              AND st.skill_level = 1
              AND NOT EXISTS (
                  SELECT 1
                  FROM character_skills existing
                  JOIN gameplay_skill_combat_definitions existing_template
                    ON existing_template.skill_id = existing.skill_id
                   AND existing_template.revision = st.revision
                  WHERE existing.user_id = cb.id
                    AND existing_template.base_name = st.base_name
                    AND COALESCE(existing_template.skill_level, 0) > COALESCE(st.skill_level, 0)
              )
            ON CONFLICT (user_id, skill_id) DO NOTHING;

            -- Mount compatibility: the original quest chain normally awards
            -- Riding at level 40.  Until quests award books authoritatively,
            -- expose the native Ride skill to every class so equipped mounts
            -- can be exercised without fabricating a combat skill.
            INSERT INTO character_skills (user_id, skill_id, skill_level, source)
            SELECT cb.id, st.skill_id, 1, 'mount-compatibility'
            FROM character_base cb
            JOIN gameplay_skill_combat_definitions st
              ON st.skill_id = 4904
             AND st.revision = COALESCE(
                 @gameplayContentRevision,
                 (
                     SELECT publication.revision
                     FROM gameplay_content_publication publication
                     WHERE publication.family = 'gameplay'
                 )
             )
             AND cb.profession = ANY(st.class_ids)
            ON CONFLICT (user_id, skill_id) DO NOTHING;
            """);
        AddGameplayContentRevisionParameter(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteCharacterEquipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int equipmentSlot,
        CancellationToken cancellationToken)
    {
        await DeleteCharacterItemSlotAsync(
            connection,
            transaction,
            characterId,
            ItemLocationEquipment,
            equipmentSlot,
            "direct-equipment-delete",
            cancellationToken);
    }

    private static async Task UpsertCharacterEquipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int equipmentSlot,
        CompactItemEntry item,
        CancellationToken cancellationToken)
    {
        await InsertCharacterItemAsync(
            connection,
            transaction,
            characterId,
            ItemLocationEquipment,
            equipmentSlot,
            item,
            cancellationToken);
    }

    private static void AddAttributeParameter(NpgsqlCommand command, string name, int? attributeId)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Smallint)
        {
            Value = attributeId is >= 0 ? (short)attributeId.Value : DBNull.Value
        });
    }

    private static void AddNullableSmallintParameter(NpgsqlCommand command, string name, short? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Smallint)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });
    }

    private static void AddNullableIntegerParameter(NpgsqlCommand command, string name, int? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });
    }

    private static void AddNullableRealParameter(NpgsqlCommand command, string name, float? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Real)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });
    }

}
