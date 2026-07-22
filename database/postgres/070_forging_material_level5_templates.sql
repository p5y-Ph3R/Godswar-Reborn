-- Locally authored level-5 forging materials and Gear Mentor combination
-- pieces. The stock client stops at level 4, so the matching client
-- ItemBaseAttribute/BijouForge rows and Icon4 atlas must be installed before
-- these IDs are granted to a player.
INSERT INTO item_templates (
    id, kind, name_key, display_name, equipment_slot, class_ids,
    min_level, max_level, hand, skill_flag, texture, icon, stats
)
VALUES
    (4215, 'consume item', 'MaterialBase6', 'Level 5 Sapphire', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon4.gwo', '36,0', '{"ID":"4215","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon4.gwo","Icon":"36,0","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","BindType":"1"}'),
    (4216, 'consume item', 'MaterialBase7', 'Level 5 Sapphire Pieces', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon4.gwo', '144,0', '{"ID":"4216","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon4.gwo","Icon":"144,0","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","BindType":"1"}'),
    (4225, 'consume item', 'MaterialAppend6', 'Level 5 Emerald', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon4.gwo', '72,0', '{"ID":"4225","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon4.gwo","Icon":"72,0","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","BindType":"1"}'),
    (4226, 'consume item', 'MaterialAppend7', 'Level 5 Emerald Pieces', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon4.gwo', '180,0', '{"ID":"4226","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon4.gwo","Icon":"180,0","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","BindType":"1"}'),
    (4234, 'consume item', 'MaterialOdds5', 'Level 5 Crystal', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon4.gwo', '0,0', '{"ID":"4234","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon4.gwo","Icon":"0,0","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","BindType":"1"}'),
    (4235, 'consume item', 'MaterialOdds6', 'Level 5 Crystal Pieces', 0, '{}', NULL, NULL, NULL, NULL, './Localization/en_us/UI/Texture/Icon4.gwo', '108,0', '{"ID":"4235","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon4.gwo","Icon":"108,0","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","BindType":"1"}')
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
