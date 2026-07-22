CREATE TABLE IF NOT EXISTS server_data_migrations (
    migration_key varchar(128) PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now(),
    affected_rows integer NOT NULL DEFAULT 0
);

-- Migration 016 already copied the legacy character_kitbag projection into
-- authoritative character_items on fresh database initialization. Recording
-- that completed import prevents runtime schema checks from replaying stale
-- compact rows after a player consumes, moves, or deletes an item.
INSERT INTO server_data_migrations (migration_key, affected_rows)
VALUES ('20260721_legacy_character_kitbag_import', 0)
ON CONFLICT (migration_key) DO NOTHING;
