CREATE OR REPLACE VIEW character_item_compact_entries AS
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
    LEAST(GREATEST(ci.item_quality::integer, 1), COALESCE(quality_limits.base_levels, ci.item_quality::integer))::text || ',' ||
    LEAST(GREATEST(ci.item_grade::integer, 1), LEAST(COALESCE(quality_limits.grade_levels, ci.item_grade::integer), 25))::text || ',' ||
    ci.bound::text || ',' ||
    ci.stack::text || ',' ||
    ci.item_exp::text || ',' ||
    ci.holy_suit_code::text || ',' ||
    COALESCE(ci.attribute_level1::text, '') || ',' ||
    COALESCE(ci.attribute_level2::text, '') || ',' ||
    COALESCE(ci.attribute_level3::text, '') || ',' ||
    COALESCE(ci.attribute_level4::text, '') || ',' ||
    COALESCE(ci.attribute_level5::text, '') ||
    ']' AS compact_entry
FROM character_items ci
LEFT JOIN item_templates it ON it.id = ci.prop_id
LEFT JOIN LATERAL (
    SELECT
        NULLIF(array_length(string_to_array(NULLIF(it.stats->>'BaseFraction', ''), ','), 1), 0) AS base_levels,
        NULLIF(array_length(string_to_array(NULLIF(it.stats->>'AppFraction', ''), ','), 1), 0) AS grade_levels
) quality_limits ON true;

CREATE OR REPLACE VIEW character_item_validation AS
SELECT
    ci.user_id,
    ci.item_location,
    ci.slot_index,
    ci.prop_id,
    it.display_name,
    ci.item_quality AS requested_quality,
    COALESCE(quality_limits.base_levels, ci.item_quality::integer)::smallint AS max_packet_quality,
    LEAST(GREATEST(ci.item_quality::integer, 1), COALESCE(quality_limits.base_levels, ci.item_quality::integer))::smallint AS packet_quality,
    ci.item_grade AS requested_grade,
    LEAST(COALESCE(quality_limits.grade_levels, ci.item_grade::integer), 25)::smallint AS max_packet_grade,
    LEAST(GREATEST(ci.item_grade::integer, 1), LEAST(COALESCE(quality_limits.grade_levels, ci.item_grade::integer), 25))::smallint AS packet_grade,
    ci.item_quality > COALESCE(quality_limits.base_levels, ci.item_quality::integer) AS quality_exceeds_item_template,
    ci.item_grade > LEAST(COALESCE(quality_limits.grade_levels, ci.item_grade::integer), 25) AS grade_exceeds_item_template
FROM character_items ci
LEFT JOIN item_templates it ON it.id = ci.prop_id
LEFT JOIN LATERAL (
    SELECT
        NULLIF(array_length(string_to_array(NULLIF(it.stats->>'BaseFraction', ''), ','), 1), 0) AS base_levels,
        NULLIF(array_length(string_to_array(NULLIF(it.stats->>'AppFraction', ''), ','), 1), 0) AS grade_levels
) quality_limits ON true;
