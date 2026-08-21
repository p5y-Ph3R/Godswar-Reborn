Set-StrictMode -Version Latest

function Get-MaxFixtureStatusSql([string]$FixtureDirectory) {
    $regularValues = Get-MaxFixtureRegularValues $FixtureDirectory
    $sql = @'
WITH desired(
 expected_id,username,password_verifier,character_name,profession,camp,
 map_id,pos_x,pos_z,build_key,holy_points
) AS (VALUES
 (7001,'dummy_ares_bulwark','__ARES_BULWARK_HASH__','AresBulwark',0,1,0,148::real,-154::real,'warrior',120),
 (7002,'dummy_ares_mirage','__ARES_MIRAGE_HASH__','AresMirage',1,1,0,148::real,-162::real,'champion_dodge',110),
 (7003,'dummy_athena_bulwark','__ATHENA_BULWARK_HASH__','AthenaBulwark',0,0,1,148::real,-154::real,'warrior',120),
 (7004,'dummy_athena_mirage','__ATHENA_MIRAGE_HASH__','AthenaMirage',1,0,1,148::real,-162::real,'champion_dodge',110),
 (7005,'test25','__TEST25_HASH__','AresTempest',1,0,0,136::real,-150::real,'champion_glass',110)
), identities AS (
 SELECT d.*,a.id account_id,c.id character_id
 FROM desired d LEFT JOIN accounts a ON a.id=d.expected_id
 LEFT JOIN character_base c ON c.id=d.expected_id
), identity_ok AS (
 SELECT NOT EXISTS (
  SELECT 1 FROM identities i LEFT JOIN accounts a ON a.id=i.expected_id
  LEFT JOIN character_base c ON c.id=i.expected_id
  LEFT JOIN character_stat_summary s ON s.user_id=i.expected_id
  WHERE a.id IS NULL OR a.username IS DISTINCT FROM i.username OR a.password IS DISTINCT FROM i.password_verifier
    OR a.status IS DISTINCT FROM 0 OR a.login_status IS DISTINCT FROM 0 OR c.id IS NULL
    OR c.account_id IS DISTINCT FROM i.expected_id OR c.server_id IS DISTINCT FROM 1 OR c.name IS DISTINCT FROM i.character_name
    OR c.gender IS DISTINCT FROM 'male' OR c."GM" IS DISTINCT FROM 0 OR c.camp IS DISTINCT FROM i.camp
    OR c.profession IS DISTINCT FROM i.profession OR c.fighter_job_lv IS DISTINCT FROM 160
    OR c.scholar_job_lv IS DISTINCT FROM 0 OR c.fighter_job_exp IS DISTINCT FROM 0 OR c.scholar_job_exp IS DISTINCT FROM 0
    OR c.status IS DISTINCT FROM 0 OR c.belief IS DISTINCT FROM 1 OR c.prestige IS DISTINCT FROM 0 OR c.earl_rank IS DISTINCT FROM 0
    OR c."Map" IS DISTINCT FROM i.map_id
    OR (i.expected_id BETWEEN 7001 AND 7004 AND
        (c."Pos_X" IS DISTINCT FROM i.pos_x
         OR c."Pos_Z" IS DISTINCT FROM i.pos_z))
    OR c."Money" IS DISTINCT FROM 2147483647 OR c."Stone" IS DISTINCT FROM 2147483647
    OR c."SkillPoint" IS DISTINCT FROM 2147483647 OR c."SkillExp" IS DISTINCT FROM 2147483647
    OR c."MaxHP" IS DISTINCT FROM 1500 OR c."MaxMP" IS DISTINCT FROM 177
    OR c.holy_suit_points IS DISTINCT FROM i.holy_points OR c.character_slot IS DISTINCT FROM 0
    OR c.lifecycle_state IS DISTINCT FROM 'active' OR c.deleted_at IS NOT NULL
    OR c.restore_until IS NOT NULL OR c.purge_after IS NOT NULL
    OR c.lifecycle_version IS DISTINCT FROM 1 OR c.fighter_level_sealed IS DISTINCT FROM false
    OR c.pet_shed_capacity IS DISTINCT FROM 2
    OR c.zodiac_type IS DISTINCT FROM 0 OR c.zodiac_level IS DISTINCT FROM 1 OR c.zodiac_energy IS DISTINCT FROM 0
    OR c.zodiac_accumulated_exp_x100 IS DISTINCT FROM 0
    OR c.zodiac_accumulated_talent_exp_x100 IS DISTINCT FROM 0
    OR c.zodiac_energy_remainder_x100 IS DISTINCT FROM 0 OR c.zodiac_lucky_status IS DISTINCT FROM 0
    OR c.zodiac_lucky_expires_at IS NOT NULL OR c.zodiac_online_day IS NOT NULL
    OR c.zodiac_online_duration_ticks IS DISTINCT FROM 0 OR c.zodiac_last_online_at IS NOT NULL
    OR c.zodiac_last_compensation_day IS NOT NULL OR s.user_id IS NULL
    OR (i.expected_id BETWEEN 7001 AND 7004 AND
        (c."curHP" IS DISTINCT FROM s.max_hp
         OR c."curMP" IS DISTINCT FROM s.max_mp))
 ) AND NOT EXISTS (
  SELECT 1 FROM desired d JOIN accounts a ON a.username=d.username
  WHERE a.id IS DISTINCT FROM d.expected_id
 ) AND NOT EXISTS (
  SELECT 1 FROM desired d JOIN character_base c ON c.name=d.character_name
  WHERE c.id IS DISTINCT FROM d.expected_id OR c.account_id IS DISTINCT FROM d.expected_id
 ) AS ok
), regular_raw(build_key,slot_index,prop_id,attrs,class_attr,element1,element2,
 socket_effects,socket_values) AS (VALUES
__REGULAR_VALUES__
), regular AS (
 SELECT build_key,slot_index,prop_id,attrs::smallint[],class_attr::smallint,
  element1::smallint,element2::smallint,socket_effects::smallint[],
  socket_values::smallint[]
 FROM regular_raw
), mount AS (
 SELECT b.build_key,s.slot_index,14508+(s.slot_index-15)*100 prop_id,
  CASE b.build_key WHEN 'warrior' THEN '{307,317,327,387,397}'::smallint[]
   WHEN 'champion_dodge' THEN '{307,317,327,336,387}'::smallint[]
   ELSE '{347,367,407,427,447}'::smallint[] END attrs,
  '{21,22}'::smallint[] socket_effects,'{300,200}'::smallint[] socket_values
 FROM (VALUES('warrior'),('champion_dodge'),('champion_glass')) b(build_key)
 CROSS JOIN generate_series(15,19) s(slot_index)
 UNION ALL SELECT b.build_key,20,16209,
  CASE b.build_key WHEN 'warrior' THEN '{307,317,327,387,397}'::smallint[]
   WHEN 'champion_dodge' THEN '{307,317,327,336,387}'::smallint[]
   ELSE '{347,367,407,427,447}'::smallint[] END,
  '{}'::smallint[],'{}'::smallint[]
 FROM (VALUES('warrior'),('champion_dodge'),('champion_glass')) b(build_key)
), expected_equipment AS (
 SELECT d.expected_id user_id,r.slot_index,r.prop_id,r.attrs,
  ARRAY(SELECT CASE WHEN x=ANY('{4,14,24,34,104,134,144,154,164}'::smallint[])
   THEN 5::smallint ELSE 1::smallint END FROM unnest(r.attrs) x) levels,
  r.class_attr,r.element1,r.element2,r.socket_effects,r.socket_values,710 holy
 FROM desired d JOIN regular r USING(build_key)
 UNION ALL SELECT d.expected_id,m.slot_index,m.prop_id,m.attrs,
  '{1,1,1,1,1}'::smallint[],NULL::smallint,NULL::smallint,NULL::smallint,
  m.socket_effects,m.socket_values,0 FROM desired d JOIN mount m USING(build_key)
), equipment_ok AS (
 SELECT (SELECT count(*) FROM expected_equipment)=87
 AND (SELECT count(*) FROM character_items i JOIN desired d
      ON d.expected_id=i.user_id WHERE i.item_location=0)=87
 AND NOT EXISTS (
  SELECT 1 FROM expected_equipment e LEFT JOIN character_items i
   ON i.user_id=e.user_id AND i.item_location=0 AND i.slot_index=e.slot_index
  WHERE i.id IS NULL OR i.prop_id IS DISTINCT FROM e.prop_id
   OR ARRAY[i.attribute1,i.attribute2,i.attribute3,i.attribute4,i.attribute5]
      IS DISTINCT FROM e.attrs
   OR ARRAY[i.attribute_level1,i.attribute_level2,i.attribute_level3,
            i.attribute_level4,i.attribute_level5] IS DISTINCT FROM e.levels
   OR i.item_quality IS DISTINCT FROM 20 OR i.item_grade IS DISTINCT FROM 25 OR i.bound IS DISTINCT FROM 1 OR i.stack IS DISTINCT FROM 1
   OR i.item_exp IS DISTINCT FROM 0 OR i.holy_suit_code IS DISTINCT FROM e.holy
   OR i.holy_socket_count IS DISTINCT FROM cardinality(e.socket_effects)
   OR ARRAY[i.holy_socket1_effect_id,i.holy_socket2_effect_id,
            i.holy_socket3_effect_id,i.holy_socket4_effect_id]
      IS DISTINCT FROM ARRAY[e.socket_effects[1],e.socket_effects[2],
                             e.socket_effects[3],e.socket_effects[4]]
   OR ARRAY[i.holy_socket1_level,i.holy_socket2_level,
            i.holy_socket3_level,i.holy_socket4_level]
      IS DISTINCT FROM ARRAY[
       CASE WHEN e.socket_effects[1] IS NULL THEN NULL ELSE 10 END,
       CASE WHEN e.socket_effects[2] IS NULL THEN NULL ELSE 10 END,
       CASE WHEN e.socket_effects[3] IS NULL THEN NULL ELSE 10 END,
       CASE WHEN e.socket_effects[4] IS NULL THEN NULL ELSE 10 END]::smallint[]
   OR ARRAY[i.holy_socket1_value,i.holy_socket2_value,
            i.holy_socket3_value,i.holy_socket4_value]
      IS DISTINCT FROM ARRAY[e.socket_values[1],e.socket_values[2],
                             e.socket_values[3],e.socket_values[4]]
   OR i.holy_socket5_effect_id IS NOT NULL OR i.holy_socket5_level IS NOT NULL
   OR i.holy_socket6_effect_id IS NOT NULL OR i.holy_socket6_level IS NOT NULL
   OR i.class_attribute1 IS DISTINCT FROM e.class_attr
   OR i.class_attribute2 IS NOT NULL
   OR i.elemental_attribute1 IS DISTINCT FROM e.element1
   OR i.elemental_attribute2 IS DISTINCT FROM e.element2
 ) AND NOT EXISTS (
  SELECT 1 FROM character_item_validation v JOIN desired d
   ON d.expected_id=v.user_id WHERE v.item_location=0 AND
   (v.quality_exceeds_item_template OR v.grade_exceeds_item_template)
  ) AS ok
), expected_talents AS (
 SELECT d.expected_id user_id,t.id talent_id,100::smallint rank
 FROM desired d JOIN talent_templates t ON t.class_id=d.profession
 WHERE (d.profession=0 AND t.id BETWEEN 0 AND 17)
    OR (d.profession=1 AND t.id BETWEEN 50 AND 67)
), expected_skills AS (
 WITH combat AS (
  SELECT DISTINCT ON(d.expected_id,s.base_name) d.expected_id user_id,
   s.skill_id,s.skill_level::smallint skill_level
  FROM desired d JOIN skill_templates s ON d.profession=ANY(s.class_ids)
  WHERE s.skill_level IS NOT NULL ORDER BY d.expected_id,s.base_name,
   s.skill_level DESC,s.min_level DESC,s.skill_id DESC
 ), utility AS (
  SELECT d.expected_id,s.skill_id,COALESCE(s.skill_level,1)::smallint skill_level
  FROM desired d JOIN skill_templates s ON d.profession=ANY(s.class_ids)
  WHERE s.skill_level IS NULL
   AND (s.stats->>'Camp' IS NULL OR (s.stats->>'Camp')::smallint=d.camp)
 ) SELECT * FROM combat UNION SELECT * FROM utility
), progression_ok AS (
 SELECT (SELECT count(*) FROM expected_talents)=90
 AND NOT EXISTS ((SELECT user_id,talent_id,rank,0 outbox_revision FROM expected_talents)
  EXCEPT (SELECT t.user_id,t.talent_id,t.rank,t.outbox_revision
   FROM character_talents t JOIN desired d ON d.expected_id=t.user_id))
 AND NOT EXISTS ((SELECT t.user_id,t.talent_id,t.rank,t.outbox_revision
   FROM character_talents t JOIN desired d ON d.expected_id=t.user_id)
  EXCEPT (SELECT user_id,talent_id,rank,0 FROM expected_talents))
 AND NOT EXISTS ((SELECT user_id,skill_id,skill_level,
                    'max-combat-fixture-v1'::text source FROM expected_skills)
  EXCEPT (SELECT s.user_id,s.skill_id,s.skill_level,s.source
   FROM character_skills s JOIN desired d ON d.expected_id=s.user_id))
 AND NOT EXISTS ((SELECT s.user_id,s.skill_id,s.skill_level,s.source
   FROM character_skills s JOIN desired d ON d.expected_id=s.user_id)
  EXCEPT (SELECT user_id,skill_id,skill_level,'max-combat-fixture-v1'::text
   FROM expected_skills)) AS ok
), pets AS (
 SELECT d.expected_id,d.character_name||'Bond' pet_name,p.*
 FROM desired d LEFT JOIN character_pets p ON p.user_id=d.expected_id
), pet_parent_ok AS (
 SELECT (SELECT count(*) FROM character_pets p JOIN desired d
         ON d.expected_id=p.user_id)=5 AND NOT EXISTS (
  SELECT 1 FROM pets p WHERE p.id IS NULL OR p.name IS DISTINCT FROM p.pet_name
   OR p.species_id IS DISTINCT FROM 45 OR p.sex IS DISTINCT FROM 1 OR p.level IS DISTINCT FROM 120 OR p.experience IS DISTINCT FROM 252947820
   OR p.aptitude IS DISTINCT FROM 16 OR p.rank IS DISTINCT FROM 655.35 OR p.completed_rebirths IS DISTINCT FROM 100
   OR p.rebirths_remaining IS DISTINCT FROM 0 OR p.completed_pet_merges IS DISTINCT FROM 0
   OR p.has_soul_contract IS DISTINCT FROM true
   OR p.has_owner_merge_talent IS DISTINCT FROM true
   OR p.maximum_energy IS DISTINCT FROM 100 OR p.amity IS DISTINCT FROM 100
   OR p.satiety IS DISTINCT FROM 100 OR p.remaining_lifetime IS DISTINCT FROM 1200
   OR p.available_stat_points IS DISTINCT FROM 0 OR p.growth_revealed IS DISTINCT FROM true
   OR p.bound IS DISTINCT FROM true OR p.activity_state IS DISTINCT FROM 'owned'
   OR p.is_carried IS DISTINCT FROM true OR p.is_summoned IS DISTINCT FROM true
   OR (p.expected_id BETWEEN 7001 AND 7004 AND
       (p.current_energy IS DISTINCT FROM 100
        OR p.contributes_to_character IS DISTINCT FROM true
        OR p.revision IS DISTINCT FROM 0))
   OR (p.expected_id=7005 AND
       (p.current_energy<0 OR p.current_energy>100
        OR (p.contributes_to_character AND p.current_energy=0)
        OR p.revision<0))
   OR p.initial_savvy_baseline_total IS DISTINCT FROM 5324
   OR p.initial_savvy_policy_version IS DISTINCT FROM 'project-v3'
   OR p.rarity_added_savvy_baseline_total IS DISTINCT FROM 5324
   OR p.rarity_added_savvy_policy_version IS DISTINCT FROM 'project-v3'
   OR p.initial_savvy_source_version IS DISTINCT FROM 'basic-plus-scaled-growth-v3'
   OR p.talent_mask IS DISTINCT FROM 31 OR p.opened_skill_slots IS DISTINCT FROM 12
   OR p.available_skill_slots IS DISTINCT FROM 12
   OR p.growth_activation_policy_version IS DISTINCT FROM 'weak-until-phoenix-v1'
   OR p.birth_rank IS DISTINCT FROM 3.60 OR p.hatch_rank_roll IS DISTINCT FROM 99
   OR p.hatch_rank_outcome_order IS DISTINCT FROM 2 OR p.hatch_rank_content_revision IS DISTINCT FROM
    'F5CC3B3EFAA33AB275AC35F8A3CF9FAB4DE26D7DAD3E13B90BAB88CC0B09F9FD'
   OR p.soul_contract_stage IS DISTINCT FROM 6
 ) AS ok
), pet_stat_template(stat_code,initial_savvy,added_savvy,base_rate,
 acceleration,birth_savvy,rarity_savvy) AS (VALUES
 (1,15591.6,4400.4,16.67,20,887.34,887.34),
 (2,15591.6,4400.4,16.67,20,887.34,887.34),
 (3,15591.6,4400.4,16.67,20,887.33,887.33),
 (4,15591.6,4400.4,16.67,20,887.33,887.33),
 (5,15592.8,4399.2,16.66,20,887.33,887.33),
 (6,15592.8,4399.2,16.66,20,887.33,887.33)
), expected_pet_stats AS (
 SELECT p.id pet_id,t.* FROM pets p CROSS JOIN pet_stat_template t
), pet_stats_ok AS (
 SELECT NOT EXISTS ((SELECT pet_id,stat_code,initial_savvy,added_savvy,
  acceleration,0 revision,base_rate,birth_savvy,rarity_savvy
  FROM expected_pet_stats) EXCEPT
 (SELECT s.pet_id,s.stat_code,s.initial_savvy,s.added_savvy,
  s.growth_acceleration,s.revision,s.base_growth_rate,
  s.birth_initial_savvy,s.rarity_added_savvy
  FROM character_pet_stat_values s JOIN pets p ON p.id=s.pet_id))
 AND NOT EXISTS ((SELECT s.pet_id,s.stat_code,s.initial_savvy,s.added_savvy,
  s.growth_acceleration,s.revision,s.base_growth_rate,
  s.birth_initial_savvy,s.rarity_added_savvy
  FROM character_pet_stat_values s JOIN pets p ON p.id=s.pet_id) EXCEPT
 (SELECT pet_id,stat_code,initial_savvy,added_savvy,acceleration,0,
  base_rate,birth_savvy,rarity_savvy FROM expected_pet_stats)) AS ok
), pet_skill_template(slot_index,skill_id) AS
 (VALUES(0,3920),(1,4519),(2,4620),(3,5220),(4,5620)),
pet_skills_ok AS (
 SELECT NOT EXISTS ((SELECT p.id,t.skill_id,t.slot_index,6,0,true,0
  FROM pets p CROSS JOIN pet_skill_template t) EXCEPT
 (SELECT s.pet_id,s.skill_id,s.slot_index,s.skill_rank,s.skill_experience,
  s.is_active,s.revision FROM character_pet_skills s JOIN pets p ON p.id=s.pet_id))
 AND NOT EXISTS ((SELECT s.pet_id,s.skill_id,s.slot_index,s.skill_rank,
  s.skill_experience,s.is_active,s.revision FROM character_pet_skills s
  JOIN pets p ON p.id=s.pet_id) EXCEPT
 (SELECT p.id,t.skill_id,t.slot_index,6,0,true,0
  FROM pets p CROSS JOIN pet_skill_template t)) AS ok
), pet_bonus_template(effect_code,effect_value) AS (VALUES
 (0,510075),(1,40786),(2,6092.9),(3,5070.75),(4,30464.5),
 (5,20323),(6,20323),(7,15242.25),(10,15262.25),(23,50807.5),
 (24,40636),(29,122058),(30,101695),(32,152622.5),
 (34,50707.5),(38,60879),(1001,3000),(1002,3000)
), pet_bonuses_ok AS (
 SELECT NOT EXISTS ((SELECT p.id,t.effect_code,t.effect_value,
  '3B503660F9802514F2477DE0B28C1221CFC3C872B46859021555C7FA54DC076E'
  FROM pets p CROSS JOIN pet_bonus_template t
  WHERE p.expected_id BETWEEN 7001 AND 7004
     OR (p.expected_id=7005 AND p.contributes_to_character)) EXCEPT
 (SELECT b.pet_id,b.effect_code,b.effect_value,b.balance_revision
  FROM character_pet_character_bonuses b JOIN pets p ON p.id=b.pet_id))
 AND NOT EXISTS ((SELECT b.pet_id,b.effect_code,b.effect_value,
  b.balance_revision FROM character_pet_character_bonuses b JOIN pets p
  ON p.id=b.pet_id) EXCEPT (SELECT p.id,t.effect_code,t.effect_value,
  '3B503660F9802514F2477DE0B28C1221CFC3C872B46859021555C7FA54DC076E'
  FROM pets p CROSS JOIN pet_bonus_template t
  WHERE p.expected_id BETWEEN 7001 AND 7004
     OR (p.expected_id=7005 AND p.contributes_to_character)))
 AND NOT EXISTS (
  SELECT 1 FROM character_pet_character_bonuses b JOIN pets p ON p.id=b.pet_id
  WHERE (p.expected_id BETWEEN 7001 AND 7004 AND b.revision<>0)
     OR (p.expected_id=7005 AND
         (b.revision<0 OR b.revision>p.revision))
 ) AND NOT EXISTS (
  SELECT p.id FROM pets p JOIN character_pet_character_bonuses b
   ON b.pet_id=p.id WHERE p.expected_id=7005
  GROUP BY p.id HAVING min(b.revision)<>max(b.revision)
 ) AS ok
), zodiac_ok AS (
 SELECT NOT EXISTS (SELECT 1 FROM character_zodiac_skill_grids z
  JOIN desired d ON d.expected_id=z.user_id) AS ok
), domains(name,ok) AS (VALUES
 ('identity',(SELECT ok FROM identity_ok)),
 ('equipment',(SELECT ok FROM equipment_ok)),
 ('progression',(SELECT ok FROM progression_ok)),
 ('pet-parent',(SELECT ok FROM pet_parent_ok)),
 ('pet-stats',(SELECT ok FROM pet_stats_ok)),
 ('pet-skills',(SELECT ok FROM pet_skills_ok)),
 ('pet-bonuses',(SELECT ok FROM pet_bonuses_ok)),
 ('zodiac',(SELECT ok FROM zodiac_ok))
)
SELECT 'MAX_COMBAT_FIXTURE_STATUS|' || json_build_object(
 'applied',(SELECT bool_and(ok) FROM domains),
 'driftDomains',(SELECT COALESCE(json_agg(name ORDER BY name)
                    FILTER(WHERE NOT ok),'[]'::json) FROM domains),
 'accounts',(SELECT count(*) FROM accounts a JOIN desired d ON a.id=d.expected_id),
 'characters',(SELECT count(*) FROM character_base c JOIN desired d ON c.id=d.expected_id),
 'stableIds',(SELECT count(*) FROM identities
              WHERE account_id=expected_id AND character_id=expected_id),
 'equipmentRows',(SELECT count(*) FROM character_items i JOIN desired d
                  ON d.expected_id=i.user_id WHERE i.item_location=0),
 'maxTalentRows',(SELECT count(*) FROM character_talents t JOIN desired d
                  ON d.expected_id=t.user_id AND t.rank=100),
 'skillRows',(SELECT count(*) FROM character_skills s JOIN desired d
              ON d.expected_id=s.user_id),
 'pets',(SELECT count(*) FROM character_pets p JOIN desired d
         ON d.expected_id=p.user_id),
 'savvyRows',(SELECT count(*) FROM character_pet_stat_values s
              JOIN pets p ON p.id=s.pet_id),
 'petSkillRows',(SELECT count(*) FROM character_pet_skills s
                 JOIN pets p ON p.id=s.pet_id),
 'petBonusRows',(SELECT count(*) FROM character_pet_character_bonuses b
                 JOIN pets p ON p.id=b.pet_id),
 'zodiacRows',(SELECT count(*) FROM character_zodiac_skill_grids z
               JOIN desired d ON d.expected_id=z.user_id),
 'identities',(SELECT json_agg(json_build_object(
   'expectedId',expected_id,'username',username,'characterName',character_name,
   'accountId',COALESCE(account_id,0),'characterId',COALESCE(character_id,0))
   ORDER BY expected_id) FROM identities)
)::text;
'@
    $replacements = [ordered]@{
        '__REGULAR_VALUES__' = $regularValues
        '__ARES_BULWARK_HASH__' = 'gws$pbkdf2-sha256$v1$600000$X3ai97nBWQlEwekDRlx6XQ==$C7pivBvf5jYBDIu6XDEPM70wwyIwL38Krd7Y0D5VsSs='
        '__ARES_MIRAGE_HASH__' = 'gws$pbkdf2-sha256$v1$600000$P88aW0u5fjI/FtRnLS7osg==$/FT0LhRB4RwmLq5gIDvyMtCnxKsQR1lp4HglLNdsEkU='
        '__ATHENA_BULWARK_HASH__' = 'gws$pbkdf2-sha256$v1$600000$T110O7doZ1mRNNFo18aXaA==$yT7Tlq9Qh9FpL+uUWTD+mLs96wKHe2mhhXFj4/R3Wq8='
        '__ATHENA_MIRAGE_HASH__' = 'gws$pbkdf2-sha256$v1$600000$mEhv48pAxaEwP8Qxko4g7w==$dwe1oEPL49+xZ2k4M5ZbJc+2zEOPsKn0M31yofGVOSk='
        '__TEST25_HASH__' = 'gws$pbkdf2-sha256$v1$600000$3EIgjUktl5sFyy2YYK3ynQ==$6WxhR6jeTEkdPBelif9J9Gze55MimrguFawh6gSezuw='
    }
    foreach ($entry in $replacements.GetEnumerator()) {
        $sql = $sql.Replace($entry.Key, $entry.Value)
    }
    if ($sql -match '__[A-Z0-9_]+__') {
        throw 'Max-combat Status SQL contains an unresolved placeholder.'
    }
    return $sql
}
