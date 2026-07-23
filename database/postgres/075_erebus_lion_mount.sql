-- Locally authored Erebus Lion family. The matching client assets and
-- Ride/Status definitions are installed by tools/InstallErebusLionMount.py.
WITH tiers(id, required_level, speed, max_hp) AS (
    VALUES
        (16200,  40, '0.20', '2500'),
        (16201,  50, '0.21', '2800'),
        (16202,  60, '0.22', '3100'),
        (16203,  70, '0.23', '3400'),
        (16204,  80, '0.24', '3700'),
        (16205,  90, '0.25', '4000'),
        (16206, 100, '0.26', '4300'),
        (16207, 110, '0.27', '4650'),
        (16208, 120, '0.28', '5000'),
        (16209, 120, '0.50', '5000')
)
INSERT INTO item_templates (
    id, kind, name_key, display_name, equipment_slot, class_ids,
    min_level, max_level, hand, skill_flag, texture, icon, stats
)
SELECT
    id,
    'mount',
    'Ride' || id,
    'Erebus Lion',
    20,
    ARRAY[0, 1, 2, 3]::smallint[],
    required_level,
    200,
    NULL,
    20,
    './Localization/en_us/UI/Texture/Icon4.gwo',
    '396,0',
    jsonb_build_object(
        'ID', id::text,
        'Type', 'mount',
        'Texture', './Localization/en_us/UI/Texture/Icon4.gwo',
        'Icon', '396,0',
        'Random', '0',
        'Distribution', '0,0',
        'Speed', array_to_string(array_fill(speed, ARRAY[20]), ','),
        'MaxHP', array_to_string(array_fill(max_hp, ARRAY[20]), ','),
        'Money', '0',
        'Overlap', '1',
        'Equip', '1',
        'Use', '1',
        'SkillFlag', '20',
        'Class', '0,1,2,3',
        'PlayLv', required_level::text || ',200'
    )
FROM tiers
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
