ALTER TABLE accounts
    ADD COLUMN IF NOT EXISTS vip_tier smallint NOT NULL DEFAULT 0;

ALTER TABLE accounts
    ADD COLUMN IF NOT EXISTS vip_expires_at timestamptz;

CREATE TABLE IF NOT EXISTS character_experience_modifiers (
    character_id integer NOT NULL REFERENCES character_base(id) ON DELETE CASCADE,
    status_id integer NOT NULL,
    kind integer NOT NULL,
    bonus_basis_points integer NOT NULL,
    priority integer NOT NULL DEFAULT 1,
    source varchar(64) NOT NULL DEFAULT '',
    activated_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz,
    PRIMARY KEY (character_id, kind)
);

CREATE INDEX IF NOT EXISTS ix_character_experience_modifiers_expiry
    ON character_experience_modifiers (character_id, expires_at);

CREATE TABLE IF NOT EXISTS world_boss_areas (
    map_id smallint PRIMARY KEY REFERENCES map_templates(map_id) ON DELETE CASCADE,
    boss_template_key varchar(128) NOT NULL,
    boss_display_name varchar(255) NOT NULL DEFAULT '',
    bonus_basis_points integer NOT NULL DEFAULT 2500,
    respawn_interval_seconds integer NOT NULL DEFAULT 43200,
    enabled boolean NOT NULL DEFAULT true
);

CREATE TABLE IF NOT EXISTS faction_area_experience_control (
    map_id smallint PRIMARY KEY REFERENCES world_boss_areas(map_id) ON DELETE CASCADE,
    controlling_camp smallint NOT NULL CHECK (controlling_camp IN (0, 1)),
    boss_template_key varchar(128) NOT NULL,
    bonus_basis_points integer NOT NULL DEFAULT 2500,
    activated_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    death_token varchar(64) NOT NULL UNIQUE
);

CREATE INDEX IF NOT EXISTS ix_faction_area_experience_control_active
    ON faction_area_experience_control (map_id, controlling_camp, expires_at);
