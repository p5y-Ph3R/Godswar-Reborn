CREATE TABLE IF NOT EXISTS equipment_rank_rules (
    rank_kind varchar(16) NOT NULL,
    rank_level smallint NOT NULL,
    required_score integer NOT NULL,
    aura_effect integer NOT NULL,
    source varchar(64) NOT NULL,
    PRIMARY KEY (rank_kind, rank_level)
);

INSERT INTO equipment_rank_rules (rank_kind, rank_level, required_score, aura_effect, source)
VALUES
    ('weapon', 1, 40, 1, 'ItemBaseAttribute.ArmEffFraction'),
    ('weapon', 2, 100, 2, 'ItemBaseAttribute.ArmEffFraction'),
    ('weapon', 3, 180, 3, 'ItemBaseAttribute.ArmEffFraction'),
    ('weapon', 4, 240, 4, 'ItemBaseAttribute.ArmEffFraction'),
    ('weapon', 5, 300, 5, 'ItemBaseAttribute.ArmEffFraction'),
    ('weapon', 6, 460, 5, 'ItemBaseAttribute.ArmEffFraction'),
    ('weapon', 7, 600, 5, 'ItemBaseAttribute.ArmEffFraction'),
    ('weapon', 8, 1200, 6, 'extended_ItemBaseAttribute.ArmEffFraction'),
    ('weapon', 9, 4000, 8, 'extended_ItemBaseAttribute.ArmEffFraction'),
    ('weapon', 10, 8000, 9, 'extended_ItemBaseAttribute.ArmEffFraction'),
    ('armor', 1, 330, 1, 'ItemBaseAttribute.DefendFraction'),
    ('armor', 2, 475, 2, 'ItemBaseAttribute.DefendFraction'),
    ('armor', 3, 750, 3, 'ItemBaseAttribute.DefendFraction'),
    ('armor', 4, 950, 4, 'ItemBaseAttribute.DefendFraction'),
    ('armor', 5, 1350, 5, 'ItemBaseAttribute.DefendFraction'),
    ('armor', 6, 1720, 6, 'ItemBaseAttribute.DefendFraction'),
    ('armor', 7, 2225, 7, 'ItemBaseAttribute.DefendFraction'),
    ('armor', 8, 3860, 8, 'ItemBaseAttribute.DefendFraction'),
    ('armor', 9, 5250, 9, 'ItemBaseAttribute.DefendFraction'),
    ('armor', 10, 8000, 10, 'extended_ItemBaseAttribute.DefendFraction'),
    ('armor', 11, 12000, 11, 'extended_ItemBaseAttribute.DefendFraction'),
    ('armor', 12, 17000, 12, 'extended_ItemBaseAttribute.DefendFraction'),
    ('armor', 13, 22000, 13, 'extended_ItemBaseAttribute.DefendFraction'),
    ('armor', 14, 25300, 14, 'extended_ItemBaseAttribute.DefendFraction')
ON CONFLICT (rank_kind, rank_level) DO UPDATE
SET required_score = EXCLUDED.required_score,
    aura_effect = EXCLUDED.aura_effect,
    source = EXCLUDED.source;

DELETE FROM equipment_rank_rules
WHERE rank_kind = 'armor'
  AND rank_level > 14;

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

CREATE OR REPLACE VIEW character_rank_summary AS
WITH totals AS (
    SELECT
        user_id,
        COALESCE(SUM(item_score) FILTER (WHERE kind = 'weapon'), 0)::integer AS weapon_score,
        COALESCE(SUM(item_score) FILTER (WHERE kind <> 'weapon'), 0)::integer AS armor_score
    FROM character_equipment_scores
    GROUP BY user_id
)
SELECT
    cb.id AS user_id,
    cb.name,
    COALESCE(t.weapon_score, 0) AS weapon_score,
    COALESCE(wr.rank_level, 0)::smallint AS weapon_rank,
    COALESCE(wr.aura_effect, 0) AS weapon_aura_effect,
    COALESCE(t.armor_score, 0) AS armor_score,
    COALESCE(ar.rank_level, 0)::smallint AS armor_rank,
    COALESCE(ar.aura_effect, 0) AS armor_aura_effect
FROM character_base cb
LEFT JOIN totals t ON t.user_id = cb.id
LEFT JOIN LATERAL (
    SELECT rank_level, aura_effect
    FROM equipment_rank_rules
    WHERE rank_kind = 'weapon'
      AND required_score <= COALESCE(t.weapon_score, 0)
    ORDER BY rank_level DESC
    LIMIT 1
) wr ON true
LEFT JOIN LATERAL (
    SELECT rank_level, aura_effect
    FROM equipment_rank_rules
    WHERE rank_kind = 'armor'
      AND required_score <= COALESCE(t.armor_score, 0)
    ORDER BY rank_level DESC
    LIMIT 1
) ar ON true;
