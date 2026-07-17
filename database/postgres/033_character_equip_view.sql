DO $$
DECLARE
    equip_relkind "char";
BEGIN
    SELECT c.relkind
    INTO equip_relkind
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relname = 'character_equip';

    IF equip_relkind = 'r' THEN
        EXECUTE $legacy$
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
            ON CONFLICT (user_id, item_location, slot_index) DO NOTHING
        $legacy$;
        EXECUTE 'DROP TABLE character_equip CASCADE';
    ELSIF equip_relkind = 'v' THEN
        EXECUTE 'DROP VIEW character_equip CASCADE';
    END IF;
END $$;

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
