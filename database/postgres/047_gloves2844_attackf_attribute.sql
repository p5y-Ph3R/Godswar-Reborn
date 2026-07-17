UPDATE item_templates
SET stats = jsonb_set(
    stats,
    '{MainAttribute}',
    to_jsonb('0,1,2,3,4,40,60,80,90,110,180,240,250,240,240,240,240,240,240,240,240,240,240,240,240'::text),
    true)
WHERE id = 2844;

UPDATE character_items
SET attribute1 = 4,
    attribute_level1 = 5,
    updated_at = now()
WHERE user_id = 1
  AND item_location = 0
  AND slot_index = 2
  AND prop_id = 2844;
