ALTER TABLE character_base
    ADD COLUMN IF NOT EXISTS holy_suit_points integer NOT NULL DEFAULT 0;
