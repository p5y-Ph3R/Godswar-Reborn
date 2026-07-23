-- Local test character max Champion skills.
-- Keeps one active max skill id per Champion combat skill line and adds utility skills valid for the character faction.

WITH character_context AS (
    SELECT id AS user_id, profession, camp
    FROM character_base
    WHERE id = 1
),
combat_skill_templates AS (
    SELECT st.skill_id
    FROM skill_templates st
    JOIN character_context cc ON cc.profession = ANY(st.class_ids)
    WHERE st.skill_level IS NOT NULL
)
DELETE FROM character_skills cs
USING combat_skill_templates cst
WHERE cs.user_id = 1
  AND cs.skill_id = cst.skill_id;

WITH character_context AS (
    SELECT id AS user_id, profession, camp
    FROM character_base
    WHERE id = 1
),
max_combat_skills AS (
    SELECT DISTINCT ON (st.base_name)
        cc.user_id,
        st.skill_id,
        st.skill_level
    FROM character_context cc
    JOIN skill_templates st ON cc.profession = ANY(st.class_ids)
    WHERE st.skill_level IS NOT NULL
    ORDER BY st.base_name, st.skill_level DESC, st.min_level DESC, st.skill_id DESC
),
utility_skills AS (
    SELECT
        cc.user_id,
        st.skill_id,
        COALESCE(st.skill_level, 1)::smallint AS skill_level
    FROM character_context cc
    JOIN skill_templates st ON cc.profession = ANY(st.class_ids)
    WHERE st.skill_level IS NULL
      AND (
          st.stats->>'Camp' IS NULL
          OR (st.stats->>'Camp')::smallint = cc.camp
      )
),
desired_skills AS (
    SELECT user_id, skill_id, skill_level::smallint
    FROM max_combat_skills
    UNION
    SELECT user_id, skill_id, skill_level
    FROM utility_skills
)
INSERT INTO character_skills (user_id, skill_id, skill_level, source, acquired_at)
SELECT user_id, skill_id, skill_level, 'test-max-skills', now()
FROM desired_skills
ON CONFLICT (user_id, skill_id) DO UPDATE
SET skill_level = EXCLUDED.skill_level,
    source = EXCLUDED.source,
    acquired_at = EXCLUDED.acquired_at;
