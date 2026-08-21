CREATE OR REPLACE VIEW character_talent_stat_summary AS
SELECT
    cb.id AS user_id,
    cb.name AS character_name,
    cb.profession,
    tt.id AS talent_id,
    tt.name AS talent_name,
    tet.key AS stat_key,
    tet.display_name AS stat_name,
    ct.rank,
    tt.effect_value,
    tt.is_percent,
    ROUND(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END)::integer AS contribution
FROM character_talents ct
JOIN character_base cb ON cb.id = ct.user_id
JOIN talent_templates tt ON tt.id = ct.talent_id AND tt.class_id = cb.profession
JOIN talent_effect_templates tet ON tet.id = tt.effect_id
WHERE ct.rank > 0;
