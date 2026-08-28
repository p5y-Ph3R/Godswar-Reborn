namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateRealmCalendarAuthority() => new(
            "20260821_103_realm_calendar_authority",
            "Persist audited IANA calendar settings for every logical realm",
            """
            DO $realm_calendar_preflight$
            BEGIN
                IF (
                    SELECT count(*)
                    FROM public.server realm
                    WHERE (
                        realm.id = 1
                      AND realm.name = 'Tempest'
                      AND realm.identifier =
                          'KAL3jcIzqGgKvOf1dbYZKC8cS'
                    ) OR (
                        realm.id = 2
                      AND realm.name = 'Dwargon'
                      AND realm.identifier =
                          'DWG3jcIzqGgKvOf1dbYZKC8cS'
                    )
                ) <> 2 THEN
                    RAISE EXCEPTION
                        'Tempest and Dwargon identities must exist exactly before realm calendar publication.'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $realm_calendar_preflight$;

            ALTER TABLE public.server
                ADD COLUMN time_zone_id varchar(64) NOT NULL
                    DEFAULT 'Etc/UTC',
                ADD COLUMN time_zone_revision bigint NOT NULL DEFAULT 1,
                ADD COLUMN time_zone_updated_at timestamptz NOT NULL
                    DEFAULT transaction_timestamp(),
                ADD COLUMN time_zone_updated_by varchar(128) NOT NULL
                    DEFAULT 'server-row-create',
                ADD CONSTRAINT ck_server_time_zone_id CHECK (
                    time_zone_id = btrim(time_zone_id)
                    AND octet_length(time_zone_id) BETWEEN 3 AND 64
                    AND time_zone_id ~
                        '^[A-Za-z0-9._+-]+(/[A-Za-z0-9._+-]+)+$'
                ),
                ADD CONSTRAINT ck_server_time_zone_revision CHECK (
                    time_zone_revision > 0
                ),
                ADD CONSTRAINT ck_server_time_zone_updated_by CHECK (
                    time_zone_updated_by = btrim(time_zone_updated_by)
                    AND octet_length(time_zone_updated_by) BETWEEN 1 AND 128
                    AND time_zone_updated_by ~ '^[ -~]+$'
                );

            DO $realm_calendar_seed$
            DECLARE
                seeded_rows integer;
            BEGIN
                UPDATE public.server
                SET time_zone_id = 'Asia/Manila',
                    time_zone_updated_by = 'migration-103'
                WHERE id IN (1, 2);
                GET DIAGNOSTICS seeded_rows = ROW_COUNT;
                IF seeded_rows <> 2 THEN
                    RAISE EXCEPTION
                        'Realm calendar seed did not update exactly Tempest and Dwargon.'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $realm_calendar_seed$;

            CREATE TABLE public.server_time_zone_audit (
                audit_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                realm_id integer NOT NULL,
                previous_revision bigint,
                revision bigint NOT NULL,
                previous_time_zone_id varchar(64),
                time_zone_id varchar(64) NOT NULL,
                changed_at timestamptz NOT NULL,
                changed_by varchar(128) NOT NULL,
                CONSTRAINT ux_server_time_zone_audit_revision
                    UNIQUE (realm_id, revision),
                CONSTRAINT ck_server_time_zone_audit_revision CHECK (
                    revision > 0
                    AND (
                        previous_revision IS NULL AND revision = 1
                        OR previous_revision IS NOT NULL
                           AND revision = previous_revision + 1
                    )
                ),
                CONSTRAINT ck_server_time_zone_audit_previous CHECK (
                    (previous_revision IS NULL) =
                    (previous_time_zone_id IS NULL)
                ),
                CONSTRAINT ck_server_time_zone_audit_zone CHECK (
                    time_zone_id = btrim(time_zone_id)
                    AND octet_length(time_zone_id) BETWEEN 3 AND 64
                ),
                CONSTRAINT ck_server_time_zone_audit_author CHECK (
                    changed_by = btrim(changed_by)
                    AND octet_length(changed_by) BETWEEN 1 AND 128
                )
            );

            INSERT INTO public.server_time_zone_audit (
                realm_id,
                revision,
                time_zone_id,
                changed_at,
                changed_by
            )
            SELECT id,
                   time_zone_revision,
                   time_zone_id,
                   time_zone_updated_at,
                   time_zone_updated_by
            FROM public.server
            ORDER BY id;

            CREATE FUNCTION public.guard_server_time_zone_change()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_server_time_zone_change$
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    IF NEW.time_zone_revision <> 1 THEN
                        RAISE EXCEPTION
                            'New realm time-zone revisions must start at one.'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END IF;

                IF (
                    NEW.time_zone_id,
                    NEW.time_zone_revision,
                    NEW.time_zone_updated_at,
                    NEW.time_zone_updated_by
                ) IS NOT DISTINCT FROM (
                    OLD.time_zone_id,
                    OLD.time_zone_revision,
                    OLD.time_zone_updated_at,
                    OLD.time_zone_updated_by
                ) THEN
                    RETURN NEW;
                END IF;

                IF NEW.time_zone_id IS NOT DISTINCT FROM OLD.time_zone_id
                   OR NEW.time_zone_revision <> OLD.time_zone_revision + 1
                   OR NEW.time_zone_updated_at < OLD.time_zone_updated_at THEN
                    RAISE EXCEPTION
                        'Realm time-zone changes require a new zone, the next revision, and monotonic audit metadata.'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $guard_server_time_zone_change$;

            CREATE FUNCTION public.audit_server_time_zone_change()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $audit_server_time_zone_change$
            BEGIN
                INSERT INTO public.server_time_zone_audit (
                    realm_id,
                    previous_revision,
                    revision,
                    previous_time_zone_id,
                    time_zone_id,
                    changed_at,
                    changed_by
                ) VALUES (
                    NEW.id,
                    CASE WHEN TG_OP = 'INSERT'
                        THEN NULL ELSE OLD.time_zone_revision END,
                    NEW.time_zone_revision,
                    CASE WHEN TG_OP = 'INSERT'
                        THEN NULL ELSE OLD.time_zone_id END,
                    NEW.time_zone_id,
                    NEW.time_zone_updated_at,
                    NEW.time_zone_updated_by
                );
                RETURN NEW;
            END;
            $audit_server_time_zone_change$;

            CREATE FUNCTION public.guard_server_time_zone_audit()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_server_time_zone_audit$
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM public.server realm
                        WHERE realm.id = NEW.realm_id
                          AND realm.time_zone_revision = NEW.revision
                          AND realm.time_zone_id = NEW.time_zone_id
                          AND realm.time_zone_updated_at = NEW.changed_at
                          AND realm.time_zone_updated_by = NEW.changed_by
                    ) OR (
                        NEW.previous_revision IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1
                            FROM public.server_time_zone_audit prior
                            WHERE prior.realm_id = NEW.realm_id
                              AND prior.revision = NEW.previous_revision
                              AND prior.time_zone_id =
                                  NEW.previous_time_zone_id
                        )
                    ) THEN
                        RAISE EXCEPTION
                            'Realm time-zone audit inserts must match the current realm row and its prior revision.'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END IF;

                RAISE EXCEPTION
                    'Realm time-zone audit evidence is append-only.'
                    USING ERRCODE = '23514';
            END;
            $guard_server_time_zone_audit$;

            CREATE TRIGGER trg_server_time_zone_guard
            BEFORE INSERT OR UPDATE ON public.server
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_server_time_zone_change();

            CREATE TRIGGER trg_server_time_zone_insert_audit
            AFTER INSERT ON public.server
            FOR EACH ROW
            EXECUTE FUNCTION public.audit_server_time_zone_change();

            CREATE TRIGGER trg_server_time_zone_update_audit
            AFTER UPDATE OF time_zone_id ON public.server
            FOR EACH ROW
            WHEN (
                NEW.time_zone_id IS DISTINCT FROM OLD.time_zone_id
            )
            EXECUTE FUNCTION public.audit_server_time_zone_change();

            CREATE TRIGGER trg_server_time_zone_audit_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON public.server_time_zone_audit
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_server_time_zone_audit();

            COMMENT ON COLUMN public.server.time_zone_id IS
                'Authoritative IANA civil time zone for all gameplay calendars in this realm.';
            COMMENT ON COLUMN public.server.time_zone_revision IS
                'CAS revision; workers pin it until a coordinated restart.';
            COMMENT ON TABLE public.server_time_zone_audit IS
                'Append-only evidence for per-realm calendar setting changes.';
            COMMENT ON COLUMN public.faction_crier_balance_revisions.server_utc_offset_minutes IS
                'Historical balance provenance only; public.server.time_zone_id is the runtime calendar authority.';
            COMMENT ON COLUMN public.holy_suit_operation_policy_content_definitions.realm_day_time_zone IS
                'Historical content provenance only; public.server.time_zone_id is the runtime calendar authority.';
            COMMENT ON COLUMN public.official_holy_suit_operation_policy_content.realm_day_time_zone IS
                'Historical content provenance only; public.server.time_zone_id is the runtime calendar authority.';
            """);
}
