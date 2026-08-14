Set-StrictMode -Version Latest

function Get-PetUnsealVitalsEnergyCalculatedMaximaCtesSql {
    @'
content_pins AS (
 SELECT item.revision item_revision,gameplay.revision gameplay_revision,
        pet_skill.revision pet_skill_revision,
        owner_merge.revision owner_merge_revision,
        item.revision=
          '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF'
        AND gameplay.revision=
          '897AB928AE2AA8168E39D1A8909D35349ED5FFE337D2C5C132005CC81C71CA62'
        AND pet_skill.revision=
          '64748AC27B0D815B9C30CFF78A7CE8AD519AE83DF528CB5CDFF4374503ABB473'
        AND owner_merge.revision=
          'E6A6FA22C0D2AEE9D6B2E7C968D903E05E0576E6D40A179DC2A3715F434A4929'
        AND EXISTS (SELECT 1
          FROM public.item_template_content_revisions revision
          WHERE revision.revision=item.revision
            AND revision.manifest_version=9
            AND revision.entry_count=1764
            AND revision.sealed_at IS NOT NULL)
        AND EXISTS (SELECT 1
          FROM public.gameplay_content_revisions revision
          WHERE revision.revision=gameplay.revision)
        AND EXISTS (SELECT 1
          FROM public.pet_skill_content_revisions revision
          WHERE revision.revision=pet_skill.revision
            AND revision.sealed_at IS NOT NULL)
        AND EXISTS (SELECT 1
          FROM public.pet_owner_merge_content_revisions revision
          WHERE revision.revision=owner_merge.revision
            AND revision.sealed_at IS NOT NULL) pins_valid
 FROM public.item_template_content_publication item
 CROSS JOIN public.gameplay_content_publication gameplay
 CROSS JOIN public.pet_skill_content_publication pet_skill
 CROSS JOIN public.pet_owner_merge_content_publication owner_merge
 WHERE item.family='items' AND gameplay.family='gameplay'
   AND pet_skill.singleton AND owner_merge.family='pet-owner-merge'
),
valid_mount_loadouts AS (
 SELECT mount.user_id,COALESCE(template.min_level,1) mount_level
 FROM public.character_items mount
 JOIN public.character_base owner ON owner.id=mount.user_id
 CROSS JOIN content_pins pins
 JOIN public.item_template_content_definitions template
   ON template.revision=pins.item_revision
  AND template.id=mount.prop_id AND template.kind='mount'
  AND template.equipment_slot=20
 WHERE mount.item_location=0 AND mount.slot_index=20
   AND mount.user_id=2
   AND owner.fighter_job_lv>=COALESCE(template.min_level,1)
   AND (template.max_level IS NULL
        OR owner.fighter_job_lv<=template.max_level)
   AND (cardinality(template.class_ids)=0
        OR owner.profession=ANY(template.class_ids))
),
mount_spirit_candidates AS (
 SELECT gear.id item_instance_id,gear.user_id,gear.slot_index,
        gear.item_quality,gear.item_grade,gear.attribute1,
        gear.attribute2,gear.attribute3,gear.attribute4,gear.attribute5,
        template.kind,template.stats,socket.effect_id,
        socket.effectiveness_value,
        row_number() OVER (PARTITION BY gear.id,socket.effect_id
          ORDER BY socket.effectiveness_value DESC,socket.socket_index)
          host_roll_rank
 FROM public.character_items gear
 JOIN public.character_base owner ON owner.id=gear.user_id
 JOIN valid_mount_loadouts mount ON mount.user_id=gear.user_id
 CROSS JOIN content_pins pins
 JOIN public.item_template_content_definitions template
   ON template.revision=pins.item_revision
  AND template.id=gear.prop_id
  AND template.equipment_slot=gear.slot_index
 CROSS JOIN LATERAL (VALUES
   (1,gear.holy_socket1_effect_id,gear.holy_socket1_level,
      gear.holy_socket1_value),
   (2,gear.holy_socket2_effect_id,gear.holy_socket2_level,
      gear.holy_socket2_value)
 ) socket(socket_index,effect_id,effect_level,effectiveness_value)
 WHERE gear.item_location=0 AND gear.slot_index BETWEEN 15 AND 19
   AND (gear.slot_index=15 AND template.kind='mounthead'
     OR gear.slot_index=16 AND template.kind='mountarmor'
     OR gear.slot_index=17 AND template.kind='mountsoul'
     OR gear.slot_index=18 AND template.kind='mountornament'
     OR gear.slot_index=19 AND template.kind='mountamulet')
   AND owner.fighter_job_lv>=COALESCE(template.min_level,1)
   AND (template.max_level IS NULL
        OR owner.fighter_job_lv<=template.max_level)
   AND mount.mount_level>=COALESCE(template.min_level,1)
   AND (cardinality(template.class_ids)=0
        OR owner.profession=ANY(template.class_ids))
   AND gear.holy_socket_count BETWEEN 1 AND 2
   AND socket.socket_index<=gear.holy_socket_count
   AND socket.effect_id IN (21,22)
   AND socket.effect_level BETWEEN 1 AND 10
   AND socket.effectiveness_value BETWEEN
       CASE socket.effect_id WHEN 21 THEN 15*socket.effect_level
                             WHEN 22 THEN 10*socket.effect_level END
       AND
       CASE socket.effect_id WHEN 21 THEN 30*socket.effect_level
                             WHEN 22 THEN 20*socket.effect_level END
),
mount_spirit_hosts AS (
 SELECT selected.* FROM (
   SELECT candidate.*,
          row_number() OVER (PARTITION BY candidate.user_id,
            candidate.effect_id
            ORDER BY candidate.effectiveness_value DESC,
                     candidate.item_instance_id) loadout_roll_rank
   FROM mount_spirit_candidates candidate
   WHERE candidate.host_roll_rank=1
 ) selected WHERE selected.loadout_roll_rank<=2
),
mount_attunement_stats AS (
 SELECT host.user_id,'max_hp' stat_name,
        COALESCE(NULLIF(values.values[
          LEAST(GREATEST(host.item_quality::integer,1),
                array_length(values.values,1))], '')::numeric,0) *
          host.effectiveness_value/10000::numeric stat_value
 FROM mount_spirit_hosts host
 JOIN LATERAL (SELECT string_to_array(
        host.stats->>'MaxHP',',') values
      WHERE host.stats?'MaxHP') values ON true
 WHERE host.effect_id=21
   AND host.kind IN ('mountarmor','mountornament')
),
mount_tempering_stats AS (
 SELECT host.user_id,
        CASE attribute_template.stat_type
          WHEN 13 THEN 'max_hp' WHEN 14 THEN 'max_mp' END stat_name,
        attribute_template.level_values[
          LEAST(GREATEST(host.item_grade::integer,1),
                array_length(attribute_template.level_values,1))] *
          host.effectiveness_value/10000::numeric stat_value
 FROM mount_spirit_hosts host
 CROSS JOIN content_pins pins
 CROSS JOIN LATERAL (VALUES(host.attribute1),(host.attribute2),
   (host.attribute3),(host.attribute4),(host.attribute5))
   attribute(attribute_id)
 JOIN public.item_attribute_content_definitions attribute_template
   ON attribute_template.revision=pins.item_revision
  AND attribute_template.id=attribute.attribute_id
 WHERE host.effect_id=22 AND attribute.attribute_id>=0
   AND attribute_template.stat_type IN (13,14)
),
equipment_stats AS (
 SELECT equipment.user_id,stat.stat_name,
        COALESCE(NULLIF(values.values[
          LEAST(GREATEST(equipment.item_quality::integer,1),
                array_length(values.values,1))], '')::numeric,0) stat_value
 FROM public.character_items equipment
 CROSS JOIN content_pins pins
 JOIN public.item_template_content_definitions template
   ON template.revision=pins.item_revision
  AND template.id=equipment.prop_id
 CROSS JOIN (VALUES ('MaxHP','max_hp'),('MaxMP','max_mp'))
   stat(source_key,stat_name)
 JOIN LATERAL (SELECT string_to_array(
        template.stats->>stat.source_key,',') values
      WHERE template.stats?stat.source_key) values ON true
 WHERE equipment.user_id=2 AND equipment.item_location=0
),
attribute_stats AS (
 SELECT equipment.user_id,
        CASE template.stat_type
          WHEN 13 THEN 'max_hp' WHEN 14 THEN 'max_mp' END stat_name,
        CASE WHEN template.percent THEN value.value*10000
             ELSE value.value END stat_value
 FROM public.character_items equipment
 CROSS JOIN content_pins pins
 CROSS JOIN LATERAL (VALUES(equipment.attribute1),
   (equipment.attribute2),(equipment.attribute3),
   (equipment.attribute4),(equipment.attribute5),
   (equipment.class_attribute1)) attribute(attribute_id)
 JOIN public.item_attribute_content_definitions template
   ON template.revision=pins.item_revision
  AND template.id=attribute.attribute_id
 CROSS JOIN LATERAL (SELECT template.level_values[
   LEAST(GREATEST(equipment.item_grade::integer,1),
         array_length(template.level_values,1))] value) value
 WHERE equipment.user_id=2 AND equipment.item_location=0
   AND attribute.attribute_id>=0 AND template.stat_type IN (13,14)
   AND value.value IS NOT NULL
),
talent_stats AS (
 SELECT talent.user_id,
        CASE effect.key WHEN 'MaxHP' THEN 'max_hp'
                        WHEN 'MaxMP' THEN 'max_mp' END stat_name,
        public.talent_effective_rank(talent.rank) *
          CASE WHEN template.is_percent THEN template.effect_value*10000
               ELSE template.effect_value END stat_value
 FROM public.character_talents talent
 JOIN public.character_base owner ON owner.id=talent.user_id
 CROSS JOIN content_pins pins
 JOIN public.gameplay_talent_definitions template
   ON template.revision=pins.gameplay_revision
  AND template.id=talent.talent_id
  AND template.class_id=owner.profession
 JOIN public.gameplay_talent_effect_definitions effect
   ON effect.revision=template.revision AND effect.id=template.effect_id
 WHERE talent.user_id=2 AND talent.rank>0
   AND effect.key IN ('MaxHP','MaxMP')
),
holy_suit_stats AS (
 SELECT owner.id user_id,
        CASE effect.effect_key WHEN 'MaxHPD' THEN 'max_hp'
                               WHEN 'MaxMPD' THEN 'max_mp' END stat_name,
        effect.effect_value stat_value
 FROM public.character_base owner
 CROSS JOIN content_pins pins
 JOIN public.holy_suit_effect_content_definitions effect
   ON effect.revision=pins.item_revision
  AND owner.holy_suit_points>=effect.unlock_points
 WHERE owner.id=2 AND effect.effect_key IN ('MaxHPD','MaxMPD')
),
pet_owner_merge_stats AS (
 SELECT pet.user_id,
        CASE bonus.effect_code WHEN 0 THEN 'max_hp'
                               WHEN 1 THEN 'max_mp' END stat_name,
        bonus.effect_value stat_value
 FROM public.character_pets pet
 JOIN public.character_pet_character_bonuses bonus ON bonus.pet_id=pet.id
 WHERE pet.user_id=2 AND pet.contributes_to_character
   AND bonus.effect_code IN (0,1)
),
pet_learned_skill_stats AS (
 SELECT pet.user_id,'max_hp' stat_name,step.absolute_value stat_value
 FROM public.character_pets pet
 CROSS JOIN content_pins pins
 JOIN public.character_pet_skills skill
   ON skill.pet_id=pet.id AND skill.is_active
 JOIN public.pet_skill_curve_definitions curve
   ON curve.revision=pins.pet_skill_revision
  AND curve.first_runtime_skill_id=skill.skill_id
  AND curve.priority=skill.skill_rank
  AND curve.family_type IN (408,412,413,419,423)
  AND curve.effect=0
 JOIN LATERAL (
   SELECT candidate.absolute_value
   FROM public.pet_skill_curve_steps candidate
   WHERE candidate.revision=curve.revision
     AND candidate.family_type=curve.family_type
     AND candidate.priority=curve.priority
     AND candidate.minimum_pet_rank::numeric<=pet.rank
   ORDER BY candidate.minimum_pet_rank DESC LIMIT 1
 ) step ON true
 WHERE pet.user_id=2 AND pet.activity_state='owned' AND pet.is_carried
),
all_max_stats AS (
 SELECT * FROM equipment_stats UNION ALL SELECT * FROM attribute_stats
 UNION ALL SELECT * FROM talent_stats UNION ALL SELECT * FROM holy_suit_stats
 UNION ALL SELECT * FROM mount_attunement_stats
 UNION ALL SELECT * FROM mount_tempering_stats
 UNION ALL SELECT * FROM pet_owner_merge_stats
 UNION ALL SELECT * FROM pet_learned_skill_stats
),
max_stat_totals AS (
 SELECT user_id,
   COALESCE(sum(stat_value) FILTER (WHERE stat_name='max_hp'),0) max_hp,
   COALESCE(sum(stat_value) FILTER (WHERE stat_name='max_mp'),0) max_mp
 FROM all_max_stats GROUP BY user_id
),
calculated_maxima AS (
 SELECT owner.id character_id,
   GREATEST(1,round(owner."MaxHP"+COALESCE(stats.max_hp,0)))::integer
     maximum_hp,
   GREATEST(0,round(owner."MaxMP"+COALESCE(stats.max_mp,0)))::integer
     maximum_mp,
   COALESCE(stats.max_hp,0) derived_max_hp,
   COALESCE(stats.max_mp,0) derived_max_mp
 FROM public.character_base owner
 LEFT JOIN max_stat_totals stats ON stats.user_id=owner.id
 WHERE owner.id=2 AND owner.account_id=13
)
'@
}

function Get-PetUnsealVitalsEnergyStateCtesSql {
    @'
identity_state AS (
 SELECT count(*) FILTER (WHERE account.username='test2'
    AND account.login_status=0 AND account.status=0
    AND account.last_login_time=
      '2026-08-14 03:54:27.136774+00'::timestamptz
    AND account.last_logout_time=
      '2026-08-14 03:55:32.027884+00'::timestamptz
    AND owner.name='test2' AND owner.lifecycle_state='active'
    AND owner.checkpoint_owner_id IS NULL
    AND owner.checkpoint_owner_generation=207) exact_rows,
   min(to_jsonb(owner)->>'curHP')::integer current_hp,
   min(to_jsonb(owner)->>'curMP')::integer current_mp,
   min(owner.vitals_revision) vitals_revision,
   min(owner."MaxHP") base_maximum_hp,
   min(owner."MaxMP") base_maximum_mp
 FROM public.accounts account
 JOIN public.character_base owner ON owner.account_id=account.id
 WHERE account.id=13 AND owner.id=2
),
pet_state AS (
 SELECT count(*) FILTER (WHERE pet.user_id=2 AND pet.name='Jolo'
    AND pet.species_id=31 AND pet.level=120 AND pet.rank=100
    AND pet.activity_state='owned' AND pet.is_carried
    AND pet.is_summoned AND NOT pet.contributes_to_character
    AND pet.bound AND pet.maximum_energy=100
    AND pet.updated_at=
      '2026-08-14 03:55:19.547448+00'::timestamptz) exact_rows,
   min(pet.current_energy) current_energy,
   min(pet.maximum_energy) maximum_energy,
   min(pet.revision) pet_revision
 FROM public.character_pets pet WHERE pet.id=1
),
source_evidence AS (
 SELECT
   EXISTS (SELECT 1 FROM public.pet_operation_audit audit
     WHERE audit.id=711 AND audit.user_id_snapshot=2
       AND audit.pet_id_snapshot=1 AND audit.operation='owner_merge'
       AND audit.outcome='committed' AND audit.reason_code='SessionEnded'
       AND audit.before_state->>'currentEnergy'='31'
       AND audit.after_state->>'currentEnergy'='31') energy_source_valid,
   EXISTS (SELECT 1 FROM public.command_audit audit
     WHERE audit.id=9263 AND audit.principal_key='13'
       AND audit.aggregate_key='character:2'
       AND audit.command_family='pet_manager_utility'
       AND audit.outcome_code='committed'
       AND audit.detail_payload->>'status'='81') seal_valid,
   EXISTS (SELECT 1 FROM public.command_audit audit
     WHERE audit.id=9266 AND audit.principal_key='13'
       AND audit.aggregate_key='character:2'
       AND audit.command_family='pet_manager_utility'
       AND audit.outcome_code='committed'
       AND audit.detail_payload->>'status'='82') unseal_valid,
   EXISTS (SELECT 1 FROM public.command_audit audit
     WHERE audit.id=9267 AND audit.principal_key='13'
       AND audit.aggregate_key='character:2'
       AND audit.command_family='pet_owner_merge_toggle'
       AND audit.outcome_code='rejected'
       AND audit.detail_payload->>'status'='32') rejection_valid,
   EXISTS (SELECT 1 FROM public.pet_operation_audit audit
     WHERE audit.id=728 AND audit.user_id_snapshot=2
       AND audit.pet_id_snapshot=1 AND audit.operation='unseal'
       AND audit.outcome='committed'
       AND audit.after_state#>>'{pet,Revision}'='1411'
       AND (audit.after_state#>>'{pet,IsCarried}')::boolean
       AND (audit.after_state#>>'{pet,IsSummoned}')::boolean) pet_unseal_valid,
   EXISTS (SELECT 1 FROM public.pet_operation_audit audit
     WHERE audit.id=729 AND audit.user_id_snapshot=2
       AND audit.pet_id_snapshot=1 AND audit.operation='owner_merge'
       AND audit.outcome='rejected'
       AND audit.reason_code='OwnerMergeEnergyNotFull') pet_rejection_valid
),
preservation_hashes AS (
 SELECT
   (SELECT encode(sha256(convert_to(to_jsonb(account)::text,'UTF8')),'hex')
      FROM public.accounts account WHERE account.id=13) account_hash,
   (SELECT encode(sha256(convert_to((to_jsonb(owner)-'curHP'-'curMP'-
       'vitals_revision')::text,'UTF8')),'hex')
      FROM public.character_base owner WHERE owner.id=2) character_hash,
   (SELECT encode(sha256(convert_to((to_jsonb(pet)-'current_energy'-
       'revision')::text,'UTF8')),'hex')
      FROM public.character_pets pet WHERE pet.id=1) pet_hash,
   encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(to_jsonb(pet)
       ORDER BY pet.id) FROM public.character_pets pet
       WHERE pet.user_id=2 AND pet.id<>1),'[]'::jsonb)::text,'UTF8')),'hex')
       other_pets_hash,
   encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(to_jsonb(item)
       ORDER BY item.item_location,item.slot_index,item.id)
       FROM public.character_items item WHERE item.user_id=2),
       '[]'::jsonb)::text,'UTF8')),'hex') items_hash,
   encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(to_jsonb(skill)
       ORDER BY skill.pet_id,skill.slot_index)
       FROM public.character_pet_skills skill WHERE skill.pet_id IN
         (SELECT id FROM public.character_pets WHERE user_id=2)),
       '[]'::jsonb)::text,'UTF8')),'hex') skills_hash,
   encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(to_jsonb(stat)
       ORDER BY stat.pet_id,stat.stat_code)
       FROM public.character_pet_stat_values stat WHERE stat.pet_id IN
         (SELECT id FROM public.character_pets WHERE user_id=2)),
       '[]'::jsonb)::text,'UTF8')),'hex') pet_stats_hash,
   encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(to_jsonb(bonus)
       ORDER BY bonus.pet_id,bonus.effect_code)
       FROM public.character_pet_character_bonuses bonus WHERE bonus.pet_id IN
         (SELECT id FROM public.character_pets WHERE user_id=2)),
       '[]'::jsonb)::text,'UTF8')),'hex') owner_bonuses_hash,
   encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(to_jsonb(link)
       ORDER BY link.id) FROM public.sealed_pet_items link
       WHERE link.owner_character_id=2),'[]'::jsonb)::text,'UTF8')),'hex')
       sealed_links_hash
)
'@
}
