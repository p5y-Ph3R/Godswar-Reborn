ALTER TABLE character_experience_modifiers
    ADD COLUMN IF NOT EXISTS remaining_online_ticks bigint;

-- The old schema stored a wall-clock expiry. We cannot recover historical
-- online usage, so retain the complete originally granted duration. This also
-- restores grants that expired only because their owners were logged out.
UPDATE character_experience_modifiers
SET remaining_online_ticks = GREATEST(
    0,
    ROUND(EXTRACT(EPOCH FROM (expires_at - activated_at)) * 10000000)::bigint)
WHERE remaining_online_ticks IS NULL
  AND expires_at IS NOT NULL;

-- Preserve online-only semantics for grants inserted after this migration,
-- including legacy admin scripts that still provide only activated/expires.
CREATE OR REPLACE FUNCTION set_character_progression_boost_online_duration()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF TG_OP = 'UPDATE'
       AND (
           NEW.activated_at IS DISTINCT FROM OLD.activated_at
           OR NEW.expires_at IS DISTINCT FROM OLD.expires_at
       )
       AND NEW.remaining_online_ticks IS NOT DISTINCT FROM OLD.remaining_online_ticks THEN
        NEW.remaining_online_ticks := CASE
            WHEN NEW.expires_at IS NULL THEN NULL
            ELSE GREATEST(
                0,
                ROUND(EXTRACT(EPOCH FROM (
                    NEW.expires_at - NEW.activated_at
                )) * 10000000)::bigint)
        END;
    ELSIF NEW.remaining_online_ticks IS NULL AND NEW.expires_at IS NOT NULL THEN
        NEW.remaining_online_ticks := GREATEST(
            0,
            ROUND(EXTRACT(EPOCH FROM (
                NEW.expires_at - NEW.activated_at
            )) * 10000000)::bigint);
    END IF;
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS trg_character_progression_boost_online_duration
    ON character_experience_modifiers;
CREATE TRIGGER trg_character_progression_boost_online_duration
BEFORE INSERT OR UPDATE OF activated_at, expires_at, remaining_online_ticks
    ON character_experience_modifiers
FOR EACH ROW
EXECUTE FUNCTION set_character_progression_boost_online_duration();

CREATE INDEX IF NOT EXISTS ix_character_experience_modifiers_online_duration
    ON character_experience_modifiers (character_id, remaining_online_ticks);
