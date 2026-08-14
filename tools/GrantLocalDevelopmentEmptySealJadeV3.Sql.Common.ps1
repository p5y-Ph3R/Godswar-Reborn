Set-StrictMode -Version Latest

function Get-EmptySealJadeV3StateCtesSql {
    @'
identity_state AS (
 SELECT count(*) FILTER (WHERE a.username='test2' AND a.login_status=0
    AND c.name='test2' AND c.lifecycle_state='active'
    AND c.checkpoint_owner_id IS NULL
    AND c.checkpoint_owner_generation=208) exact_rows,
   max(c.inventory_revision) inventory_revision,
   max(c."curHP") current_hp,max(c."curMP") current_mp,
   max(c.vitals_revision) vitals_revision
 FROM public.accounts a JOIN public.character_base c ON c.account_id=a.id
 WHERE a.id=13 AND c.id=2
), inventory_state AS (
 SELECT count(*) FILTER (WHERE i.item_location=1) bag_rows,
   COALESCE(sum(i.stack) FILTER (WHERE i.item_location=1),0) bag_units,
   count(*) total_item_rows,
   count(*) FILTER (WHERE i.item_location=1 AND i.slot_index=24
      AND i.prop_id=10104 AND i.stack=94 AND i.bound=0
      AND i.item_quality=1 AND i.item_grade=1 AND i.item_exp=0
      AND i.holy_suit_code=0 AND i.holy_socket_count=0) source_rows,
   count(*) FILTER (WHERE i.item_location=1 AND i.slot_index=0
      AND i.prop_id=10108 AND i.stack=1 AND i.bound=0
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
          i.holy_socket3_value,i.holy_socket4_value)=0) granted_rows,
   count(*) FILTER (WHERE i.prop_id=10108) target_anywhere,
   (SELECT min(candidate_slot) FROM generate_series(0,95) s(candidate_slot)
    WHERE NOT EXISTS (SELECT 1 FROM public.character_items occupied
      WHERE occupied.user_id=2 AND occupied.item_location=1
        AND occupied.slot_index=s.candidate_slot)) first_empty_slot
 FROM public.character_items i WHERE i.user_id=2
), preserved_state AS (
 SELECT encode(sha256(convert_to(jsonb_build_object(
   'account',(SELECT to_jsonb(a) FROM public.accounts a WHERE a.id=13),
   'character',(SELECT to_jsonb(c)-'inventory_revision'
      FROM public.character_base c WHERE c.id=2),
   'items',COALESCE((SELECT jsonb_agg(to_jsonb(i)
      ORDER BY i.item_location,i.slot_index,i.id)
      FROM public.character_items i WHERE i.user_id=2 AND NOT
       (i.item_location=1 AND i.slot_index=0 AND i.prop_id=10108)),
      '[]'::jsonb),
   'pets',COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id)
      FROM public.character_pets p WHERE p.user_id=2),'[]'::jsonb),
   'stats',COALESCE((SELECT jsonb_agg(to_jsonb(s)
      ORDER BY s.pet_id,s.stat_code) FROM public.character_pet_stat_values s
      WHERE s.pet_id IN (SELECT id FROM public.character_pets
                         WHERE user_id=2)),'[]'::jsonb),
   'skills',COALESCE((SELECT jsonb_agg(to_jsonb(s)
      ORDER BY s.pet_id,s.slot_index) FROM public.character_pet_skills s
      WHERE s.pet_id IN (SELECT id FROM public.character_pets
                         WHERE user_id=2)),'[]'::jsonb),
   'bonuses',COALESCE((SELECT jsonb_agg(to_jsonb(b)
      ORDER BY b.pet_id,b.effect_code)
      FROM public.character_pet_character_bonuses b
      WHERE b.pet_id IN (SELECT id FROM public.character_pets
                         WHERE user_id=2)),'[]'::jsonb),
   'growth',COALESCE((SELECT jsonb_agg(to_jsonb(g) ORDER BY g.pet_id)
      FROM public.character_pet_growth_previews g WHERE g.user_id=2),
      '[]'::jsonb),
   'savvy',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.pet_id)
      FROM public.character_pet_basic_savvy_previews s WHERE s.user_id=2),
      '[]'::jsonb),
   'sealed',COALESCE((SELECT jsonb_agg(to_jsonb(l) ORDER BY l.id)
      FROM public.sealed_pet_items l WHERE l.owner_character_id=2),
      '[]'::jsonb),
   'stream',(SELECT to_jsonb(v) FROM public.pet_durable_stream_versions v
      WHERE v.character_id=2)
 )::text,'UTF8')),'hex') preserved_hash
), main_pet AS (
 SELECT jsonb_build_object('id',p.id,'revision',p.revision,
   'energy',p.current_energy,'maximumEnergy',p.maximum_energy,
   'level',p.level,'rank',p.rank,'experience',p.experience,
   'bound',p.bound,'activity',p.activity_state,'carried',p.is_carried,
   'summoned',p.is_summoned) state
 FROM public.character_pets p WHERE p.id=1 AND p.user_id=2
), content_state AS (
 SELECT p.revision,
   (p.revision=
    '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF'
    AND r.source=
    'items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+zephyr-v1+mount-speed-v3+pets-v4'
    AND r.manifest_version=9 AND r.entry_count=1764
    AND r.sealed_at IS NOT NULL AND d.id=10108
    AND d.kind='consume item' AND d.name_key='Pet10108'
    AND d.display_name='Seal Jade (Empty)' AND d.equipment_slot=0
    AND d.texture='./Localization/en_us/UI/Texture/Icon2.gwo'
    AND d.icon='936,972' AND d.stats->>'Overlap'='99'
    AND to_jsonb(m)=to_jsonb(d)-'revision') content_valid
 FROM public.item_template_content_publication p
 JOIN public.item_template_content_revisions r USING(revision)
 JOIN public.item_template_content_definitions d ON d.revision=p.revision
 JOIN public.item_templates m ON m.id=d.id
 WHERE p.family='items' AND d.id=10108
)
'@
}

function Get-EmptySealJadeV3PreservedHash {
    'ae7d16d74fee3639477dad0a00cf4534ce6d8cceaa42819396ca237995bc9033'
}
