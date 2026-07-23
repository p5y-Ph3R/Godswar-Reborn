-- Mounts and mount gear contribute their authored gameplay stats, but they do
-- not belong to the character's ordinary armor-rank/aura ladder.
CREATE OR REPLACE VIEW character_rank_summary AS
WITH totals AS (
    SELECT
        user_id,
        COALESCE(SUM(item_score) FILTER (WHERE kind = 'weapon'), 0)::integer AS weapon_score,
        COALESCE(SUM(item_score) FILTER (
            WHERE kind <> 'weapon'
              AND kind NOT IN (
                  'mount',
                  'mounthead',
                  'mountarmor',
                  'mountsoul',
                  'mountornament',
                  'mountamulet'
              )
        ), 0)::integer AS armor_score
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
