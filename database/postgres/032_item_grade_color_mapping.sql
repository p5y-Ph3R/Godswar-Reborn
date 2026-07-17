INSERT INTO item_grade_levels (level, stars, color, attribute_color)
VALUES
    (13, 13, 'Divine_color', 'GREEN_TEXTCOLOR'),
    (14, 14, 'Divine_color', 'GREEN_TEXTCOLOR'),
    (15, 15, 'Celestial_color', 'GREEN_TEXTCOLOR')
ON CONFLICT (level) DO UPDATE
SET stars = EXCLUDED.stars,
    color = EXCLUDED.color,
    attribute_color = EXCLUDED.attribute_color;
