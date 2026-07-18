CREATE TABLE IF NOT EXISTS server_data_migrations (
    migration_key varchar(128) PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now(),
    affected_rows integer NOT NULL DEFAULT 0
);

DO $migration$
DECLARE
    repaired_count integer;
BEGIN
    IF EXISTS (
        SELECT 1
        FROM server_data_migrations
        WHERE migration_key = '20260718_repair_sparta_starting_map'
    ) THEN
        RETURN;
    END IF;

    -- Map travel is not implemented in this server baseline. Every existing
    -- camp-0/map-1 character was therefore created by the old camp mapping bug.
    -- Preserve the saved position so an active character resumes where it left off.
    UPDATE character_base
    SET "Map" = 0
    WHERE camp = 0
      AND "Map" = 1;

    GET DIAGNOSTICS repaired_count = ROW_COUNT;

    INSERT INTO server_data_migrations (migration_key, affected_rows)
    VALUES ('20260718_repair_sparta_starting_map', repaired_count);
END
$migration$;
