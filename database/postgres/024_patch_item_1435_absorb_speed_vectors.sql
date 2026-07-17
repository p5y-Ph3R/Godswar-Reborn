UPDATE item_templates
SET stats = stats || '{
  "Speed": "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
  "PhysicalDamageAbsorb": "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
  "MagicDamageAbsorb": "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0"
}'::jsonb
WHERE id = 1435;
