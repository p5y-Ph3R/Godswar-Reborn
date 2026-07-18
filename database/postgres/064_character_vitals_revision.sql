ALTER TABLE character_base
    ADD COLUMN IF NOT EXISTS vitals_revision bigint NOT NULL DEFAULT 0;
