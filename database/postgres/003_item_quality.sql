CREATE TABLE IF NOT EXISTS item_quality_levels (
    level smallint PRIMARY KEY,
    name varchar(32) NOT NULL,
    color varchar(64) NOT NULL
);

INSERT INTO item_quality_levels (level, name, color)
VALUES
    (1, 'Common', 'DEFAULT_TEXTCOLOR'),
    (2, 'Enhanced', 'TEAM_COLOR'),
    (3, 'Delicate', 'ItemCOLOR'),
    (4, 'Good', 'GREEN_TEXTCOLOR'),
    (5, 'Superior', 'GUILD_COLOR'),
    (6, 'Classic', 'Classical_color'),
    (7, 'Eternal', 'YELLOW_TEXTCOLOR'),
    (8, 'Epic', 'WHISPER_COLOR'),
    (9, 'Legendary', 'Legend_color'),
    (10, 'Mystic', 'Itemper_color'),
    (11, 'Divine', 'Divine_color'),
    (12, 'Celestial', 'Celestial_color'),
    (13, 'Mythical', 'Mythical_color'),
    (14, 'Astral', 'Astral_color'),
    (15, 'Arcane', 'Arcane_color'),
    (16, 'Ethereal', 'Ethereal_color'),
    (17, 'Transcendent', 'Transcendent_color'),
    (18, 'Ancient', 'Ancient_color'),
    (19, 'Primordial', 'Primordial_color'),
    (20, 'Boundless', 'Boundless_color')
ON CONFLICT (level) DO UPDATE
SET name = EXCLUDED.name,
    color = EXCLUDED.color;

CREATE TABLE IF NOT EXISTS item_grade_levels (
    level smallint PRIMARY KEY,
    stars smallint NOT NULL,
    color varchar(64) NOT NULL,
    attribute_color varchar(64) NOT NULL
);

INSERT INTO item_grade_levels (level, stars, color, attribute_color)
VALUES
    (1, 1, 'DEFAULT_TEXTCOLOR', 'GREEN_TEXTCOLOR'),
    (2, 2, 'DEFAULT_TEXTCOLOR', 'GREEN_TEXTCOLOR'),
    (3, 3, 'TEAM_COLOR', 'GREEN_TEXTCOLOR'),
    (4, 4, 'TEAM_COLOR', 'GREEN_TEXTCOLOR'),
    (5, 5, 'ItemCOLOR', 'GREEN_TEXTCOLOR'),
    (6, 6, 'GREEN_TEXTCOLOR', 'GREEN_TEXTCOLOR'),
    (7, 7, 'GUILD_COLOR', 'GREEN_TEXTCOLOR'),
    (8, 8, 'YELLOW_TEXTCOLOR', 'GREEN_TEXTCOLOR'),
    (9, 9, 'WHISPER_COLOR', 'GREEN_TEXTCOLOR'),
    (10, 10, 'WHISPER_COLOR', 'GREEN_TEXTCOLOR'),
    (11, 11, 'Legend_color', 'GREEN_TEXTCOLOR'),
    (12, 12, 'Itemper_color', 'GREEN_TEXTCOLOR'),
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

ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS item_quality smallint NOT NULL DEFAULT 1;
ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS item_grade smallint NOT NULL DEFAULT 1;
ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS bound smallint NOT NULL DEFAULT 1;
ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS stack smallint NOT NULL DEFAULT 1;
ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS item_exp integer NOT NULL DEFAULT 0;
ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS holy_suit_type smallint NOT NULL DEFAULT 0;
ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS holy_suit_level smallint NOT NULL DEFAULT 0;
ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS holy_suit_code integer NOT NULL DEFAULT 0;

WITH entries AS (
    SELECT
        cb.id AS user_id,
        item.ordinality - 1 AS body_part_id,
        trim(both '[]' FROM item.entry) AS clean_entry
    FROM character_base cb
    JOIN character_kitbag ck ON ck.user_id = cb.id
    CROSS JOIN LATERAL regexp_split_to_table(COALESCE(ck.equip, ''), '#') WITH ORDINALITY AS item(entry, ordinality)
    WHERE item.ordinality BETWEEN 1 AND 13
      AND item.entry <> ''
      AND item.entry <> '[]'
),
parts AS (
    SELECT
        user_id,
        body_part_id,
        string_to_array(clean_entry, ',') AS item_parts
    FROM entries
),
parsed AS (
    SELECT
        user_id,
        body_part_id,
        NULLIF(item_parts[1], '')::integer AS prop_id,
        COALESCE(NULLIF(item_parts[7], '')::smallint, 1) AS item_quality,
        COALESCE(NULLIF(item_parts[8], '')::smallint, 1) AS item_grade,
        COALESCE(NULLIF(item_parts[9], '')::smallint, 0) AS bound,
        COALESCE(NULLIF(item_parts[10], '')::smallint, 1) AS stack,
        COALESCE(NULLIF(item_parts[11], '')::integer, 0) AS item_exp,
        COALESCE(NULLIF(item_parts[12], '')::integer, 0) AS holy_suit_code
    FROM parts
    WHERE NULLIF(item_parts[1], '') IS NOT NULL
)
INSERT INTO character_equip (
    user_id, body_part_id, prop_id,
    item_quality, item_grade, bound, stack, item_exp,
    holy_suit_type, holy_suit_level, holy_suit_code,
    isbind
)
SELECT
    user_id,
    body_part_id,
    prop_id,
    item_quality,
    item_grade,
    bound,
    stack,
    item_exp,
    CASE WHEN holy_suit_code <= 0 THEN 0 ELSE LEAST(GREATEST(holy_suit_code / 100, 0), 7) END,
    CASE WHEN holy_suit_code <= 0 THEN 0 ELSE LEAST(GREATEST(holy_suit_code % 100, 0), 10) END,
    holy_suit_code,
    bound
FROM parsed
ON CONFLICT (user_id, body_part_id) DO UPDATE
SET prop_id = EXCLUDED.prop_id,
    item_quality = EXCLUDED.item_quality,
    item_grade = EXCLUDED.item_grade,
    bound = EXCLUDED.bound,
    stack = EXCLUDED.stack,
    item_exp = EXCLUDED.item_exp,
    holy_suit_type = EXCLUDED.holy_suit_type,
    holy_suit_level = EXCLUDED.holy_suit_level,
    holy_suit_code = EXCLUDED.holy_suit_code,
    isbind = EXCLUDED.isbind;
