INSERT INTO equipment_rank_rules (rank_kind, rank_level, required_score, aura_effect, source)
VALUES
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
