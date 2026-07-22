ALTER TABLE character_base
    ADD COLUMN IF NOT EXISTS zodiac_type smallint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS zodiac_lucky_status integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS zodiac_lucky_expires_at timestamptz,
    ADD COLUMN IF NOT EXISTS zodiac_level smallint NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS zodiac_energy integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS zodiac_energy_remainder_x100 integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS zodiac_online_day date,
    ADD COLUMN IF NOT EXISTS zodiac_online_duration_ticks bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS zodiac_last_online_at timestamptz,
    ADD COLUMN IF NOT EXISTS zodiac_last_compensation_day date,
    ADD COLUMN IF NOT EXISTS zodiac_accumulated_exp_x100 integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS zodiac_accumulated_talent_exp_x100 integer NOT NULL DEFAULT 0;
