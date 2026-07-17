WITH weapon_stones AS (
    SELECT
        user_id,
        holy_socket_count,
        holy_socket1_effect_id,
        holy_socket1_level,
        holy_socket2_effect_id,
        holy_socket2_level,
        holy_socket3_effect_id,
        holy_socket3_level,
        holy_socket4_effect_id,
        holy_socket4_level,
        holy_socket5_effect_id,
        holy_socket5_level,
        holy_socket6_effect_id,
        holy_socket6_level
    FROM character_items
    WHERE user_id = 1
      AND item_location = 0
      AND slot_index = 10
)
UPDATE character_items rings
SET holy_socket_count = weapon_stones.holy_socket_count,
    holy_socket1_effect_id = weapon_stones.holy_socket1_effect_id,
    holy_socket1_level = weapon_stones.holy_socket1_level,
    holy_socket2_effect_id = weapon_stones.holy_socket2_effect_id,
    holy_socket2_level = weapon_stones.holy_socket2_level,
    holy_socket3_effect_id = weapon_stones.holy_socket3_effect_id,
    holy_socket3_level = weapon_stones.holy_socket3_level,
    holy_socket4_effect_id = weapon_stones.holy_socket4_effect_id,
    holy_socket4_level = weapon_stones.holy_socket4_level,
    holy_socket5_effect_id = weapon_stones.holy_socket5_effect_id,
    holy_socket5_level = weapon_stones.holy_socket5_level,
    holy_socket6_effect_id = weapon_stones.holy_socket6_effect_id,
    holy_socket6_level = weapon_stones.holy_socket6_level,
    updated_at = now()
FROM weapon_stones
WHERE rings.user_id = weapon_stones.user_id
  AND rings.item_location = 0
  AND rings.slot_index IN (8, 9);

UPDATE character_kitbag ck
SET equip = loadout.equip,
    kitbag_1 = loadout.kitbag_1
FROM character_item_loadout loadout
WHERE ck.user_id = loadout.user_id
  AND ck.user_id = 1;
