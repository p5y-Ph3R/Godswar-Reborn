namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string ElementalAttributeCompactViewSql = """
        CREATE OR REPLACE VIEW
            public.character_item_compact_entries
        AS
        SELECT
            ci.user_id,
            ci.item_location,
            ci.slot_index,
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
                    THEN ''
                ELSE
                    ',' ||
                    COALESCE(ci.class_attribute1::text, '') || ',' ||
                    '' || ',' ||
                    COALESCE(ci.elemental_attribute1::text, '') || ',' ||
                    COALESCE(ci.elemental_attribute2::text, '')
            END ||
            ']' AS compact_entry
        FROM public.character_items ci
        LEFT JOIN public.official_item_template_content it
            ON it.id = ci.prop_id
        LEFT JOIN LATERAL (
            SELECT
                NULLIF(array_length(string_to_array(
                    NULLIF(it.stats->>'BaseFraction', ''),
                    ','), 1), 0) AS base_levels,
                NULLIF(array_length(string_to_array(
                    NULLIF(it.stats->>'AppFraction', ''),
                    ','), 1), 0) AS grade_levels
        ) quality_limits ON true;

        """;
}
