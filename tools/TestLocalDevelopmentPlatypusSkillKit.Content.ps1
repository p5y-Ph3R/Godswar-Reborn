Set-StrictMode -Version Latest

function Get-PlatypusSkillKitDisposableContentSql {
    @'
BEGIN;
SET LOCAL session_replication_role='replica';
DO $stage$
DECLARE
 v_source constant text :=
  'BCF91FCD7A9E3C5EA93B774143B5D2F9B714B147E40EBF0B85C639CF0DD63057';
 v_target constant text :=
  '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF';
 v_publication text;
BEGIN
 SELECT revision INTO v_publication
 FROM public.item_template_content_publication WHERE family='items';
 IF v_publication=v_target THEN RETURN; END IF;
 IF v_publication<>v_source THEN
   RAISE EXCEPTION 'Disposable source is not official pets-v3.';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM public.item_template_content_revisions
                WHERE revision=v_target) THEN
   INSERT INTO public.item_template_content_revisions(
    revision,entry_count,source,created_at,sealed_at,manifest_version,
    attribute_count,equipment_rank_count,holy_suit_effect_count,
    material_policy_count,material_recipe_count,holy_suit_tier_count,
    holy_suit_upgrade_count,holy_suit_consumable_count,
    holy_suit_policy_count)
   SELECT v_target,entry_count+6,
    'items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+'||
    'zephyr-v1+mount-speed-v3+pets-v4',now(),now(),manifest_version,
    attribute_count,equipment_rank_count,holy_suit_effect_count,
    material_policy_count,material_recipe_count,holy_suit_tier_count,
    holy_suit_upgrade_count,holy_suit_consumable_count,
    holy_suit_policy_count
   FROM public.item_template_content_revisions WHERE revision=v_source;
   INSERT INTO public.item_template_content_definitions(
    revision,id,kind,name_key,display_name,equipment_slot,class_ids,
    min_level,max_level,hand,skill_flag,texture,icon,stats)
   SELECT v_target,id,kind,name_key,display_name,equipment_slot,class_ids,
    min_level,max_level,hand,skill_flag,texture,icon,stats
   FROM public.item_template_content_definitions WHERE revision=v_source;
   INSERT INTO public.item_template_content_definitions(
    revision,id,kind,name_key,display_name,equipment_slot,class_ids,
    min_level,max_level,hand,skill_flag,texture,icon,stats)
   SELECT v_target,10530+n,'consume item','Pet'||(10530+n)::text,
    (ARRAY['Pet Skill: Focus  I','Pet Skill:Focus  II',
      'Pet Skill:Focus  III','Pet Skill:Focus  IV',
      'Pet Skill:Focus  V','Pet Skill:Focus  VI'])[n+1],
    0,ARRAY[]::smallint[],NULL,NULL,NULL,NULL,
    './Localization/en_us/UI/Texture/Icon2.gwo','216,972',
    jsonb_build_object('ID',(10530+n)::text,'Type','consume item',
      'Texture','./Localization/en_us/UI/Texture/Icon2.gwo',
      'Icon','216,972','Random','0','Distribution','0,0','Money','0',
      'Overlap','99','Use','1','ItemType',CASE WHEN n=0 THEN '4' ELSE '3' END,
      'PetSkill',(ARRAY[4600,4604,4608,4612,4616,4620])[n+1]::text)
   FROM generate_series(0,5) n;
 END IF;
 INSERT INTO public.item_templates(
   id,kind,name_key,display_name,equipment_slot,class_ids,min_level,
   max_level,hand,skill_flag,texture,icon,stats)
 SELECT d.id,d.kind,d.name_key,d.display_name,d.equipment_slot,d.class_ids,
        d.min_level,d.max_level,d.hand,d.skill_flag,d.texture,d.icon,d.stats
 FROM public.item_template_content_definitions d
 WHERE d.revision=v_target AND d.id BETWEEN 10530 AND 10535
 ON CONFLICT (id) DO UPDATE SET kind=EXCLUDED.kind,
   name_key=EXCLUDED.name_key,display_name=EXCLUDED.display_name,
   equipment_slot=EXCLUDED.equipment_slot,class_ids=EXCLUDED.class_ids,
   min_level=EXCLUDED.min_level,max_level=EXCLUDED.max_level,
   hand=EXCLUDED.hand,skill_flag=EXCLUDED.skill_flag,
   texture=EXCLUDED.texture,icon=EXCLUDED.icon,stats=EXCLUDED.stats;
 UPDATE public.item_template_content_publication
 SET revision=v_target,published_at=now() WHERE family='items';
END
$stage$;
COMMIT;
'@
}
