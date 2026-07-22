-- Native Gear Mentor Attribute Dusts. Each stack of 99 converts into the
-- corresponding Attribute Stone; runtime recipe validation remains server-side.
INSERT INTO item_templates (
    id, kind, name_key, display_name, equipment_slot, class_ids,
    min_level, max_level, hand, skill_flag, texture, icon, stats
)
VALUES
    (9900, 'consume item', 'Rmaterial1', 'Strength Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '504,432', '{"ID":"9900","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"504,432","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9901, 'consume item', 'Rmaterial2', 'Shield Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '540,432', '{"ID":"9901","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"540,432","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9902, 'consume item', 'Rmaterial3', 'Magic Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '576,432', '{"ID":"9902","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"576,432","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9903, 'consume item', 'Rmaterial4', 'Spell Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '612,432', '{"ID":"9903","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"612,432","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9904, 'consume item', 'Rmaterial5', 'Absorption Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '648,432', '{"ID":"9904","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"648,432","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9905, 'consume item', 'Rmaterial6', 'Health Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '684,432', '{"ID":"9905","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"684,432","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9906, 'consume item', 'Rmaterial7', 'Mana Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '720,432', '{"ID":"9906","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"720,432","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9907, 'consume item', 'Rmaterial8', 'Blood Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '756,432', '{"ID":"9907","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"756,432","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9908, 'consume item', 'Rmaterial9', 'Vigor Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '792,432', '{"ID":"9908","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"792,432","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9910, 'consume item', 'Rmaterial10', 'Accuracy Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '504,504', '{"ID":"9910","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"504,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9911, 'consume item', 'Rmaterial11', 'Psychic Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '540,504', '{"ID":"9911","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"540,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9912, 'consume item', 'Rmaterial12', 'Fury Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '576,504', '{"ID":"9912","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"576,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9913, 'consume item', 'Rmaterial13', 'Tenacity Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '612,504', '{"ID":"9913","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"612,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9914, 'consume item', 'Rmaterial14', 'Impact Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '648,504', '{"ID":"9914","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"648,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9915, 'consume item', 'Rmaterial15', 'Fervor Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '684,504', '{"ID":"9915","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"684,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9916, 'consume item', 'Rmaterial16', 'Punishment Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '720,504', '{"ID":"9916","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"720,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9917, 'consume item', 'Rmaterial17', 'Purge Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '756,504', '{"ID":"9917","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"756,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9918, 'consume item', 'Rmaterial18', 'Guard Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '792,504', '{"ID":"9918","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"792,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9919, 'consume item', 'Rmaterial19', 'Restoration Dust', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '828,504', '{"ID":"9919","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"828,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9920, 'consume item', 'Rmaterial20', 'Dust of Destruction', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '864,504', '{"ID":"9920","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"864,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'),
    (9921, 'consume item', 'Rmaterial21', 'Dust of Penetration', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '900,504', '{"ID":"9921","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"900,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}')
ON CONFLICT (id) DO UPDATE
SET kind = EXCLUDED.kind,
    name_key = EXCLUDED.name_key,
    display_name = EXCLUDED.display_name,
    equipment_slot = EXCLUDED.equipment_slot,
    class_ids = EXCLUDED.class_ids,
    min_level = EXCLUDED.min_level,
    max_level = EXCLUDED.max_level,
    hand = EXCLUDED.hand,
    skill_flag = EXCLUDED.skill_flag,
    texture = EXCLUDED.texture,
    icon = EXCLUDED.icon,
    stats = EXCLUDED.stats;
