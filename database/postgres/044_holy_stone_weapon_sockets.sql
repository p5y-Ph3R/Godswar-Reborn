ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket_count smallint NOT NULL DEFAULT 0;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket1_effect_id smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket1_level smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket2_effect_id smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket2_level smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket3_effect_id smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket3_level smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket4_effect_id smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket4_level smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket5_effect_id smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket5_level smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket6_effect_id smallint;
ALTER TABLE character_items ADD COLUMN IF NOT EXISTS holy_socket6_level smallint;

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
    ']' AS compact_entry
FROM character_items ci
LEFT JOIN item_templates it ON it.id = ci.prop_id
LEFT JOIN LATERAL (
    SELECT
        NULLIF(array_length(string_to_array(NULLIF(it.stats->>'BaseFraction', ''), ','), 1), 0) AS base_levels,
        NULLIF(array_length(string_to_array(NULLIF(it.stats->>'AppFraction', ''), ','), 1), 0) AS grade_levels
) quality_limits ON true;

CREATE OR REPLACE VIEW character_item_loadout AS
SELECT
    cb.id AS user_id,
    COALESCE(equipment.equip, '') AS equip,
    COALESCE(kitbag.kitbag_1, '') AS kitbag_1
FROM character_base cb
LEFT JOIN LATERAL (
    SELECT string_agg(COALESCE(cice.compact_entry, '[]'), '#' ORDER BY slot.slot_index) || '#' AS equip
    FROM generate_series(0, 23) AS slot(slot_index)
    LEFT JOIN character_item_compact_entries cice
        ON cice.user_id = cb.id
       AND cice.item_location = 0
       AND cice.slot_index = slot.slot_index
) equipment ON true
LEFT JOIN LATERAL (
    SELECT string_agg(COALESCE(cice.compact_entry, '[]'), '#' ORDER BY slot.slot_index) || '#' AS kitbag_1
    FROM generate_series(0, 95) AS slot(slot_index)
    LEFT JOIN character_item_compact_entries cice
        ON cice.user_id = cb.id
       AND cice.item_location = 1
       AND cice.slot_index = slot.slot_index
) kitbag ON true;

CREATE OR REPLACE VIEW character_equip AS
SELECT
    ci.user_id,
    ci.slot_index::smallint AS body_part_id,
    ci.prop_id,
    ci.attribute1 AS type1,
    attr1.attribute_level AS quality1,
    attr1.attribute_value AS value1,
    ci.attribute2 AS type2,
    attr2.attribute_level AS quality2,
    attr2.attribute_value AS value2,
    ci.attribute3 AS type3,
    attr3.attribute_level AS quality3,
    attr3.attribute_value AS value3,
    ci.attribute4 AS type4,
    attr4.attribute_level AS quality4,
    attr4.attribute_value AS value4,
    ci.attribute5 AS type5,
    attr5.attribute_level AS quality5,
    attr5.attribute_value AS value5,
    ci.item_quality,
    ci.item_grade,
    ci.bound,
    ci.stack,
    ci.item_exp,
    CASE WHEN ci.holy_suit_code > 0 THEN (ci.holy_suit_code / 100)::smallint ELSE 0::smallint END AS holy_suit_type,
    CASE WHEN ci.holy_suit_code > 0 THEN (ci.holy_suit_code % 100)::smallint ELSE 0::smallint END AS holy_suit_level,
    ci.holy_suit_code,
    ci.bound AS isbind,
    ci.holy_socket_count,
    ci.holy_socket1_effect_id,
    ci.holy_socket1_level,
    ci.holy_socket2_effect_id,
    ci.holy_socket2_level,
    ci.holy_socket3_effect_id,
    ci.holy_socket3_level,
    ci.holy_socket4_effect_id,
    ci.holy_socket4_level,
    ci.holy_socket5_effect_id,
    ci.holy_socket5_level,
    ci.holy_socket6_effect_id,
    ci.holy_socket6_level
FROM character_items ci
LEFT JOIN LATERAL (
    SELECT
        LEAST(GREATEST(COALESCE(ci.attribute_level1, ci.item_grade)::integer, 1), iat.max_level)::smallint AS attribute_level,
        iat.level_values[LEAST(GREATEST(ci.item_grade::integer, 1), array_length(iat.level_values, 1))]::real AS attribute_value
    FROM item_attribute_templates iat
    WHERE iat.id = ci.attribute1
      AND ci.attribute1 >= 0
      AND iat.level_values IS NOT NULL
) attr1 ON true
LEFT JOIN LATERAL (
    SELECT
        LEAST(GREATEST(COALESCE(ci.attribute_level2, ci.item_grade)::integer, 1), iat.max_level)::smallint AS attribute_level,
        iat.level_values[LEAST(GREATEST(ci.item_grade::integer, 1), array_length(iat.level_values, 1))]::real AS attribute_value
    FROM item_attribute_templates iat
    WHERE iat.id = ci.attribute2
      AND ci.attribute2 >= 0
      AND iat.level_values IS NOT NULL
) attr2 ON true
LEFT JOIN LATERAL (
    SELECT
        LEAST(GREATEST(COALESCE(ci.attribute_level3, ci.item_grade)::integer, 1), iat.max_level)::smallint AS attribute_level,
        iat.level_values[LEAST(GREATEST(ci.item_grade::integer, 1), array_length(iat.level_values, 1))]::real AS attribute_value
    FROM item_attribute_templates iat
    WHERE iat.id = ci.attribute3
      AND ci.attribute3 >= 0
      AND iat.level_values IS NOT NULL
) attr3 ON true
LEFT JOIN LATERAL (
    SELECT
        LEAST(GREATEST(COALESCE(ci.attribute_level4, ci.item_grade)::integer, 1), iat.max_level)::smallint AS attribute_level,
        iat.level_values[LEAST(GREATEST(ci.item_grade::integer, 1), array_length(iat.level_values, 1))]::real AS attribute_value
    FROM item_attribute_templates iat
    WHERE iat.id = ci.attribute4
      AND ci.attribute4 >= 0
      AND iat.level_values IS NOT NULL
) attr4 ON true
LEFT JOIN LATERAL (
    SELECT
        LEAST(GREATEST(COALESCE(ci.attribute_level5, ci.item_grade)::integer, 1), iat.max_level)::smallint AS attribute_level,
        iat.level_values[LEAST(GREATEST(ci.item_grade::integer, 1), array_length(iat.level_values, 1))]::real AS attribute_value
    FROM item_attribute_templates iat
    WHERE iat.id = ci.attribute5
      AND ci.attribute5 >= 0
      AND iat.level_values IS NOT NULL
) attr5 ON true
WHERE ci.item_location = 0;
