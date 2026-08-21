-- Level-160 class progression. Rank 100 is the fixture's literal max talent rank.
CREATE TEMP TABLE fixture_talents ON COMMIT DROP AS
SELECT f.character_id AS user_id,t.id AS talent_id,100::smallint AS rank
FROM fixture_context f JOIN talent_templates t
  ON t.class_id=f.profession
WHERE (f.profession=0 AND t.id BETWEEN 0 AND 17)
   OR (f.profession=1 AND t.id BETWEEN 50 AND 67);

DO $talent_guard$
BEGIN
  IF (SELECT count(*) FROM fixture_talents)<>90
     OR EXISTS (SELECT 1 FROM fixture_context f
                LEFT JOIN fixture_talents t ON t.user_id=f.character_id
                GROUP BY f.character_id HAVING count(t.talent_id)<>18) THEN
    RAISE EXCEPTION 'sealed max talent catalog is incomplete';
  END IF;
END
$talent_guard$;

DELETE FROM character_talents t USING fixture_context f
WHERE t.user_id=f.character_id;
INSERT INTO character_talents(user_id,talent_id,rank,outbox_revision)
SELECT user_id,talent_id,rank,0 FROM fixture_talents;

CREATE TEMP TABLE fixture_skills ON COMMIT DROP AS
WITH max_combat AS (
  SELECT DISTINCT ON (f.character_id,s.base_name)
    f.character_id AS user_id,s.skill_id,s.skill_level::smallint AS skill_level
  FROM fixture_context f JOIN skill_templates s
    ON f.profession=ANY(s.class_ids)
  WHERE s.skill_level IS NOT NULL
  ORDER BY f.character_id,s.base_name,s.skill_level DESC,
           s.min_level DESC,s.skill_id DESC
), utility AS (
  SELECT f.character_id AS user_id,s.skill_id,
         COALESCE(s.skill_level,1)::smallint AS skill_level
  FROM fixture_context f JOIN skill_templates s
    ON f.profession=ANY(s.class_ids)
  WHERE s.skill_level IS NULL
    AND (s.stats->>'Camp' IS NULL OR (s.stats->>'Camp')::smallint=f.camp)
)
SELECT * FROM max_combat UNION SELECT * FROM utility;

DO $skill_guard$
BEGIN
  IF EXISTS (SELECT 1 FROM fixture_skills f
             LEFT JOIN skill_templates s ON s.skill_id=f.skill_id
             WHERE s.skill_id IS NULL OR f.skill_level<1)
     OR EXISTS (SELECT user_id,skill_id FROM fixture_skills
                GROUP BY user_id,skill_id HAVING count(*)<>1) THEN
    RAISE EXCEPTION 'sealed max skill catalog produced invalid rows';
  END IF;
END
$skill_guard$;

DELETE FROM character_skills s USING fixture_context f
WHERE s.user_id=f.character_id;
INSERT INTO character_skills(user_id,skill_id,skill_level,source)
SELECT user_id,skill_id,skill_level,'max-combat-fixture-v1'
FROM fixture_skills;

UPDATE character_base c SET
  fighter_job_lv=160,fighter_job_exp=0,scholar_job_lv=0,scholar_job_exp=0,
  "Money"=2147483647,"Stone"=2147483647,
  "SkillPoint"=2147483647,"SkillExp"=2147483647
FROM fixture_context f WHERE c.id=f.character_id;
