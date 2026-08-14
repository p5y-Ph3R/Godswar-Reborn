namespace Godswar.Server.State;

/// <summary>
/// Builds bounded native-client item strings from authoritative
/// <c>character_items</c> rows for a <c>character_base cb</c> query. The SQL
/// deliberately preserves the former compatibility view's quality, grade,
/// sparse-slot, holy-suit, and holy-socket representation.
/// </summary>
internal static class PostgresCharacterItemProjectionSql
{
    public static readonly string EquipmentJoinForCharacterAlias =
        BuildJoin(
            maximumSlot: 23,
            itemLocation: 0,
            projectionAlias: "equipment_projection",
            resultColumn: "equip");

    public static readonly string KitBagJoinForCharacterAlias =
        BuildJoin(
            maximumSlot: 95,
            itemLocation: 1,
            projectionAlias: "kitbag_projection",
            resultColumn: "kitbag_1");

    public static readonly string FullJoinForCharacterAlias =
        EquipmentJoinForCharacterAlias + "\n" +
        KitBagJoinForCharacterAlias + "\n" +
        PostgresCharacterRuntimeItemProjectionSql
            .RankLateralJoinForCharacterAlias;

    private static string BuildJoin(
        int maximumSlot,
        int itemLocation,
        string projectionAlias,
        string resultColumn) =>
        $"""
        LEFT JOIN LATERAL (
            SELECT string_agg(
                       COALESCE(item.compact_entry, '[]'),
                       '#' ORDER BY slot.slot_index) || '#' AS {resultColumn}
            FROM generate_series(0, {maximumSlot}) AS slot(slot_index)
            LEFT JOIN LATERAL (
                SELECT
                    '[' ||
                    ci.prop_id::text || ',' ||
                    COALESCE(ci.attribute1::text, '') || ',' ||
                    COALESCE(ci.attribute2::text, '') || ',' ||
                    COALESCE(ci.attribute3::text, '') || ',' ||
                    COALESCE(ci.attribute4::text, '') || ',' ||
                    COALESCE(ci.attribute5::text, '') || ',' ||
                    LEAST(
                        GREATEST(ci.item_quality::integer, 1),
                        COALESCE(
                            quality_limits.base_levels,
                            ci.item_quality::integer))::text || ',' ||
                    LEAST(
                        GREATEST(ci.item_grade::integer, 1),
                        LEAST(
                            COALESCE(
                                quality_limits.grade_levels,
                                ci.item_grade::integer),
                            25))::text || ',' ||
                    ci.bound::text || ',' ||
                    ci.stack::text || ',' ||
                    ci.item_exp::text || ',' ||
                    ci.holy_suit_code::text || ',' ||
                    COALESCE(ci.attribute_level1::text, '') || ',' ||
                    COALESCE(ci.attribute_level2::text, '') || ',' ||
                    COALESCE(ci.attribute_level3::text, '') || ',' ||
                    COALESCE(ci.attribute_level4::text, '') || ',' ||
                    COALESCE(ci.attribute_level5::text, '') || ',' ||
                    COALESCE(ci.holy_socket_count::text, '0') || ',' ||
                    COALESCE(ci.holy_socket1_effect_id::text, '') || ',' ||
                    COALESCE(ci.holy_socket1_level::text, '') || ',' ||
                    COALESCE(ci.holy_socket2_effect_id::text, '') || ',' ||
                    COALESCE(ci.holy_socket2_level::text, '') || ',' ||
                    COALESCE(ci.holy_socket3_effect_id::text, '') || ',' ||
                    COALESCE(ci.holy_socket3_level::text, '') || ',' ||
                    COALESCE(ci.holy_socket4_effect_id::text, '') || ',' ||
                    COALESCE(ci.holy_socket4_level::text, '') || ',' ||
                    COALESCE(ci.holy_socket5_effect_id::text, '') || ',' ||
                    COALESCE(ci.holy_socket5_level::text, '') || ',' ||
                    COALESCE(ci.holy_socket6_effect_id::text, '') || ',' ||
                    COALESCE(ci.holy_socket6_level::text, '') ||
                    CASE
                        WHEN ci.class_attribute1 IS NULL
                         AND ci.elemental_attribute1 IS NULL
                         AND ci.elemental_attribute2 IS NULL
                         AND ci.holy_socket1_value IS NULL
                         AND ci.holy_socket2_value IS NULL
                         AND ci.holy_socket3_value IS NULL
                         AND ci.holy_socket4_value IS NULL
                         AND sealed_link.pet_id IS NULL
                            THEN ''
                        ELSE
                            ',' ||
                            COALESCE(ci.class_attribute1::text, '') || ',' ||
                            '' || ',' ||
                            COALESCE(ci.elemental_attribute1::text, '') || ',' ||
                            COALESCE(ci.elemental_attribute2::text, '') || ',' ||
                            COALESCE(ci.holy_socket1_value::text, '') || ',' ||
                            COALESCE(ci.holy_socket2_value::text, '') || ',' ||
                            COALESCE(ci.holy_socket3_value::text, '') || ',' ||
                            COALESCE(ci.holy_socket4_value::text, '') ||
                            CASE
                                WHEN sealed_link.pet_id IS NULL THEN ''
                                ELSE ',' || sealed_link.pet_id::text
                            END
                    END ||
                    ']' AS compact_entry
                FROM character_items ci
                LEFT JOIN item_template_content_definitions template
                  ON template.revision = @itemContentRevision
                 AND template.id = ci.prop_id
                LEFT JOIN public.sealed_pet_items sealed_link
                  ON sealed_link.item_instance_id = ci.id
                 AND ci.prop_id = 10109
                 AND sealed_link.owner_character_id = ci.user_id
                 AND (ci.bound = 1) IS NOT DISTINCT FROM
                     sealed_link.pet_bound_snapshot
                 AND EXISTS (
                     SELECT 1
                     FROM public.character_pets sealed_pet
                     WHERE sealed_pet.id = sealed_link.pet_id
                       AND sealed_pet.user_id = ci.user_id
                       AND sealed_pet.activity_state = 'sealed'
                       AND sealed_pet.bound IS NOT DISTINCT FROM
                           sealed_link.pet_bound_snapshot
                 )
                LEFT JOIN LATERAL (
                    SELECT
                        NULLIF(array_length(string_to_array(
                            NULLIF(
                                template.stats->>'BaseFraction', ''),
                            ','), 1), 0) AS base_levels,
                        NULLIF(array_length(string_to_array(
                            NULLIF(
                                template.stats->>'AppFraction', ''),
                            ','), 1), 0) AS grade_levels
                ) quality_limits ON true
                WHERE ci.user_id = cb.id
                  AND ci.item_location = {itemLocation}
                  AND ci.slot_index = slot.slot_index
            ) item ON true
        ) {projectionAlias} ON true
        """;
}
