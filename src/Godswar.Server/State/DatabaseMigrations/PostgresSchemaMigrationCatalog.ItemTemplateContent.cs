namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateItemTemplateContentRelease() => new(
            "20260801_038_item_template_content_release",
            "Create immutable, versioned item-template content and one publication pointer",
            """
            CREATE TABLE public.item_template_content_revisions (
                revision varchar(64) PRIMARY KEY,
                entry_count integer NOT NULL,
                source varchar(96) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                sealed_at timestamptz,
                CONSTRAINT ck_item_template_content_revisions_revision
                    CHECK (revision ~ '^[0-9A-F]{64}$'),
                CONSTRAINT ck_item_template_content_revisions_entry_count
                    CHECK (entry_count BETWEEN 1 AND 100000),
                CONSTRAINT ck_item_template_content_revisions_source
                    CHECK (btrim(source) <> '')
            );

            CREATE TABLE public.item_template_content_definitions (
                revision varchar(64) NOT NULL,
                id integer NOT NULL,
                kind varchar(64) NOT NULL,
                name_key varchar(128) NOT NULL,
                display_name varchar(255) NOT NULL,
                equipment_slot smallint NOT NULL,
                class_ids smallint[] NOT NULL,
                min_level integer,
                max_level integer,
                hand smallint,
                skill_flag integer,
                texture varchar(512) NOT NULL,
                icon varchar(64) NOT NULL,
                stats jsonb NOT NULL,
                CONSTRAINT pk_item_template_content_definitions
                    PRIMARY KEY (revision, id),
                CONSTRAINT fk_item_template_content_definitions_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.item_template_content_revisions (revision)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_item_template_content_definitions_id
                    CHECK (id > 0),
                CONSTRAINT ck_item_template_content_definitions_kind
                    CHECK (btrim(kind) <> ''),
                CONSTRAINT ck_item_template_content_definitions_name_key
                    CHECK (btrim(name_key) <> ''),
                CONSTRAINT ck_item_template_content_definitions_display_name
                    CHECK (btrim(display_name) <> ''),
                CONSTRAINT ck_item_template_content_definitions_equipment_slot
                    CHECK (equipment_slot BETWEEN -1 AND 127),
                CONSTRAINT ck_item_template_content_definitions_levels
                    CHECK (
                        (min_level IS NULL OR min_level >= 0) AND
                        (max_level IS NULL OR max_level >= 0) AND
                        (min_level IS NULL OR max_level IS NULL OR min_level <= max_level)
                    ),
                CONSTRAINT ck_item_template_content_definitions_stats
                    CHECK (jsonb_typeof(stats) = 'object')
            );

            CREATE INDEX ix_item_template_content_definitions_kind
                ON public.item_template_content_definitions
                    (revision, kind, id);

            CREATE TABLE public.item_template_content_publication (
                family varchar(32) PRIMARY KEY,
                revision varchar(64) NOT NULL,
                published_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT ck_item_template_content_publication_family
                    CHECK (family = 'items'),
                CONSTRAINT fk_item_template_content_publication_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.item_template_content_revisions (revision)
                    ON DELETE RESTRICT
            );

            CREATE OR REPLACE FUNCTION public.reject_item_template_content_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $immutable_item_template_content$
            BEGIN
                IF TG_TABLE_NAME = 'item_template_content_revisions'
                   AND TG_OP = 'UPDATE'
                   AND OLD.sealed_at IS NULL
                   AND NEW.sealed_at IS NOT NULL
                   AND NEW.revision = OLD.revision
                   AND NEW.entry_count = OLD.entry_count
                   AND NEW.source = OLD.source
                   AND NEW.created_at = OLD.created_at THEN
                    RETURN NEW;
                END IF;
                RAISE EXCEPTION 'published item-template revisions are immutable';
            END
            $immutable_item_template_content$;

            CREATE TRIGGER trg_item_template_content_revisions_immutable
            BEFORE UPDATE OR DELETE ON public.item_template_content_revisions
            FOR EACH ROW EXECUTE FUNCTION public.reject_item_template_content_mutation();

            CREATE TRIGGER trg_item_template_content_definitions_immutable
            BEFORE UPDATE OR DELETE ON public.item_template_content_definitions
            FOR EACH ROW EXECUTE FUNCTION public.reject_item_template_content_mutation();

            CREATE OR REPLACE FUNCTION public.guard_item_template_content_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_item_template_content_insert$
            DECLARE
                expected_count integer;
                is_sealed boolean;
                actual_count integer;
            BEGIN
                SELECT entry_count, sealed_at IS NOT NULL
                  INTO expected_count, is_sealed
                FROM public.item_template_content_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'unknown item-template revision %', NEW.revision;
                END IF;
                IF is_sealed THEN
                    RAISE EXCEPTION 'item-template revision % is already published', NEW.revision;
                END IF;
                SELECT count(*)::integer
                  INTO actual_count
                FROM public.item_template_content_definitions
                WHERE revision = NEW.revision;
                IF actual_count >= expected_count THEN
                    RAISE EXCEPTION 'item-template revision % exceeds declared count %',
                        NEW.revision, expected_count;
                END IF;
                RETURN NEW;
            END
            $guard_item_template_content_insert$;

            CREATE TRIGGER trg_item_template_content_definitions_insert_guard
            BEFORE INSERT ON public.item_template_content_definitions
            FOR EACH ROW EXECUTE FUNCTION public.guard_item_template_content_insert();

            CREATE OR REPLACE FUNCTION public.validate_item_template_content_publication()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $validate_item_template_content_publication$
            DECLARE
                expected_count integer;
                actual_count integer;
            BEGIN
                SELECT entry_count
                  INTO expected_count
                FROM public.item_template_content_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'unknown item-template revision %', NEW.revision;
                END IF;
                SELECT count(*)::integer
                  INTO actual_count
                FROM public.item_template_content_definitions
                WHERE revision = NEW.revision;
                IF actual_count <> expected_count THEN
                    RAISE EXCEPTION
                        'item-template revision % has % definitions; expected %',
                        NEW.revision, actual_count, expected_count;
                END IF;
                UPDATE public.item_template_content_revisions
                SET sealed_at = now()
                WHERE revision = NEW.revision
                  AND sealed_at IS NULL;
                RETURN NEW;
            END
            $validate_item_template_content_publication$;

            CREATE TRIGGER trg_item_template_content_publication_complete
            BEFORE INSERT OR UPDATE ON public.item_template_content_publication
            FOR EACH ROW EXECUTE FUNCTION public.validate_item_template_content_publication();

            CREATE OR REPLACE FUNCTION public.reject_item_template_publication_delete()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $reject_item_template_publication_delete$
            BEGIN
                RAISE EXCEPTION 'the official item-template publication cannot be deleted';
            END
            $reject_item_template_publication_delete$;

            CREATE TRIGGER trg_item_template_content_publication_no_delete
            BEFORE DELETE ON public.item_template_content_publication
            FOR EACH ROW EXECUTE FUNCTION public.reject_item_template_publication_delete();
            """);
}
