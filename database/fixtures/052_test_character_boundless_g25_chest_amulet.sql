UPDATE character_items
SET attribute1 = 14,
    attribute_level1 = 5,
    attribute2 = 34,
    attribute_level2 = 5,
    attribute3 = 50,
    attribute_level3 = 5,
    attribute4 = 70,
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
  AND slot_index = 3;

UPDATE character_items
SET attribute1 = 50,
    attribute_level1 = 5,
    attribute2 = 70,
    attribute_level2 = 5,
    attribute3 = 104,
    attribute_level3 = 5,
    attribute4 = 120,
    attribute_level4 = 5,
    attribute5 = 164,
    attribute_level5 = 5,
    item_quality = 20,
    item_grade = 25,
    bound = 1,
    stack = 1,
    updated_at = now()
WHERE user_id = 1
  AND item_location = 0
  AND slot_index = 1;

UPDATE character_kitbag ck
SET equip = loadout.equip,
    kitbag_1 = loadout.kitbag_1
FROM character_item_loadout loadout
WHERE ck.user_id = loadout.user_id
  AND ck.user_id = 1;
