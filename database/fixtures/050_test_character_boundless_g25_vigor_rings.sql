UPDATE character_items
SET prop_id = 3246,
    attribute1 = 4,
    attribute_level1 = 5,
    attribute2 = 80,
    attribute_level2 = 5,
    attribute3 = 240,
    attribute_level3 = 5,
    attribute4 = 60,
    attribute_level4 = 5,
    attribute5 = 134,
    attribute_level5 = 5,
    item_quality = 20,
    item_grade = 25,
    bound = 1,
    stack = 1,
    updated_at = now()
WHERE user_id = 1
  AND item_location = 0
  AND slot_index IN (8, 9);

UPDATE character_kitbag ck
SET equip = loadout.equip,
    kitbag_1 = loadout.kitbag_1
FROM character_item_loadout loadout
WHERE ck.user_id = loadout.user_id
  AND ck.user_id = 1;
