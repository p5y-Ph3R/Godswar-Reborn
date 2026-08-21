-- One exact max Cupid each; only the four headless dummies pin owner Merge.
CREATE TEMP TABLE fixture_pet_names ON COMMIT DROP AS
SELECT character_id,(character_name || 'Bond')::varchar(32) AS pet_name
FROM fixture_context;

DO $pet_identity_guard$
BEGIN
  IF EXISTS (
      SELECT 1 FROM fixture_pet_names n JOIN character_pets p
        ON p.user_id=n.character_id
      WHERE p.name<>n.pet_name)
     OR EXISTS (
      SELECT p.user_id FROM character_pets p JOIN fixture_pet_names n
        ON n.character_id=p.user_id
      GROUP BY p.user_id HAVING count(*)>1) THEN
    RAISE EXCEPTION 'a fixture character already owns an unrelated pet';
  END IF;
  IF NOT EXISTS (
      SELECT 1 FROM pet_content_publication
      WHERE family='pets' AND revision=
       'F5CC3B3EFAA33AB275AC35F8A3CF9FAB4DE26D7DAD3E13B90BAB88CC0B09F9FD')
     OR NOT EXISTS (
      SELECT 1 FROM pet_owner_merge_content_publication
      WHERE family='pet-owner-merge' AND revision=
       '3B503660F9802514F2477DE0B28C1221CFC3C872B46859021555C7FA54DC076E') THEN
    RAISE EXCEPTION 'reviewed pet or owner-Merge content is not published';
  END IF;
END
$pet_identity_guard$;

INSERT INTO character_pets (
 user_id,species_id,name,sex,level,experience,aptitude,rank,
 completed_rebirths,rebirths_remaining,completed_pet_merges,
 has_soul_contract,has_owner_merge_talent,current_energy,maximum_energy,
 amity,satiety,remaining_lifetime,available_stat_points,growth_revealed,
 bound,activity_state,is_carried,is_summoned,contributes_to_character,
 revision,initial_savvy_baseline_total,initial_savvy_policy_version,
 rarity_added_savvy_baseline_total,rarity_added_savvy_policy_version,
 initial_savvy_source_version,talent_mask,opened_skill_slots,
 available_skill_slots,growth_activation_policy_version,
 birth_rank,hatch_rank_roll,hatch_rank_outcome_order,
 hatch_rank_content_revision,soul_contract_stage)
SELECT n.character_id,45,n.pet_name,1,120,252947820,16,655.35,
 100,0,0,true,true,100,100,100,100,1200,0,true,
 true,'owned',true,true,(n.character_id<>7005),0,
 5324,'project-v3',5324,'project-v3',
 'basic-plus-scaled-growth-v3',31,12,12,'weak-until-phoenix-v1',
 3.60,99,2,
 'F5CC3B3EFAA33AB275AC35F8A3CF9FAB4DE26D7DAD3E13B90BAB88CC0B09F9FD',6
FROM fixture_pet_names n
WHERE NOT EXISTS (SELECT 1 FROM character_pets p
                  WHERE p.user_id=n.character_id AND p.name=n.pet_name);

UPDATE character_pets p SET
 species_id=45,sex=1,level=120,experience=252947820,aptitude=16,
 rank=655.35,completed_rebirths=100,rebirths_remaining=0,
 completed_pet_merges=0,has_soul_contract=true,
 has_owner_merge_talent=true,current_energy=100,maximum_energy=100,
 amity=100,satiety=100,remaining_lifetime=1200,available_stat_points=0,
 growth_revealed=true,bound=true,activity_state='owned',is_carried=true,
 is_summoned=true,contributes_to_character=(n.character_id<>7005),revision=0,
 initial_savvy_baseline_total=5324,initial_savvy_policy_version='project-v3',
 rarity_added_savvy_baseline_total=5324,
 rarity_added_savvy_policy_version='project-v3',
 initial_savvy_source_version='basic-plus-scaled-growth-v3',
 talent_mask=31,opened_skill_slots=12,available_skill_slots=12,
 growth_activation_policy_version='weak-until-phoenix-v1',
 birth_rank=3.60,hatch_rank_roll=99,hatch_rank_outcome_order=2,
 hatch_rank_content_revision=
  'F5CC3B3EFAA33AB275AC35F8A3CF9FAB4DE26D7DAD3E13B90BAB88CC0B09F9FD',
 soul_contract_stage=6,updated_at=now()
FROM fixture_pet_names n
WHERE p.user_id=n.character_id AND p.name=n.pet_name;

CREATE TEMP TABLE fixture_pets ON COMMIT DROP AS
SELECT n.character_id,n.pet_name,p.id AS pet_id
FROM fixture_pet_names n JOIN character_pets p
  ON p.user_id=n.character_id AND p.name=n.pet_name;

CREATE TEMP TABLE fixture_pet_stats (
 stat_code smallint PRIMARY KEY,initial_savvy numeric,added_savvy numeric,
 base_growth_rate numeric,growth_acceleration numeric,
 birth_initial_savvy numeric,rarity_added_savvy numeric
) ON COMMIT DROP;
INSERT INTO fixture_pet_stats VALUES
 (1,15591.6,4400.4,16.67,20,887.34,887.34),
 (2,15591.6,4400.4,16.67,20,887.34,887.34),
 (3,15591.6,4400.4,16.67,20,887.33,887.33),
 (4,15591.6,4400.4,16.67,20,887.33,887.33),
 (5,15592.8,4399.2,16.66,20,887.33,887.33),
 (6,15592.8,4399.2,16.66,20,887.33,887.33);

DELETE FROM character_pet_stat_values s USING fixture_pets p
WHERE s.pet_id=p.pet_id;
INSERT INTO character_pet_stat_values (
 pet_id,stat_code,initial_savvy,added_savvy,growth_acceleration,
 revision,base_growth_rate,birth_initial_savvy,rarity_added_savvy)
SELECT p.pet_id,s.stat_code,s.initial_savvy,s.added_savvy,
 s.growth_acceleration,0,s.base_growth_rate,
 s.birth_initial_savvy,s.rarity_added_savvy
FROM fixture_pets p CROSS JOIN fixture_pet_stats s;

CREATE TEMP TABLE fixture_pet_skills(
 slot_index smallint,skill_id integer,PRIMARY KEY(slot_index)
) ON COMMIT DROP;
INSERT INTO fixture_pet_skills VALUES
 (0,3920),(1,4519),(2,4620),(3,5220),(4,5620);
DELETE FROM character_pet_skills s USING fixture_pets p
WHERE s.pet_id=p.pet_id;
INSERT INTO character_pet_skills(
 pet_id,skill_id,slot_index,skill_rank,skill_experience,is_active,revision)
SELECT p.pet_id,s.skill_id,s.slot_index,6,0,true,0
FROM fixture_pets p CROSS JOIN fixture_pet_skills s;

CREATE TEMP TABLE fixture_pet_bonuses(
 effect_code smallint PRIMARY KEY,effect_value numeric
) ON COMMIT DROP;
INSERT INTO fixture_pet_bonuses VALUES
 (0,510075),(1,40786),(2,6092.9),(3,5070.75),(4,30464.5),
 (5,20323),(6,20323),(7,15242.25),(10,15262.25),(23,50807.5),
 (24,40636),(29,122058),(30,101695),(32,152622.5),
 (34,50707.5),(38,60879),(1001,3000),(1002,3000);
DELETE FROM character_pet_character_bonuses b USING fixture_pets p
WHERE b.pet_id=p.pet_id;
INSERT INTO character_pet_character_bonuses(
 pet_id,effect_code,effect_value,revision,balance_revision)
SELECT p.pet_id,b.effect_code,b.effect_value,0,
 '3B503660F9802514F2477DE0B28C1221CFC3C872B46859021555C7FA54DC076E'
FROM fixture_pets p CROSS JOIN fixture_pet_bonuses b
WHERE p.character_id BETWEEN 7001 AND 7004;

UPDATE character_base c SET "curHP"=s.max_hp,"curMP"=s.max_mp
FROM fixture_context f JOIN character_stat_summary s
  ON s.user_id=f.character_id
WHERE c.id=f.character_id;
