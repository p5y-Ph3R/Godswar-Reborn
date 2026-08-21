-- Exact Q20/G25 Adamantium combat gear and max mount equipment.
CREATE TEMP TABLE fixture_regular (
  build_key text,slot_index smallint,prop_id integer,
  attrs smallint[],class_attr smallint,element1 smallint,element2 smallint,
  socket_effects smallint[],socket_values smallint[],
  PRIMARY KEY(build_key,slot_index)
) ON COMMIT DROP;

INSERT INTO fixture_regular VALUES
 ('warrior',0,2534,'{40,60,80,134,144}',201,485,491,'{1,3,5,7}','{800,500,400,600}'),
 ('warrior',1,3134,'{70,104,144,170,180}',201,484,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('warrior',2,2834,'{4,40,60,80,240}',201,485,491,'{1,3,5,7}','{800,500,400,600}'),
 ('warrior',3,2134,'{14,34,104,134,170}',201,484,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('warrior',4,2634,'{14,34,70,154,170}',201,484,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('warrior',5,3034,'{14,34,70,104,134}',201,484,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('warrior',6,2934,'{70,104,134,154,170}',201,484,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('warrior',7,2734,'{14,34,70,154,170}',201,484,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('warrior',8,3236,'{4,60,80,134,240}',201,485,491,'{1,3,5,7}','{800,500,400,600}'),
 ('warrior',9,3236,'{4,60,80,134,240}',201,485,491,'{1,3,5,7}','{800,500,400,600}'),
 ('warrior',10,1035,'{4,40,60,80,240}',201,483,489,'{1,3,5,7}','{800,500,400,600}'),
 ('warrior',11,2034,'{14,34,104,134,170}',201,484,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_dodge',0,2544,'{4,80,40,60,240}',201,494,491,'{1,3,5,7}','{800,500,400,600}'),
 ('champion_dodge',1,3144,'{50,70,104,144,170}',201,493,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_dodge',2,2844,'{4,40,80,60,240}',201,494,491,'{1,3,5,7}','{800,500,400,600}'),
 ('champion_dodge',3,2144,'{14,34,50,104,134}',201,493,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_dodge',4,2644,'{14,34,40,70,170}',201,493,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_dodge',5,3044,'{14,34,70,104,134}',201,493,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_dodge',6,2944,'{50,70,104,134,170}',201,493,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_dodge',7,2744,'{14,34,50,70,154}',201,493,490,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_dodge',8,3246,'{4,80,240,60,134}',201,494,491,'{1,3,5,7}','{800,500,400,600}'),
 ('champion_dodge',9,3246,'{4,80,240,60,134}',201,494,491,'{1,3,5,7}','{800,500,400,600}'),
 ('champion_dodge',10,1435,'{4,40,60,80,240}',201,492,489,'{1,3,5,7}','{800,500,400,600}'),
 ('champion_glass',0,2344,'{4,80,40,60,240}',210,482,488,'{1,3,5,7}','{800,500,400,600}'),
 ('champion_glass',1,3144,'{50,70,104,144,170}',210,481,487,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_glass',2,2844,'{4,40,80,60,240}',210,482,488,'{1,3,5,7}','{800,500,400,600}'),
 ('champion_glass',3,2144,'{14,34,50,104,134}',210,481,487,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_glass',4,2644,'{14,34,40,70,170}',210,481,487,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_glass',5,3044,'{14,34,70,104,134}',210,481,487,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_glass',6,2944,'{50,70,104,134,170}',210,481,487,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_glass',7,2744,'{14,34,50,70,154}',210,481,487,'{9,11,13,14}','{550,400,700,1000}'),
 ('champion_glass',8,3246,'{4,80,240,60,134}',210,482,488,'{1,3,5,7}','{800,500,400,600}'),
 ('champion_glass',9,3246,'{4,80,240,60,134}',210,482,488,'{1,3,5,7}','{800,500,400,600}'),
 ('champion_glass',10,1435,'{4,40,60,80,240}',210,480,486,'{1,3,5,7}','{800,500,400,600}');

CREATE TEMP TABLE fixture_mount (
  build_key text,slot_index smallint,prop_id integer,attrs smallint[],
  socket_effects smallint[],socket_values smallint[],
  PRIMARY KEY(build_key,slot_index)
) ON COMMIT DROP;

INSERT INTO fixture_mount
SELECT b.build_key,s.slot_index,14508+(s.slot_index-15)*100,
  CASE b.build_key
    WHEN 'warrior' THEN '{307,317,327,387,397}'::smallint[]
    WHEN 'champion_dodge' THEN '{307,317,327,336,387}'::smallint[]
    ELSE '{347,367,407,427,447}'::smallint[] END,
  '{21,22}'::smallint[],'{300,200}'::smallint[]
FROM (VALUES('warrior'),('champion_dodge'),('champion_glass')) b(build_key)
CROSS JOIN generate_series(15,19) s(slot_index);

INSERT INTO fixture_mount
SELECT b.build_key,20,16209,
  CASE b.build_key
    WHEN 'warrior' THEN '{307,317,327,387,397}'::smallint[]
    WHEN 'champion_dodge' THEN '{307,317,327,336,387}'::smallint[]
    ELSE '{347,367,407,427,447}'::smallint[] END,
  '{}'::smallint[],'{}'::smallint[]
FROM (VALUES('warrior'),('champion_dodge'),('champion_glass')) b(build_key);

CREATE TEMP TABLE fixture_equipment ON COMMIT DROP AS
SELECT f.character_id AS user_id,r.slot_index,r.prop_id,r.attrs,
  -- Only the nine canEnhance=true ItemBaseAttribute chains use Quartz levels.
  -- All single-ID and server-extension stones are add/delete-only at level 1.
  ARRAY(SELECT CASE
      WHEN a.attribute_id=ANY('{4,14,24,34,104,134,144,154,164}'::smallint[])
        THEN 5::smallint ELSE 1::smallint END
    FROM unnest(r.attrs) WITH ORDINALITY a(attribute_id,position)
    ORDER BY a.position) AS attribute_levels,
  r.class_attr,r.element1,r.element2,
  r.socket_effects,r.socket_values,710 AS holy_suit_code
FROM fixture_context f JOIN fixture_regular r USING(build_key)
UNION ALL
SELECT f.character_id,m.slot_index,m.prop_id,m.attrs,
  '{1,1,1,1,1}'::smallint[],
  NULL::smallint,NULL::smallint,NULL::smallint,
  m.socket_effects,m.socket_values,0
FROM fixture_context f JOIN fixture_mount m USING(build_key);

DO $equipment_content_guard$
BEGIN
  IF (SELECT count(*) FROM fixture_equipment)<>87
     OR EXISTS (SELECT 1 FROM fixture_equipment e
                LEFT JOIN item_templates i ON i.id=e.prop_id
                WHERE i.id IS NULL OR
                 (i.equipment_slot<>e.slot_index AND NOT
                  (i.equipment_slot=8 AND e.slot_index=9)))
     OR EXISTS (SELECT 1 FROM fixture_equipment e
                CROSS JOIN LATERAL unnest(e.attrs,e.attribute_levels)
                  a(attribute_id,attribute_level)
                LEFT JOIN item_attribute_templates t ON t.id=a.attribute_id
                WHERE t.id IS NULL OR t.max_level<a.attribute_level) THEN
    RAISE EXCEPTION 'max-combat equipment content is missing or incompatible';
  END IF;
END
$equipment_content_guard$;

DELETE FROM character_items i USING fixture_context f
WHERE i.user_id=f.character_id AND i.item_location=0
  AND NOT EXISTS (SELECT 1 FROM fixture_equipment e
                  WHERE e.user_id=i.user_id AND e.slot_index=i.slot_index);

INSERT INTO character_items (
 user_id,item_location,slot_index,prop_id,
 attribute1,attribute2,attribute3,attribute4,attribute5,
 attribute_level1,attribute_level2,attribute_level3,
 attribute_level4,attribute_level5,item_quality,item_grade,bound,stack,
 item_exp,holy_suit_code,holy_socket_count,
 holy_socket1_effect_id,holy_socket1_level,holy_socket1_value,
 holy_socket2_effect_id,holy_socket2_level,holy_socket2_value,
 holy_socket3_effect_id,holy_socket3_level,holy_socket3_value,
 holy_socket4_effect_id,holy_socket4_level,holy_socket4_value,
 holy_socket5_effect_id,holy_socket5_level,
 holy_socket6_effect_id,holy_socket6_level,
 class_attribute1,class_attribute2,elemental_attribute1,elemental_attribute2)
SELECT user_id,0,slot_index,prop_id,
 attrs[1],attrs[2],attrs[3],attrs[4],attrs[5],
 attribute_levels[1],attribute_levels[2],attribute_levels[3],
 attribute_levels[4],attribute_levels[5],
 20,25,1,1,0,holy_suit_code,cardinality(socket_effects),
 socket_effects[1],CASE WHEN socket_effects[1] IS NULL THEN NULL ELSE 10 END,socket_values[1],
 socket_effects[2],CASE WHEN socket_effects[2] IS NULL THEN NULL ELSE 10 END,socket_values[2],
 socket_effects[3],CASE WHEN socket_effects[3] IS NULL THEN NULL ELSE 10 END,socket_values[3],
 socket_effects[4],CASE WHEN socket_effects[4] IS NULL THEN NULL ELSE 10 END,socket_values[4],
 NULL,NULL,NULL,NULL,class_attr,NULL,element1,element2
FROM fixture_equipment
ON CONFLICT (user_id,item_location,slot_index) DO UPDATE SET
 prop_id=EXCLUDED.prop_id,attribute1=EXCLUDED.attribute1,
 attribute2=EXCLUDED.attribute2,attribute3=EXCLUDED.attribute3,
 attribute4=EXCLUDED.attribute4,attribute5=EXCLUDED.attribute5,
 attribute_level1=EXCLUDED.attribute_level1,
 attribute_level2=EXCLUDED.attribute_level2,
 attribute_level3=EXCLUDED.attribute_level3,
 attribute_level4=EXCLUDED.attribute_level4,
 attribute_level5=EXCLUDED.attribute_level5,
 item_quality=20,item_grade=25,bound=1,stack=1,item_exp=0,
 holy_suit_code=EXCLUDED.holy_suit_code,
 holy_socket_count=EXCLUDED.holy_socket_count,
 holy_socket1_effect_id=EXCLUDED.holy_socket1_effect_id,
 holy_socket1_level=EXCLUDED.holy_socket1_level,
 holy_socket1_value=EXCLUDED.holy_socket1_value,
 holy_socket2_effect_id=EXCLUDED.holy_socket2_effect_id,
 holy_socket2_level=EXCLUDED.holy_socket2_level,
 holy_socket2_value=EXCLUDED.holy_socket2_value,
 holy_socket3_effect_id=EXCLUDED.holy_socket3_effect_id,
 holy_socket3_level=EXCLUDED.holy_socket3_level,
 holy_socket3_value=EXCLUDED.holy_socket3_value,
 holy_socket4_effect_id=EXCLUDED.holy_socket4_effect_id,
 holy_socket4_level=EXCLUDED.holy_socket4_level,
 holy_socket4_value=EXCLUDED.holy_socket4_value,
 holy_socket5_effect_id=NULL,holy_socket5_level=NULL,
 holy_socket6_effect_id=NULL,holy_socket6_level=NULL,
 class_attribute1=EXCLUDED.class_attribute1,class_attribute2=NULL,
 elemental_attribute1=EXCLUDED.elemental_attribute1,
 elemental_attribute2=EXCLUDED.elemental_attribute2,updated_at=now();

SELECT public.recompute_character_holy_suit_points(character_id)
FROM fixture_context ORDER BY character_id;
