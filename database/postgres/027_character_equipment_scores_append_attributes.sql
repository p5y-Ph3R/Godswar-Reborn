CREATE OR REPLACE VIEW character_equipment_scores AS
WITH score_parts AS (
    SELECT
        ce.user_id,
        ce.body_part_id,
        ce.prop_id,
        ce.item_quality,
        ce.item_grade,
        ce.bound,
        ce.stack,
        ce.item_exp,
        ce.holy_suit_type,
        ce.holy_suit_level,
        ce.holy_suit_code,
        it.kind,
        it.display_name,
        it.equipment_slot,
        (
            (ce.type1 IS NOT NULL AND ce.type1 >= 0)::integer +
            (ce.type2 IS NOT NULL AND ce.type2 >= 0)::integer +
            (ce.type3 IS NOT NULL AND ce.type3 >= 0)::integer +
            (ce.type4 IS NOT NULL AND ce.type4 >= 0)::integer +
            (ce.type5 IS NOT NULL AND ce.type5 >= 0)::integer
        ) AS append_attribute_count,
        CASE
            WHEN base_values.values IS NULL THEN 0
            ELSE COALESCE(NULLIF(base_values.values[
                LEAST(GREATEST(ce.item_quality::integer, 1), array_length(base_values.values, 1))
            ], '')::integer, 0)
        END AS base_score,
        CASE
            WHEN app_values.values IS NULL THEN 0
            ELSE COALESCE(NULLIF(app_values.values[
                LEAST(GREATEST(ce.item_grade::integer, 1), array_length(app_values.values, 1))
            ], '')::integer, 0)
        END AS grade_score
    FROM character_equip ce
    LEFT JOIN item_templates it ON it.id = ce.prop_id
    LEFT JOIN LATERAL (
        SELECT string_to_array(it.stats->>'BaseFraction', ',') AS values
        WHERE it.stats ? 'BaseFraction'
    ) base_values ON true
    LEFT JOIN LATERAL (
        SELECT string_to_array(it.stats->>'AppFraction', ',') AS values
        WHERE it.stats ? 'AppFraction'
    ) app_values ON true
)
SELECT
    user_id,
    body_part_id,
    prop_id,
    kind,
    display_name,
    equipment_slot,
    item_quality,
    item_grade,
    bound,
    stack,
    item_exp,
    base_score,
    grade_score * append_attribute_count AS grade_score,
    base_score + (grade_score * append_attribute_count) AS item_score,
    holy_suit_type,
    holy_suit_level,
    holy_suit_code
FROM score_parts;
