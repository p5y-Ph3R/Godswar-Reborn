UPDATE item_templates
SET stats = jsonb_set(
    stats,
    '{AppFraction}',
    to_jsonb('10,13,16,20,24,28,32,40,50,60,80,100,130,170,220,280,350,430,520,620,730,850,980,1120,1270'::text)
)
WHERE id = 1435;
