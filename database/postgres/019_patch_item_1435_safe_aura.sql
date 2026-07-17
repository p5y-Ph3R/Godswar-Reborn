UPDATE item_templates
SET stats = jsonb_set(
    stats,
    '{BaseFraction}',
    to_jsonb('0,8,18,28,40,54,74,100,140,200,260,340,440,560,700,860,1040,1240,1460,1700'::text)
)
WHERE id = 1435;

UPDATE character_equip ce
SET item_quality = ci.item_quality,
    item_grade = ci.item_grade,
    bound = ci.bound,
    stack = ci.stack,
    item_exp = ci.item_exp,
    holy_suit_code = ci.holy_suit_code,
    holy_suit_type = CASE WHEN ci.holy_suit_code > 0 THEN ci.holy_suit_code / 100 ELSE 0 END,
    holy_suit_level = CASE WHEN ci.holy_suit_code > 0 THEN ci.holy_suit_code % 100 ELSE 0 END
FROM character_items ci
WHERE ci.user_id = ce.user_id
  AND ci.item_location = 0
  AND ci.slot_index = ce.body_part_id
  AND ci.prop_id = 1435;
