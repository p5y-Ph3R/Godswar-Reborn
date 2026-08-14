Set-StrictMode -Version Latest

function Get-PlatypusSkillKitItemValuesSql {
    @'
(1,25,11080,'Magic Jade: Platypus','396,756',NULL,NULL),
(2,26,10530,'Pet Skill: Focus  I','216,972',4,4600),
(3,27,10531,'Pet Skill:Focus  II','216,972',3,4604),
(4,28,10532,'Pet Skill:Focus  III','216,972',3,4608),
(5,29,10533,'Pet Skill:Focus  IV','216,972',3,4612),
(6,30,10534,'Pet Skill:Focus  V','216,972',3,4616),
(7,31,10535,'Pet Skill:Focus  VI','216,972',3,4620)
'@
}

function Get-PlatypusSkillKitCurveValuesSql {
    @'
(1,0,4600,ARRAY[4600,4601,4602,4603],ARRAY[0,9,13,21]::smallint[],ARRAY[20,33,43,50]::numeric[]),
(2,64,4604,ARRAY[4604,4605,4606,4607],ARRAY[0,21,25,30]::smallint[],ARRAY[50,54,58,63]::numeric[]),
(3,192,4608,ARRAY[4608,4609,4610,4611],ARRAY[0,30,39,48]::smallint[],ARRAY[63,69,76,85]::numeric[]),
(4,235,4612,ARRAY[4612,4613,4614,4615],ARRAY[0,48,55,62]::smallint[],ARRAY[85,89,93,96]::numeric[]),
(5,270,4616,ARRAY[4616,4617,4618,4619],ARRAY[0,62,69,76]::smallint[],ARRAY[96,100,104,108]::numeric[]),
(6,305,4620,ARRAY[4620,4621,4622,4623],ARRAY[0,76,83,90]::smallint[],ARRAY[108,111,115,119]::numeric[])
'@
}

function Get-PlatypusSkillKitExpectedCtesSql {
    @"
expected_kit(ordinal,slot_index,prop_id,display_name,icon,item_type,
             runtime_skill_id) AS (VALUES
$(Get-PlatypusSkillKitItemValuesSql)
), expected_curve(priority,required_accuracy,first_runtime_skill_id,
                  runtime_ids,minimum_ranks,absolute_values) AS (VALUES
$(Get-PlatypusSkillKitCurveValuesSql)
)
"@
}

function Get-PlatypusSkillKitContentCtesSql {
    @'
item_release AS (
 SELECT p.revision,r.source,r.manifest_version,r.entry_count,r.sealed_at
 FROM public.item_template_content_publication p
 JOIN public.item_template_content_revisions r USING(revision)
 WHERE p.family='items'
), item_content AS (
 SELECT count(*) AS exact_rows
 FROM expected_kit e
 JOIN public.item_template_content_definitions d
   ON d.revision=(SELECT revision FROM item_release) AND d.id=e.prop_id
 JOIN public.item_templates m ON m.id=d.id
 WHERE d.kind='consume item' AND d.name_key='Pet'||e.prop_id::text
   AND d.display_name=e.display_name AND d.equipment_slot=0
   AND cardinality(d.class_ids)=0 AND d.min_level IS NULL
   AND d.max_level IS NULL AND d.hand IS NULL AND d.skill_flag IS NULL
   AND d.texture='./Localization/en_us/UI/Texture/Icon2.gwo'
   AND d.icon=e.icon
   AND d.stats=jsonb_strip_nulls(jsonb_build_object(
      'ID',e.prop_id::text,'Type','consume item',
      'Texture','./Localization/en_us/UI/Texture/Icon2.gwo','Icon',e.icon,
      'Random','0','Distribution','0,0','Money','0','Overlap','99',
      'Use',CASE WHEN e.runtime_skill_id IS NULL THEN NULL ELSE '1' END,
      'ItemType',e.item_type::text,
      'PetSkill',e.runtime_skill_id::text))
   AND to_jsonb(m)=to_jsonb(d)-'revision'
), learned_release AS (
 SELECT p.revision,r.source,r.curve_count,r.step_count,r.sealed_at
 FROM public.pet_skill_content_publication p
 JOIN public.pet_skill_content_revisions r USING(revision)
 WHERE p.singleton
), curve_content AS (
 SELECT count(*) FILTER (WHERE c.genre=2 AND c.effect=2
    AND c.opaque_add=1 AND c.opaque_flag=1
    AND c.required_agility=0 AND c.required_strength=0
    AND c.required_accuracy=e.required_accuracy
    AND c.required_technique=0 AND c.required_wisdom=0
    AND c.required_luck=0
    AND c.first_runtime_skill_id=e.first_runtime_skill_id
    AND steps.runtime_ids=e.runtime_ids
    AND steps.minimum_ranks=e.minimum_ranks
    AND steps.absolute_values=e.absolute_values) AS exact_rows
 FROM expected_curve e
 JOIN public.pet_skill_curve_definitions c
   ON c.revision=(SELECT revision FROM learned_release)
  AND c.family_type=413 AND c.priority=e.priority
 JOIN LATERAL (
   SELECT array_agg(s.runtime_skill_id ORDER BY s.step_order) runtime_ids,
          array_agg(s.minimum_pet_rank ORDER BY s.step_order) minimum_ranks,
          array_agg(s.absolute_value ORDER BY s.step_order) absolute_values
   FROM public.pet_skill_curve_steps s
   WHERE s.revision=c.revision AND s.family_type=c.family_type
     AND s.priority=c.priority
 ) steps ON true
), pet_release AS (
 SELECT p.revision,r.source,r.sealed_at
 FROM public.pet_content_publication p
 JOIN public.pet_content_revisions r USING(revision)
 WHERE p.family='pets'
), species_content AS (
 SELECT count(*) AS exact_rows
 FROM public.pet_content_species_definitions s
 WHERE s.revision=(SELECT revision FROM pet_release) AND s.species_id=31
   AND s.display_name='Platypus' AND s.food_kind=2
   AND s.starter_skill_id=4600 AND s.starter_skill_name='Focus I'
   AND s.lifetime_values=ARRAY[1200]::integer[]
   AND s.egg_item_id=10180 AND s.egg_declared_species_id=31
   AND s.magic_jade_item_id=11080
), content_state AS (
 SELECT ir.revision item_revision,lr.revision learned_revision,
        pr.revision pet_revision,
   (ir.revision='1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF'
    AND ir.source='items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+zephyr-v1+mount-speed-v3+pets-v4'
    AND ir.manifest_version=9 AND ir.entry_count=1764
    AND ir.sealed_at IS NOT NULL
    AND ic.exact_rows=7) item_content_valid,
   (lr.revision='64748AC27B0D815B9C30CFF78A7CE8AD519AE83DF528CB5CDFF4374503ABB473'
    AND lr.source='installed-en-us-pet-skill-normalized-v1'
    AND lr.curve_count=384 AND lr.step_count=1655
    AND lr.sealed_at IS NOT NULL AND cc.exact_rows=6
    AND (SELECT count(*) FROM public.pet_skill_curve_definitions c
         WHERE c.revision=lr.revision AND c.family_type=413)=6
    AND (SELECT count(*) FROM public.pet_skill_curve_steps s
         WHERE s.revision=lr.revision AND s.family_type=413)=24
    AND sc.exact_rows=1 AND pr.sealed_at IS NOT NULL)
      activation_content_valid
 FROM item_release ir CROSS JOIN item_content ic
 CROSS JOIN learned_release lr CROSS JOIN curve_content cc
 CROSS JOIN pet_release pr CROSS JOIN species_content sc
)
'@
}

function Get-PlatypusSkillKitStateCtesSql {
    @'
identity_state AS (
 SELECT count(*) FILTER (WHERE a.username='test2' AND a.login_status=0
    AND c.name='test2' AND c.lifecycle_state='active'
    AND c.checkpoint_owner_id IS NULL) AS exact_rows,
    max(c.inventory_revision) inventory_revision
 FROM public.accounts a JOIN public.character_base c ON c.account_id=a.id
 WHERE a.id=13 AND c.id=2
), inventory_state AS (
 SELECT count(*) FILTER (WHERE i.item_location=1) bag_rows,
   COALESCE(sum(i.stack) FILTER (WHERE i.item_location=1),0) bag_units,
   count(*) total_item_rows,
   count(*) FILTER (WHERE i.item_location=1 AND i.slot_index=24
      AND i.prop_id=10104 AND i.stack=94 AND i.bound=0
      AND i.item_quality=1 AND i.item_grade=1 AND i.item_exp=0
      AND i.holy_suit_code=0 AND i.holy_socket_count=0
      AND num_nonnulls(i.attribute1,i.attribute2,i.attribute3,
          i.attribute4,i.attribute5,i.attribute_level1,
          i.attribute_level2,i.attribute_level3,i.attribute_level4,
          i.attribute_level5,i.class_attribute1,i.class_attribute2,
          i.elemental_attribute1,i.elemental_attribute2,
          i.holy_socket1_effect_id,i.holy_socket1_level,
          i.holy_socket2_effect_id,i.holy_socket2_level,
          i.holy_socket3_effect_id,i.holy_socket3_level,
          i.holy_socket4_effect_id,i.holy_socket4_level,
          i.holy_socket5_effect_id,i.holy_socket5_level,
          i.holy_socket6_effect_id,i.holy_socket6_level,
          i.holy_socket1_value,i.holy_socket2_value,
          i.holy_socket3_value,i.holy_socket4_value)=0) source_rows,
   count(*) FILTER (WHERE e.prop_id IS NOT NULL AND i.item_location=1
      AND i.slot_index=e.slot_index AND i.prop_id=e.prop_id
      AND i.stack=1 AND i.bound=0 AND i.item_quality=1
      AND i.item_grade=1 AND i.item_exp=0 AND i.holy_suit_code=0
      AND i.holy_socket_count=0
      AND num_nonnulls(i.attribute1,i.attribute2,i.attribute3,
          i.attribute4,i.attribute5,i.attribute_level1,
          i.attribute_level2,i.attribute_level3,i.attribute_level4,
          i.attribute_level5,i.class_attribute1,i.class_attribute2,
          i.elemental_attribute1,i.elemental_attribute2,
          i.holy_socket1_effect_id,i.holy_socket1_level,
          i.holy_socket2_effect_id,i.holy_socket2_level,
          i.holy_socket3_effect_id,i.holy_socket3_level,
          i.holy_socket4_effect_id,i.holy_socket4_level,
          i.holy_socket5_effect_id,i.holy_socket5_level,
          i.holy_socket6_effect_id,i.holy_socket6_level,
          i.holy_socket1_value,i.holy_socket2_value,
          i.holy_socket3_value,i.holy_socket4_value)=0) kit_rows,
   count(*) FILTER (WHERE i.prop_id IN
      (11080,10530,10531,10532,10533,10534,10535)) target_anywhere,
   encode(sha256(convert_to(COALESCE(jsonb_agg(to_jsonb(i)
      ORDER BY i.item_location,i.slot_index,i.id) FILTER (WHERE NOT
      (i.item_location=1 AND i.slot_index BETWEEN 25 AND 31)),
      '[]'::jsonb)::text,'UTF8')),'hex') preserved_items_hash
 FROM public.character_items i
 LEFT JOIN expected_kit e ON e.slot_index=i.slot_index
   AND i.item_location=1
 WHERE i.user_id=2
), authority_hashes AS (
 SELECT
   encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(to_jsonb(p)
      ORDER BY p.id) FROM public.character_pets p WHERE p.user_id=2),
      '[]'::jsonb)::text,'UTF8')),'hex') pets_hash,
   encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(to_jsonb(s)
      ORDER BY s.pet_id,s.stat_code) FROM public.character_pet_stat_values s
      WHERE s.pet_id IN (SELECT id FROM public.character_pets
                         WHERE user_id=2)),'[]'::jsonb)::text,'UTF8')),'hex') stats_hash,
   encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(to_jsonb(s)
      ORDER BY s.pet_id,s.slot_index) FROM public.character_pet_skills s
      WHERE s.pet_id IN (SELECT id FROM public.character_pets
                         WHERE user_id=2)),'[]'::jsonb)::text,'UTF8')),'hex') skills_hash,
   (SELECT count(*) FROM public.character_pet_growth_previews
    WHERE user_id=2) growth_previews,
   (SELECT count(*) FROM public.character_pet_basic_savvy_previews
    WHERE user_id=2) savvy_previews,
   (SELECT count(*) FROM public.sealed_pet_items
    WHERE owner_character_id=2) sealed_links,
   (SELECT jsonb_build_object('petId',p.id,'speciesId',p.species_id,
       'rank',p.rank,'revision',p.revision,'isCarried',p.is_carried,
       'isSummoned',p.is_summoned,'contributes',p.contributes_to_character)
    FROM public.character_pets p WHERE p.id=1 AND p.user_id=2) main_pet
)
'@
}
