UPDATE character_items
SET item_quality = 20,
    item_grade = 25,
    bound = 1,
    stack = 1,
    updated_at = now()
WHERE user_id = 1
  AND item_location = 0
  AND slot_index = 4;

UPDATE character_kitbag ck
SET equip = loadout.equip,
    kitbag_1 = loadout.kitbag_1
FROM character_item_loadout loadout
WHERE ck.user_id = loadout.user_id
  AND ck.user_id = 1;
