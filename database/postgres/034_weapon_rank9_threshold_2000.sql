INSERT INTO equipment_rank_rules (rank_kind, rank_level, required_score, aura_effect, source)
VALUES ('weapon', 9, 4000, 8, 'extended_ItemBaseAttribute.ArmEffFraction')
ON CONFLICT (rank_kind, rank_level) DO UPDATE
SET required_score = EXCLUDED.required_score,
    aura_effect = EXCLUDED.aura_effect,
    source = EXCLUDED.source;

UPDATE item_templates
SET stats = stats
    || jsonb_build_object(
        'ArmEffFraction', '40,100,180,240,300,460,600,1200,4000,8000,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1',
        'ArmEff', '1,2,3,4,5,5,5,6,8,9,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5'
    )
WHERE id = 1435;
