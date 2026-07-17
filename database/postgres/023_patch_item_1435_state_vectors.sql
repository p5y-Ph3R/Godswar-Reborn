UPDATE item_templates
SET stats = stats || '{
  "State": "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
  "StateImmunity": "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0"
}'::jsonb
WHERE id = 1435;
