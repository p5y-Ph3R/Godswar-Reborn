-- Local test character defensive holy-stone setup.
-- Cooled stones are valid for armor, shield, sleeves/cuffs, leggings, shoes, girdles, and amulets.
-- Effects:
--   9  = Water Spirit of Darkness  = Reduce Physical Damage %
--   10 = Water Spirit of Mist      = Reduce Magical Damage %
--   13 = Water Spirit of Intent    = Reduce Critical Hitting Damage %
--   20 = Water Spirit of Frost     = Reflect damage amount

UPDATE item_templates
SET stats = jsonb_set(
    stats,
    '{AppFraction}',
    to_jsonb('15,19,24,30,36,42,48,60,75,90,120,150,174,199,227,255,285,317,350,384,420,458,498,548,600'::text))
WHERE id IN (2144, 2244);

UPDATE character_items
SET item_quality = 20,
    item_grade = 25,
    bound = 1,
    stack = 1,
    updated_at = now()
WHERE user_id = 1
  AND item_location = 0
  AND slot_index = 3;

WITH target_items AS (
    SELECT ci.id
    FROM character_items ci
    JOIN item_templates it ON it.id = ci.prop_id
    WHERE ci.user_id = 1
      AND ci.item_location = 0
      AND it.kind IN ('amulet', 'armor', 'shield', 'cuff', 'girdle', 'shoes', 'leggins')
      AND COALESCE(ci.holy_socket_count, 0) = 0
      AND ci.holy_socket1_effect_id IS NULL
      AND ci.holy_socket2_effect_id IS NULL
      AND ci.holy_socket3_effect_id IS NULL
      AND ci.holy_socket4_effect_id IS NULL
)
UPDATE character_items ci
SET holy_socket_count = 4,
    holy_socket1_effect_id = 9,
    holy_socket1_level = 10,
    holy_socket2_effect_id = 10,
    holy_socket2_level = 10,
    holy_socket3_effect_id = 13,
    holy_socket3_level = 10,
    holy_socket4_effect_id = 20,
    holy_socket4_level = 10,
    holy_socket5_effect_id = NULL,
    holy_socket5_level = NULL,
    holy_socket6_effect_id = NULL,
    holy_socket6_level = NULL,
    updated_at = now()
FROM target_items
WHERE ci.id = target_items.id;

UPDATE character_kitbag ck
SET equip = loadout.equip,
    kitbag_1 = loadout.kitbag_1
FROM character_item_loadout loadout
WHERE ck.user_id = loadout.user_id
  AND ck.user_id = 1;
