UPDATE character_items
SET attribute5 = 70,
    attribute_level5 = 5,
    updated_at = now()
WHERE user_id = 1
  AND item_location = 0
  AND slot_index = 5;

UPDATE character_kitbag ck
SET equip = loadout.equip,
    kitbag_1 = loadout.kitbag_1
FROM character_item_loadout loadout
WHERE ck.user_id = loadout.user_id
  AND ck.user_id = 1;
