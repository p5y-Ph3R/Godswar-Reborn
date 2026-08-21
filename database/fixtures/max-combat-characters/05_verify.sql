-- Fail the entire serializable transaction unless every requested invariant holds.
SET CONSTRAINTS ALL IMMEDIATE;

DO $fixture_verify$
BEGIN
  IF (SELECT count(*) FROM fixture_context)<>5
     OR EXISTS (
       SELECT 1 FROM fixture_context f JOIN accounts a ON a.id=f.account_id
       JOIN character_base c ON c.id=f.character_id
       WHERE a.username<>f.username OR a.password<>f.password_verifier
          OR a.status<>0 OR a.login_status<>0
          OR c.account_id<>f.account_id OR c.server_id<>1
          OR c.name<>f.character_name OR c.gender<>'male' OR c."GM"<>0
          OR c.profession<>f.profession OR c.camp<>f.camp
          OR c.fighter_job_lv<>160 OR c.scholar_job_lv<>0
          OR c.fighter_job_exp<>0 OR c.scholar_job_exp<>0
          OR c.status<>0 OR c.belief<>1 OR c.prestige<>0 OR c.earl_rank<>0
          OR c."Map"<>f.map_id OR c."Pos_X"<>f.pos_x OR c."Pos_Z"<>f.pos_z
          OR c."MaxHP"<>1500 OR c."MaxMP"<>177
          OR c.character_slot<>0 OR c.lifecycle_state<>'active'
          OR c.lifecycle_version<>1 OR c.deleted_at IS NOT NULL
          OR c.restore_until IS NOT NULL OR c.purge_after IS NOT NULL
          OR c.fighter_level_sealed OR c.pet_shed_capacity<>2
          OR c.holy_suit_points<>
             CASE WHEN f.profession=0 THEN 120 ELSE 110 END
          OR c."Money"<>2147483647 OR c."Stone"<>2147483647
          OR c."SkillPoint"<>2147483647 OR c."SkillExp"<>2147483647
          OR c.zodiac_type<>0 OR c.zodiac_level<>1 OR c.zodiac_energy<>0
          OR c.zodiac_accumulated_exp_x100<>0
          OR c.zodiac_accumulated_talent_exp_x100<>0
          OR c.zodiac_energy_remainder_x100<>0 OR c.zodiac_lucky_status<>0
          OR c.zodiac_lucky_expires_at IS NOT NULL
          OR c.zodiac_online_day IS NOT NULL
          OR c.zodiac_online_duration_ticks<>0
          OR c.zodiac_last_online_at IS NOT NULL
          OR c.zodiac_last_compensation_day IS NOT NULL)
     OR EXISTS (
       SELECT 1 FROM fixture_context f JOIN character_base c ON c.id=f.character_id
       JOIN character_stat_summary s ON s.user_id=c.id
       WHERE c."curHP"<>s.max_hp OR c."curMP"<>s.max_mp) THEN
    RAISE EXCEPTION 'max-combat identity or character readback failed';
  END IF;

  IF (SELECT count(*) FROM character_items i JOIN fixture_context f
      ON f.character_id=i.user_id WHERE i.item_location=0)<>87
     OR EXISTS (
       SELECT 1 FROM fixture_equipment e LEFT JOIN character_items i
         ON i.user_id=e.user_id AND i.item_location=0
        AND i.slot_index=e.slot_index
       WHERE i.id IS NULL OR i.prop_id<>e.prop_id
          OR ARRAY[i.attribute1,i.attribute2,i.attribute3,
                   i.attribute4,i.attribute5] IS DISTINCT FROM e.attrs
          OR ARRAY[i.attribute_level1,i.attribute_level2,
                   i.attribute_level3,i.attribute_level4,
                   i.attribute_level5]
             IS DISTINCT FROM e.attribute_levels
          OR i.item_quality<>20 OR i.item_grade<>25
          OR i.bound<>1 OR i.stack<>1 OR i.item_exp<>0
          OR i.holy_suit_code<>e.holy_suit_code
          OR i.holy_socket_count<>cardinality(e.socket_effects)
          OR i.holy_socket1_effect_id IS DISTINCT FROM e.socket_effects[1]
          OR i.holy_socket2_effect_id IS DISTINCT FROM e.socket_effects[2]
          OR i.holy_socket3_effect_id IS DISTINCT FROM e.socket_effects[3]
          OR i.holy_socket4_effect_id IS DISTINCT FROM e.socket_effects[4]
          OR i.holy_socket1_value IS DISTINCT FROM e.socket_values[1]
          OR i.holy_socket2_value IS DISTINCT FROM e.socket_values[2]
          OR i.holy_socket3_value IS DISTINCT FROM e.socket_values[3]
          OR i.holy_socket4_value IS DISTINCT FROM e.socket_values[4]
          OR i.holy_socket1_level IS DISTINCT FROM
             CASE WHEN e.socket_effects[1] IS NULL THEN NULL ELSE 10 END
          OR i.holy_socket2_level IS DISTINCT FROM
             CASE WHEN e.socket_effects[2] IS NULL THEN NULL ELSE 10 END
          OR i.holy_socket3_level IS DISTINCT FROM
             CASE WHEN e.socket_effects[3] IS NULL THEN NULL ELSE 10 END
          OR i.holy_socket4_level IS DISTINCT FROM
             CASE WHEN e.socket_effects[4] IS NULL THEN NULL ELSE 10 END
          OR i.holy_socket5_effect_id IS NOT NULL
          OR i.holy_socket5_level IS NOT NULL
          OR i.holy_socket6_effect_id IS NOT NULL
          OR i.holy_socket6_level IS NOT NULL
          OR i.class_attribute1 IS DISTINCT FROM e.class_attr
          OR i.class_attribute2 IS NOT NULL
          OR i.elemental_attribute1 IS DISTINCT FROM e.element1
          OR i.elemental_attribute2 IS DISTINCT FROM e.element2)
     OR EXISTS (
       SELECT 1 FROM character_item_validation v JOIN fixture_context f
         ON f.character_id=v.user_id
       WHERE v.item_location=0 AND
        (v.quality_exceeds_item_template OR v.grade_exceeds_item_template)) THEN
    RAISE EXCEPTION 'max-combat equipment readback failed';
  END IF;

  IF (SELECT count(*) FROM character_talents t JOIN fixture_context f
      ON f.character_id=t.user_id)<>90
     OR EXISTS (SELECT 1 FROM fixture_talents f LEFT JOIN character_talents t
                USING(user_id,talent_id)
                WHERE t.talent_id IS NULL OR t.rank<>f.rank
                   OR t.outbox_revision<>0)
     OR EXISTS (SELECT 1 FROM fixture_skills f LEFT JOIN character_skills s
                USING(user_id,skill_id)
                WHERE s.skill_id IS NULL OR s.skill_level<>f.skill_level
                   OR s.source<>'max-combat-fixture-v1')
     OR (SELECT count(*) FROM character_skills s JOIN fixture_context f
         ON f.character_id=s.user_id)<>(SELECT count(*) FROM fixture_skills) THEN
    RAISE EXCEPTION 'max-combat progression readback failed';
  END IF;

  IF (SELECT count(*) FROM fixture_pets)<>5
     OR EXISTS (
       SELECT 1 FROM fixture_pets f JOIN character_pets p ON p.id=f.pet_id
       WHERE p.user_id<>f.character_id OR p.name<>f.pet_name
          OR p.species_id<>45 OR p.sex<>1 OR p.level<>120
          OR p.experience<>252947820 OR p.aptitude<>16
          OR p.rank<>655.35 OR p.completed_rebirths<>100
          OR p.rebirths_remaining<>0 OR p.completed_pet_merges<>0
          OR p.soul_contract_stage<>6 OR NOT p.has_soul_contract
          OR p.talent_mask<>31 OR NOT p.has_owner_merge_talent
          OR p.current_energy<>100 OR p.maximum_energy<>100
          OR p.amity<>100 OR p.satiety<>100
          OR NOT p.is_carried OR NOT p.is_summoned
          OR p.contributes_to_character<>(f.character_id<>7005)
          OR p.remaining_lifetime<>1200
          OR p.available_stat_points<>0 OR NOT p.growth_revealed
          OR NOT p.bound OR p.activity_state<>'owned' OR p.revision<>0
          OR p.initial_savvy_baseline_total<>5324
          OR p.initial_savvy_policy_version<>'project-v3'
          OR p.rarity_added_savvy_baseline_total<>5324
          OR p.rarity_added_savvy_policy_version<>'project-v3'
          OR p.initial_savvy_source_version<>
             'basic-plus-scaled-growth-v3'
          OR p.opened_skill_slots<>12 OR p.available_skill_slots<>12
          OR p.growth_activation_policy_version<>
             'weak-until-phoenix-v1'
          OR p.birth_rank<>3.60 OR p.hatch_rank_roll<>99
          OR p.hatch_rank_outcome_order<>2
          OR p.hatch_rank_content_revision<>
             'F5CC3B3EFAA33AB275AC35F8A3CF9FAB4DE26D7DAD3E13B90BAB88CC0B09F9FD')
     OR (SELECT count(*) FROM character_pet_stat_values s
         JOIN fixture_pets p ON p.pet_id=s.pet_id)<>30
     OR EXISTS (
       SELECT 1 FROM fixture_pets p CROSS JOIN fixture_pet_stats f
       LEFT JOIN character_pet_stat_values s
         ON s.pet_id=p.pet_id AND s.stat_code=f.stat_code
       WHERE s.pet_id IS NULL
          OR s.initial_savvy<>f.initial_savvy
          OR s.added_savvy<>f.added_savvy
          OR s.base_growth_rate<>f.base_growth_rate
          OR s.growth_acceleration<>f.growth_acceleration
          OR s.birth_initial_savvy<>f.birth_initial_savvy
          OR s.rarity_added_savvy<>f.rarity_added_savvy
          OR s.revision<>0
          OR s.initial_savvy+s.added_savvy+8<>20000)
     OR (SELECT count(*) FROM character_pet_skills s
         JOIN fixture_pets p ON p.pet_id=s.pet_id)<>25
     OR EXISTS (
       SELECT 1 FROM fixture_pets p CROSS JOIN fixture_pet_skills f
       LEFT JOIN character_pet_skills s
         ON s.pet_id=p.pet_id AND s.slot_index=f.slot_index
       WHERE s.pet_id IS NULL OR s.skill_id<>f.skill_id
          OR s.skill_rank<>6 OR s.skill_experience<>0
          OR NOT s.is_active OR s.revision<>0)
     OR (SELECT count(*) FROM character_pet_character_bonuses b
         JOIN fixture_pets p ON p.pet_id=b.pet_id)<>72
     OR EXISTS (
       SELECT 1 FROM fixture_pets p CROSS JOIN fixture_pet_bonuses f
       LEFT JOIN character_pet_character_bonuses b
         ON b.pet_id=p.pet_id AND b.effect_code=f.effect_code
       WHERE p.character_id BETWEEN 7001 AND 7004
         AND (b.pet_id IS NULL OR b.effect_value<>f.effect_value
          OR b.revision<>0 OR b.balance_revision<>
            '3B503660F9802514F2477DE0B28C1221CFC3C872B46859021555C7FA54DC076E'))
     OR EXISTS (
       SELECT 1 FROM fixture_pets p
       JOIN character_pet_character_bonuses b ON b.pet_id=p.pet_id
       WHERE p.character_id=7005) THEN
    RAISE EXCEPTION 'max-combat pet readback failed';
  END IF;

  IF EXISTS (SELECT 1 FROM character_zodiac_skill_grids z
             JOIN fixture_context f ON f.character_id=z.user_id) THEN
    RAISE EXCEPTION 'zodiac state was populated despite explicit exclusion';
  END IF;
END
$fixture_verify$;

SELECT 'MAX_COMBAT_FIXTURE_RESULT|' || json_build_object(
 'status','Applied','accounts',5,'characters',5,'equipmentRows',87,
 'maxTalentRows',90,'skillRows',(SELECT count(*) FROM fixture_skills),
 'pets',5,'savvyRows',30,'effectiveSavvyPerStat',20000,
 'petBonusRows',72,
 'zodiacRows',0,
 'identities',(SELECT json_agg(json_build_object(
    'username',username,'characterName',character_name,
    'accountId',account_id,'characterId',character_id,
    'petId',pet_id,'petName',pet_name)
    ORDER BY username)
   FROM fixture_context JOIN fixture_pets USING(character_id)),
 'characterStats',(SELECT json_agg(to_jsonb(s) ORDER BY s.name)
   FROM character_stat_summary s JOIN fixture_context f
     ON f.character_id=s.user_id)
)::text;

COMMIT;
