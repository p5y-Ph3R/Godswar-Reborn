namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateMonsterContentRelease() =>
        new(
            "20260801_036_monster_content_release",
            "Create immutable, versioned monster spawn content and one publication pointer",
            """
            CREATE TABLE public.monster_content_revisions (
                revision varchar(64) PRIMARY KEY,
                entry_count integer NOT NULL,
                source varchar(96) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT ck_monster_content_revisions_revision
                    CHECK (revision ~ '^[0-9A-F]{64}$'),
                CONSTRAINT ck_monster_content_revisions_entry_count
                    CHECK (entry_count BETWEEN 0 AND 100000),
                CONSTRAINT ck_monster_content_revisions_source
                    CHECK (btrim(source) <> '')
            );

            CREATE TABLE public.monster_spawn_definitions (
                revision varchar(64) NOT NULL,
                map_id smallint NOT NULL,
                scene_key varchar(96) NOT NULL,
                template_key varchar(128) NOT NULL,
                display_name varchar(255) NOT NULL,
                object_id bigint NOT NULL,
                pos_x real NOT NULL,
                pos_z real NOT NULL,
                clear_bytes bytea NOT NULL,
                CONSTRAINT pk_monster_spawn_definitions
                    PRIMARY KEY (revision, map_id, object_id),
                CONSTRAINT fk_monster_spawn_definitions_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.monster_content_revisions (revision)
                    ON DELETE RESTRICT,
                CONSTRAINT fk_monster_spawn_definitions_map
                    FOREIGN KEY (map_id)
                    REFERENCES public.map_templates (map_id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_monster_spawn_definitions_scene_key
                    CHECK (btrim(scene_key) <> ''),
                CONSTRAINT ck_monster_spawn_definitions_template_key
                    CHECK (btrim(template_key) <> ''),
                CONSTRAINT ck_monster_spawn_definitions_display_name
                    CHECK (btrim(display_name) <> ''),
                CONSTRAINT ck_monster_spawn_definitions_object_id
                    CHECK (object_id BETWEEN 1 AND 4294967295),
                CONSTRAINT ck_monster_spawn_definitions_pos_x
                    CHECK (
                        pos_x NOT IN (
                            'NaN'::real,
                            'Infinity'::real,
                            '-Infinity'::real
                        )
                    ),
                CONSTRAINT ck_monster_spawn_definitions_pos_z
                    CHECK (
                        pos_z NOT IN (
                            'NaN'::real,
                            'Infinity'::real,
                            '-Infinity'::real
                        )
                    ),
                CONSTRAINT ck_monster_spawn_definitions_clear_bytes
                    CHECK (octet_length(clear_bytes) BETWEEN 108 AND 1200)
            );

            CREATE INDEX ix_monster_spawn_definitions_canonical
                ON public.monster_spawn_definitions (
                    revision,
                    map_id,
                    object_id,
                    template_key
                );

            CREATE TABLE public.monster_content_publication (
                family varchar(16) PRIMARY KEY,
                revision varchar(64) NOT NULL,
                published_at timestamptz NOT NULL DEFAULT now(),
                publisher varchar(64) NOT NULL,
                CONSTRAINT ck_monster_content_publication_family
                    CHECK (family = 'monsters'),
                CONSTRAINT ck_monster_content_publication_publisher
                    CHECK (btrim(publisher) <> ''),
                CONSTRAINT fk_monster_content_publication_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.monster_content_revisions (revision)
                    ON DELETE RESTRICT
            );

            CREATE OR REPLACE FUNCTION public.reject_immutable_monster_content_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $reject_immutable_monster_content_mutation$
            BEGIN
                RAISE EXCEPTION
                    'Monster content revisions and definitions are immutable; publish a new revision instead.';
            END;
            $reject_immutable_monster_content_mutation$;

            CREATE TRIGGER trg_monster_content_revisions_immutable
            BEFORE UPDATE OR DELETE ON public.monster_content_revisions
            FOR EACH ROW
            EXECUTE FUNCTION public.reject_immutable_monster_content_mutation();

            CREATE TRIGGER trg_monster_spawn_definitions_immutable
            BEFORE UPDATE OR DELETE ON public.monster_spawn_definitions
            FOR EACH ROW
            EXECUTE FUNCTION public.reject_immutable_monster_content_mutation();

            CREATE OR REPLACE FUNCTION public.guard_monster_content_definition_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_monster_content_definition_insert$
            DECLARE
                declared_entry_count integer;
                stored_entry_count integer;
            BEGIN
                SELECT entry_count
                INTO STRICT declared_entry_count
                FROM public.monster_content_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;

                SELECT COUNT(*)::integer
                INTO stored_entry_count
                FROM public.monster_spawn_definitions
                WHERE revision = NEW.revision;

                IF stored_entry_count >= declared_entry_count THEN
                    RAISE EXCEPTION
                        'Monster content revision % already contains its declared % definitions.',
                        NEW.revision,
                        declared_entry_count;
                END IF;

                RETURN NEW;
            END;
            $guard_monster_content_definition_insert$;

            CREATE TRIGGER trg_monster_spawn_definitions_bounded_insert
            BEFORE INSERT ON public.monster_spawn_definitions
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_monster_content_definition_insert();

            CREATE OR REPLACE FUNCTION public.validate_monster_content_publication()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $validate_monster_content_publication$
            DECLARE
                declared_entry_count integer;
                stored_entry_count integer;
            BEGIN
                SELECT entry_count
                INTO STRICT declared_entry_count
                FROM public.monster_content_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;

                SELECT COUNT(*)::integer
                INTO stored_entry_count
                FROM public.monster_spawn_definitions
                WHERE revision = NEW.revision;

                IF stored_entry_count <> declared_entry_count THEN
                    RAISE EXCEPTION
                        'Monster content revision % declares % definitions but contains %.',
                        NEW.revision,
                        declared_entry_count,
                        stored_entry_count;
                END IF;

                RETURN NEW;
            END;
            $validate_monster_content_publication$;

            CREATE TRIGGER trg_monster_content_publication_complete
            BEFORE INSERT OR UPDATE ON public.monster_content_publication
            FOR EACH ROW
            EXECUTE FUNCTION public.validate_monster_content_publication();

            CREATE OR REPLACE FUNCTION public.reject_monster_content_publication_delete()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $reject_monster_content_publication_delete$
            BEGIN
                RAISE EXCEPTION
                    'The monster publication pointer cannot be deleted; publish or roll back to another revision.';
            END;
            $reject_monster_content_publication_delete$;

            CREATE TRIGGER trg_monster_content_publication_no_delete
            BEFORE DELETE ON public.monster_content_publication
            FOR EACH ROW
            EXECUTE FUNCTION public.reject_monster_content_publication_delete();
            """);
}
