INSERT INTO item_grade_levels (level, stars, color, attribute_color)
VALUES
    (13, 13, 'Divine_color', 'GREEN_TEXTCOLOR'),
    (14, 14, 'Divine_color', 'GREEN_TEXTCOLOR'),
    (15, 15, 'Celestial_color', 'GREEN_TEXTCOLOR'),
    (16, 16, 'Celestial_color', 'GREEN_TEXTCOLOR'),
    (17, 17, 'Mythical_color', 'GREEN_TEXTCOLOR'),
    (18, 18, 'Mythical_color', 'GREEN_TEXTCOLOR'),
    (19, 19, 'Astral_color', 'GREEN_TEXTCOLOR'),
    (20, 20, 'Arcane_color', 'GREEN_TEXTCOLOR'),
    (21, 21, 'Transcendent_color', 'GREEN_TEXTCOLOR'),
    (22, 22, 'Ancient_color', 'GREEN_TEXTCOLOR'),
    (23, 23, 'Primordial_color', 'GREEN_TEXTCOLOR'),
    (24, 24, 'Primordial_color', 'GREEN_TEXTCOLOR'),
    (25, 25, 'Boundless_color', 'GREEN_TEXTCOLOR')
ON CONFLICT (level) DO UPDATE
SET stars = EXCLUDED.stars,
    color = EXCLUDED.color,
    attribute_color = EXCLUDED.attribute_color;
