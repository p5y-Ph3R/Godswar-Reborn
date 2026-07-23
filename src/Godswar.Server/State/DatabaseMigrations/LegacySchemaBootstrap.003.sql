CREATE VIEW character_equip AS
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
    ci.holy_socket6_level,
    ci.bound AS isbind
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

INSERT INTO character_items (
    user_id, item_location, slot_index, prop_id,
    attribute1, attribute2, attribute3, attribute4, attribute5,
    attribute_level1, attribute_level2, attribute_level3, attribute_level4, attribute_level5,
    item_quality, item_grade, bound, stack, item_exp, holy_suit_code
)
SELECT
    ce.user_id,
    0,
    ce.body_part_id,
    ce.prop_id,
    ce.type1,
    ce.type2,
    ce.type3,
    ce.type4,
    ce.type5,
    ce.quality1,
    ce.quality2,
    ce.quality3,
    ce.quality4,
    ce.quality5,
    ce.item_quality,
    ce.item_grade,
    ce.bound,
    ce.stack,
    ce.item_exp,
    ce.holy_suit_code
FROM character_equip ce
ON CONFLICT (user_id, item_location, slot_index) DO NOTHING;

-- Import legacy compact bag rows once. character_items is authoritative
-- after this point; replaying character_kitbag would resurrect items
-- that a player consumed, moved, or deleted.
WITH entries AS (
    SELECT
        ck.user_id,
        item.ordinality - 1 AS slot_index,
        string_to_array(trim(both '[]' FROM item.entry), ',') AS item_parts
    FROM character_kitbag ck
    CROSS JOIN LATERAL regexp_split_to_table(COALESCE(ck.kitbag_1, ''), '#') WITH ORDINALITY AS item(entry, ordinality)
    WHERE item.entry <> ''
      AND item.entry <> '[]'
      AND NOT EXISTS (
          SELECT 1
          FROM server_data_migrations
          WHERE migration_key = '20260721_legacy_character_kitbag_import'
      )
), imported AS (
    INSERT INTO character_items (
        user_id, item_location, slot_index, prop_id,
        attribute1, attribute2, attribute3, attribute4, attribute5,
        attribute_level1, attribute_level2, attribute_level3, attribute_level4, attribute_level5,
        item_quality, item_grade, bound, stack, item_exp, holy_suit_code,
        holy_socket_count, holy_socket1_effect_id, holy_socket1_level, holy_socket2_effect_id, holy_socket2_level,
        holy_socket3_effect_id, holy_socket3_level, holy_socket4_effect_id, holy_socket4_level,
        holy_socket5_effect_id, holy_socket5_level, holy_socket6_effect_id, holy_socket6_level
    )
    SELECT
        user_id,
        1,
        slot_index,
        NULLIF(item_parts[1], '')::integer,
        CASE WHEN NULLIF(item_parts[2], '') IS NOT NULL AND NULLIF(item_parts[2], '')::integer >= 0 THEN NULLIF(item_parts[2], '')::smallint END,
        CASE WHEN NULLIF(item_parts[3], '') IS NOT NULL AND NULLIF(item_parts[3], '')::integer >= 0 THEN NULLIF(item_parts[3], '')::smallint END,
        CASE WHEN NULLIF(item_parts[4], '') IS NOT NULL AND NULLIF(item_parts[4], '')::integer >= 0 THEN NULLIF(item_parts[4], '')::smallint END,
        CASE WHEN NULLIF(item_parts[5], '') IS NOT NULL AND NULLIF(item_parts[5], '')::integer >= 0 THEN NULLIF(item_parts[5], '')::smallint END,
        CASE WHEN NULLIF(item_parts[6], '') IS NOT NULL AND NULLIF(item_parts[6], '')::integer >= 0 THEN NULLIF(item_parts[6], '')::smallint END,
        NULLIF(item_parts[13], '')::smallint,
        NULLIF(item_parts[14], '')::smallint,
        NULLIF(item_parts[15], '')::smallint,
        NULLIF(item_parts[16], '')::smallint,
        NULLIF(item_parts[17], '')::smallint,
        COALESCE(NULLIF(item_parts[7], '')::smallint, 1),
        COALESCE(NULLIF(item_parts[8], '')::smallint, 1),
        COALESCE(NULLIF(item_parts[9], '')::smallint, 0),
        COALESCE(NULLIF(item_parts[10], '')::smallint, 1),
        COALESCE(NULLIF(item_parts[11], '')::integer, 0),
        COALESCE(NULLIF(item_parts[12], '')::integer, 0),
        COALESCE(NULLIF(item_parts[18], '')::smallint, 0),
        NULLIF(item_parts[19], '')::smallint,
        NULLIF(item_parts[20], '')::smallint,
        NULLIF(item_parts[21], '')::smallint,
        NULLIF(item_parts[22], '')::smallint,
        NULLIF(item_parts[23], '')::smallint,
        NULLIF(item_parts[24], '')::smallint,
        NULLIF(item_parts[25], '')::smallint,
        NULLIF(item_parts[26], '')::smallint,
        NULLIF(item_parts[27], '')::smallint,
        NULLIF(item_parts[28], '')::smallint,
        NULLIF(item_parts[29], '')::smallint,
        NULLIF(item_parts[30], '')::smallint
    FROM entries
    WHERE NULLIF(item_parts[1], '') IS NOT NULL
    ON CONFLICT (user_id, item_location, slot_index) DO NOTHING
    RETURNING 1
)
INSERT INTO server_data_migrations (migration_key, affected_rows)
SELECT
    '20260721_legacy_character_kitbag_import',
    COUNT(*)::integer
FROM imported
ON CONFLICT (migration_key) DO NOTHING;

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

CREATE TABLE IF NOT EXISTS holy_suit_tiers (
    type smallint PRIMARY KEY,
    name varchar(32) NOT NULL,
    max_level smallint NOT NULL DEFAULT 10,
    material_item_id integer,
    material_name varchar(64),
    source varchar(128) NOT NULL
);

INSERT INTO holy_suit_tiers (type, name, max_level, material_item_id, material_name, source)
VALUES
    (0, 'Common', 0, NULL, NULL, 'EquipSuitInfoIni.xml'),
    (1, 'Bronze', 10, 9010, 'Bronze Ware', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (2, 'Silver', 10, 9011, 'Silver Ware', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (3, 'Gold', 10, 9012, 'Gold Ware', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (4, 'Platinum', 10, 9013, 'Platinum Ingot', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (5, 'Mithril', 10, 9014, 'Mithril Ingot', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (6, 'Orichalcum', 10, 9015, 'Orichalcum Ingot', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (7, 'Adamantium', 10, 9016, 'Adamantite', 'EquipSuitInfoIni.xml + EquipName.dat')
ON CONFLICT (type) DO UPDATE
SET name = EXCLUDED.name,
    max_level = EXCLUDED.max_level,
    material_item_id = EXCLUDED.material_item_id,
    material_name = EXCLUDED.material_name,
    source = EXCLUDED.source;

CREATE TABLE IF NOT EXISTS holy_suit_upgrade_requirements (
    suit_type smallint NOT NULL REFERENCES holy_suit_tiers(type),
    from_level smallint NOT NULL,
    to_suit_type smallint NOT NULL REFERENCES holy_suit_tiers(type),
    to_level smallint NOT NULL,
    required_exp bigint NOT NULL DEFAULT 0,
    required_prisms integer NOT NULL DEFAULT 0,
    source varchar(128) NOT NULL,
    PRIMARY KEY (suit_type, from_level)
);

INSERT INTO holy_suit_upgrade_requirements (
    suit_type, from_level, to_suit_type, to_level, required_exp, required_prisms, source
)
VALUES
    (1, 0, 1, 1, 9688, 0, 'HelpSystemConfig.lua'),
    (1, 1, 1, 2, 58127, 0, 'HelpSystemConfig.lua'),
    (1, 2, 1, 3, 174380, 0, 'HelpSystemConfig.lua'),
    (1, 3, 1, 4, 348759, 0, 'HelpSystemConfig.lua'),
    (1, 4, 1, 5, 581265, 0, 'HelpSystemConfig.lua'),
    (1, 5, 1, 6, 4198026, 0, 'HelpSystemConfig.lua'),
    (1, 6, 1, 7, 4843876, 0, 'HelpSystemConfig.lua'),
    (1, 7, 1, 8, 5489727, 0, 'HelpSystemConfig.lua'),
    (1, 8, 1, 9, 6458502, 0, 'HelpSystemConfig.lua'),
    (1, 9, 1, 10, 7427277, 0, 'HelpSystemConfig.lua'),
    (1, 10, 2, 1, 3875101, 0, 'HelpSystemConfig.lua'),
    (2, 1, 2, 2, 58127, 0, 'HelpSystemConfig.lua'),
    (2, 2, 2, 3, 9416496, 0, 'HelpSystemConfig.lua'),
    (2, 3, 2, 4, 14647883, 0, 'HelpSystemConfig.lua'),
    (2, 4, 2, 5, 18832991, 0, 'HelpSystemConfig.lua'),
    (2, 5, 2, 6, 23018100, 0, 'HelpSystemConfig.lua'),
    (2, 6, 2, 7, 27278774, 0, 'HelpSystemConfig.lua'),
    (2, 7, 2, 8, 31475509, 0, 'HelpSystemConfig.lua'),
    (2, 8, 2, 9, 35672243, 0, 'HelpSystemConfig.lua'),
    (2, 9, 2, 10, 41967345, 0, 'HelpSystemConfig.lua'),
    (2, 10, 3, 1, 57661505, 0, 'HelpSystemConfig.lua'),
    (3, 1, 3, 2, 61505605, 0, 'HelpSystemConfig.lua'),
    (3, 2, 3, 3, 63497705, 0, 'HelpSystemConfig.lua'),
    (3, 3, 3, 4, 69193805, 0, 'HelpSystemConfig.lua'),
    (3, 4, 3, 5, 73037906, 0, 'HelpSystemConfig.lua'),
    (3, 5, 3, 6, 76882006, 0, 'HelpSystemConfig.lua'),
    (3, 6, 3, 7, 80726106, 0, 'HelpSystemConfig.lua'),
    (3, 7, 3, 8, 88414306, 0, 'HelpSystemConfig.lua'),
    (3, 8, 3, 9, 96102508, 0, 'HelpSystemConfig.lua'),
    (3, 9, 3, 10, 100000000, 0, 'HelpSystemConfig.lua'),
    (3, 10, 4, 1, 133833876, 0, 'HelpSystemConfig.lua'),
    (4, 1, 4, 2, 155886267, 0, 'HelpSystemConfig.lua'),
    (4, 2, 4, 3, 184272577, 0, 'HelpSystemConfig.lua'),
    (4, 3, 4, 4, 234454153, 0, 'HelpSystemConfig.lua'),
    (4, 4, 4, 5, 295435479, 0, 'HelpSystemConfig.lua'),
    (4, 5, 4, 6, 373355467, 0, 'HelpSystemConfig.lua'),
    (4, 6, 4, 7, 485358297, 0, 'HelpSystemConfig.lua'),
    (4, 7, 4, 8, 616532565, 0, 'HelpSystemConfig.lua'),
    (4, 8, 4, 9, 735697878, 0, 'HelpSystemConfig.lua'),
    (4, 9, 4, 10, 866697995, 0, 'HelpSystemConfig.lua'),
    (4, 10, 5, 1, 999999999, 0, 'HelpSystemConfig.lua'),
    (5, 1, 5, 2, 0, 12, 'HelpSystemConfig.lua'),
    (5, 2, 5, 3, 0, 15, 'HelpSystemConfig.lua'),
    (5, 3, 5, 4, 0, 18, 'HelpSystemConfig.lua'),
    (5, 4, 5, 5, 0, 21, 'HelpSystemConfig.lua'),
    (5, 5, 5, 6, 0, 24, 'HelpSystemConfig.lua'),
    (5, 6, 5, 7, 0, 27, 'HelpSystemConfig.lua'),
    (5, 7, 5, 8, 0, 30, 'HelpSystemConfig.lua'),
    (5, 8, 5, 9, 0, 33, 'HelpSystemConfig.lua'),
    (5, 9, 5, 10, 0, 36, 'HelpSystemConfig.lua'),
    (5, 10, 6, 1, 0, 39, 'HelpSystemConfig.lua'),
    (6, 1, 6, 2, 0, 42, 'HelpSystemConfig.lua'),
    (6, 2, 6, 3, 0, 45, 'HelpSystemConfig.lua'),
    (6, 3, 6, 4, 0, 48, 'HelpSystemConfig.lua'),
    (6, 4, 6, 5, 0, 51, 'HelpSystemConfig.lua'),
    (6, 5, 6, 6, 0, 54, 'HelpSystemConfig.lua'),
    (6, 6, 6, 7, 0, 57, 'HelpSystemConfig.lua'),
    (6, 7, 6, 8, 0, 60, 'HelpSystemConfig.lua'),
    (6, 8, 6, 9, 0, 63, 'HelpSystemConfig.lua'),
    (6, 9, 6, 10, 0, 66, 'HelpSystemConfig.lua'),
    (6, 10, 7, 1, 0, 69, 'HelpSystemConfig.lua'),
    (7, 1, 7, 2, 0, 72, 'HelpSystemConfig.lua'),
    (7, 2, 7, 3, 0, 75, 'HelpSystemConfig.lua'),
    (7, 3, 7, 4, 0, 78, 'HelpSystemConfig.lua'),
    (7, 4, 7, 5, 0, 81, 'HelpSystemConfig.lua'),
    (7, 5, 7, 6, 0, 84, 'HelpSystemConfig.lua'),
    (7, 6, 7, 7, 0, 87, 'HelpSystemConfig.lua'),
    (7, 7, 7, 8, 0, 90, 'HelpSystemConfig.lua'),
    (7, 8, 7, 9, 0, 93, 'HelpSystemConfig.lua'),
    (7, 9, 7, 10, 0, 96, 'HelpSystemConfig.lua')
ON CONFLICT (suit_type, from_level) DO UPDATE
SET to_suit_type = EXCLUDED.to_suit_type,
    to_level = EXCLUDED.to_level,
    required_exp = EXCLUDED.required_exp,
    required_prisms = EXCLUDED.required_prisms,
    source = EXCLUDED.source;

