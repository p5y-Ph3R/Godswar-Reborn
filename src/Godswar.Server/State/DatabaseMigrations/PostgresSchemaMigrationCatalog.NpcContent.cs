namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateNpcContentRelease() => new(
        "20260729_023_npc_content_release",
        "Create immutable, versioned NPC spawn content and one publication pointer",
        """
        CREATE TABLE public.npc_content_revisions (
            revision varchar(64) PRIMARY KEY,
            entry_count integer NOT NULL,
            source varchar(64) NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_npc_content_revisions_revision
                CHECK (revision ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_npc_content_revisions_entry_count
                CHECK (entry_count BETWEEN 0 AND 10000),
            CONSTRAINT ck_npc_content_revisions_source
                CHECK (btrim(source) <> '')
        );

        CREATE TABLE public.npc_spawn_definitions (
            revision varchar(64) NOT NULL,
            map_id smallint NOT NULL,
            scene_key varchar(96) NOT NULL,
            npc_key varchar(96) NOT NULL,
            template_key varchar(128) NOT NULL,
            object_id bigint NOT NULL,
            pos_x real NOT NULL,
            pos_z real NOT NULL,
            interaction_id bigint NOT NULL,
            appearance_type bigint NOT NULL,
            facing real NOT NULL,
            detail_10077 bytea NOT NULL DEFAULT '\x'::bytea,
            detail_10080 bytea NOT NULL DEFAULT '\x'::bytea,
            CONSTRAINT pk_npc_spawn_definitions
                PRIMARY KEY (revision, map_id, object_id),
            CONSTRAINT uq_npc_spawn_definitions_interaction
                UNIQUE (revision, map_id, interaction_id),
            CONSTRAINT fk_npc_spawn_definitions_revision
                FOREIGN KEY (revision)
                REFERENCES public.npc_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT fk_npc_spawn_definitions_map
                FOREIGN KEY (map_id)
                REFERENCES public.map_templates (map_id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_npc_spawn_definitions_scene_key
                CHECK (btrim(scene_key) <> ''),
            CONSTRAINT ck_npc_spawn_definitions_npc_key
                CHECK (btrim(npc_key) <> ''),
            CONSTRAINT ck_npc_spawn_definitions_template_key
                CHECK (btrim(template_key) <> ''),
            CONSTRAINT ck_npc_spawn_definitions_object_id
                CHECK (object_id BETWEEN 1 AND 4294967295),
            CONSTRAINT ck_npc_spawn_definitions_interaction_id
                CHECK (interaction_id BETWEEN 1 AND 4294967295),
            CONSTRAINT ck_npc_spawn_definitions_appearance_type
                CHECK (appearance_type BETWEEN 1 AND 4294967295),
            CONSTRAINT ck_npc_spawn_definitions_pos_x
                CHECK (
                    pos_x NOT IN (
                        'NaN'::real,
                        'Infinity'::real,
                        '-Infinity'::real
                    )
                ),
            CONSTRAINT ck_npc_spawn_definitions_pos_z
                CHECK (
                    pos_z NOT IN (
                        'NaN'::real,
                        'Infinity'::real,
                        '-Infinity'::real
                    )
                ),
            CONSTRAINT ck_npc_spawn_definitions_facing
                CHECK (
                    facing NOT IN (
                        'NaN'::real,
                        'Infinity'::real,
                        '-Infinity'::real
                    )
                ),
            CONSTRAINT ck_npc_spawn_definitions_detail_10077
                CHECK (octet_length(detail_10077) <= 65535),
            CONSTRAINT ck_npc_spawn_definitions_detail_10080
                CHECK (octet_length(detail_10080) <= 65535)
        );

        CREATE INDEX ix_npc_spawn_definitions_canonical
            ON public.npc_spawn_definitions (
                revision,
                map_id,
                npc_key,
                template_key,
                object_id
            );

        CREATE TABLE public.npc_content_publication (
            family varchar(16) PRIMARY KEY,
            revision varchar(64) NOT NULL,
            published_at timestamptz NOT NULL DEFAULT now(),
            publisher varchar(64) NOT NULL,
            CONSTRAINT ck_npc_content_publication_family
                CHECK (family = 'npcs'),
            CONSTRAINT ck_npc_content_publication_publisher
                CHECK (btrim(publisher) <> ''),
            CONSTRAINT fk_npc_content_publication_revision
                FOREIGN KEY (revision)
                REFERENCES public.npc_content_revisions (revision)
                ON DELETE RESTRICT
        );

        CREATE OR REPLACE FUNCTION public.reject_immutable_npc_content_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_immutable_npc_content_mutation$
        BEGIN
            RAISE EXCEPTION
                'NPC content revisions and definitions are immutable; publish a new revision instead.';
        END;
        $reject_immutable_npc_content_mutation$;

        CREATE TRIGGER trg_npc_content_revisions_immutable
        BEFORE UPDATE OR DELETE ON public.npc_content_revisions
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_immutable_npc_content_mutation();

        CREATE TRIGGER trg_npc_spawn_definitions_immutable
        BEFORE UPDATE OR DELETE ON public.npc_spawn_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_immutable_npc_content_mutation();

        CREATE OR REPLACE FUNCTION public.guard_npc_content_definition_insert()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_npc_content_definition_insert$
        DECLARE
            declared_entry_count integer;
            stored_entry_count integer;
        BEGIN
            SELECT entry_count
            INTO STRICT declared_entry_count
            FROM public.npc_content_revisions
            WHERE revision = NEW.revision
            FOR UPDATE;

            SELECT COUNT(*)::integer
            INTO stored_entry_count
            FROM public.npc_spawn_definitions
            WHERE revision = NEW.revision;

            IF stored_entry_count >= declared_entry_count THEN
                RAISE EXCEPTION
                    'NPC content revision % already contains its declared % definitions.',
                    NEW.revision,
                    declared_entry_count;
            END IF;

            RETURN NEW;
        END;
        $guard_npc_content_definition_insert$;

        CREATE TRIGGER trg_npc_spawn_definitions_bounded_insert
        BEFORE INSERT ON public.npc_spawn_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_npc_content_definition_insert();

        CREATE OR REPLACE FUNCTION public.validate_npc_content_publication()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $validate_npc_content_publication$
        DECLARE
            declared_entry_count integer;
            stored_entry_count integer;
        BEGIN
            SELECT entry_count
            INTO STRICT declared_entry_count
            FROM public.npc_content_revisions
            WHERE revision = NEW.revision
            FOR UPDATE;

            SELECT COUNT(*)::integer
            INTO stored_entry_count
            FROM public.npc_spawn_definitions
            WHERE revision = NEW.revision;

            IF stored_entry_count <> declared_entry_count THEN
                RAISE EXCEPTION
                    'NPC content revision % declares % definitions but contains %.',
                    NEW.revision,
                    declared_entry_count,
                    stored_entry_count;
            END IF;

            RETURN NEW;
        END;
        $validate_npc_content_publication$;

        CREATE TRIGGER trg_npc_content_publication_complete
        BEFORE INSERT OR UPDATE ON public.npc_content_publication
        FOR EACH ROW
        EXECUTE FUNCTION public.validate_npc_content_publication();

        CREATE OR REPLACE FUNCTION public.reject_npc_content_publication_delete()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_npc_content_publication_delete$
        BEGIN
            RAISE EXCEPTION
                'The NPC publication pointer cannot be deleted; publish or roll back to another revision.';
        END;
        $reject_npc_content_publication_delete$;

        CREATE TRIGGER trg_npc_content_publication_no_delete
        BEFORE DELETE ON public.npc_content_publication
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_npc_content_publication_delete();
        """);
}
