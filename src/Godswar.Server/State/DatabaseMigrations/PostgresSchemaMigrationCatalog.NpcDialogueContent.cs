namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateNpcDialogueContentRelease() =>
        new(
            "20260729_024_npc_dialogue_content_release",
            "Create immutable NPC text, dialogue-route, and menu publications",
            """
            CREATE TABLE public.npc_dialogue_revisions (
                revision varchar(64) PRIMARY KEY,
                spawn_revision varchar(64) NOT NULL,
                text_count integer NOT NULL,
                profile_count integer NOT NULL,
                route_count integer NOT NULL,
                menu_entry_count integer NOT NULL,
                source varchar(64) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT fk_npc_dialogue_revisions_spawn
                    FOREIGN KEY (spawn_revision)
                    REFERENCES public.npc_content_revisions (revision)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_npc_dialogue_revisions_revision
                    CHECK (revision ~ '^[0-9A-F]{64}$'),
                CONSTRAINT ck_npc_dialogue_revisions_text_count
                    CHECK (text_count BETWEEN 1 AND 10000),
                CONSTRAINT ck_npc_dialogue_revisions_profile_count
                    CHECK (profile_count BETWEEN 1 AND 1024),
                CONSTRAINT ck_npc_dialogue_revisions_route_count
                    CHECK (route_count BETWEEN 1 AND 10000),
                CONSTRAINT ck_npc_dialogue_revisions_menu_count
                    CHECK (menu_entry_count BETWEEN 1 AND 65535),
                CONSTRAINT ck_npc_dialogue_revisions_source
                    CHECK (btrim(source) <> '')
            );

            CREATE TABLE public.npc_dialogue_texts (
                revision varchar(64) NOT NULL,
                npc_key varchar(96) NOT NULL,
                scene_key varchar(96) NOT NULL,
                display_name varchar(255) NOT NULL,
                description text NOT NULL,
                CONSTRAINT pk_npc_dialogue_texts
                    PRIMARY KEY (revision, npc_key),
                CONSTRAINT fk_npc_dialogue_texts_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.npc_dialogue_revisions (revision)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_npc_dialogue_texts_npc_key
                    CHECK (btrim(npc_key) <> ''),
                CONSTRAINT ck_npc_dialogue_texts_scene_key
                    CHECK (btrim(scene_key) <> ''),
                CONSTRAINT ck_npc_dialogue_texts_display_name
                    CHECK (
                        btrim(display_name) <> ''
                        AND octet_length(display_name) <= 1024
                    ),
                CONSTRAINT ck_npc_dialogue_texts_description
                    CHECK (
                        btrim(description) <> ''
                        AND octet_length(description) <= 16384
                    )
            );

            CREATE TABLE public.npc_dialogue_profiles (
                revision varchar(64) NOT NULL,
                profile_key varchar(64) NOT NULL,
                dialog_index integer NOT NULL,
                behavior smallint NOT NULL,
                initial_request_sub_id integer NOT NULL DEFAULT -1,
                CONSTRAINT pk_npc_dialogue_profiles
                    PRIMARY KEY (revision, profile_key),
                CONSTRAINT fk_npc_dialogue_profiles_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.npc_dialogue_revisions (revision)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_npc_dialogue_profiles_key
                    CHECK (profile_key ~ '^[a-z][a-z0-9_]{0,63}$'),
                CONSTRAINT ck_npc_dialogue_profiles_dialog_index
                    CHECK (dialog_index BETWEEN 0 AND 32767),
                CONSTRAINT ck_npc_dialogue_profiles_behavior
                    CHECK (behavior BETWEEN 1 AND 4),
                CONSTRAINT ck_npc_dialogue_profiles_initial_sub_id
                    CHECK (initial_request_sub_id BETWEEN -1 AND 1000000),
                CONSTRAINT uq_npc_dialogue_profiles_behavior
                    UNIQUE (revision, behavior)
            );

            CREATE TABLE public.npc_dialogue_profile_entries (
                revision varchar(64) NOT NULL,
                profile_key varchar(64) NOT NULL,
                menu_order smallint NOT NULL,
                sub_id integer NOT NULL,
                CONSTRAINT pk_npc_dialogue_profile_entries
                    PRIMARY KEY (revision, profile_key, menu_order),
                CONSTRAINT uq_npc_dialogue_profile_entries_sub_id
                    UNIQUE (revision, profile_key, sub_id),
                CONSTRAINT fk_npc_dialogue_profile_entries_profile
                    FOREIGN KEY (revision, profile_key)
                    REFERENCES public.npc_dialogue_profiles (
                        revision,
                        profile_key
                    )
                    ON DELETE RESTRICT,
                CONSTRAINT ck_npc_dialogue_profile_entries_order
                    CHECK (menu_order BETWEEN 0 AND 63),
                CONSTRAINT ck_npc_dialogue_profile_entries_sub_id
                    CHECK (sub_id BETWEEN 0 AND 1000000)
            );

            CREATE TABLE public.npc_dialogue_bindings (
                revision varchar(64) NOT NULL,
                npc_key varchar(96) NOT NULL,
                client_script_key varchar(32) NOT NULL,
                profile_key varchar(64) NOT NULL,
                CONSTRAINT pk_npc_dialogue_bindings
                    PRIMARY KEY (revision, npc_key),
                CONSTRAINT fk_npc_dialogue_bindings_text
                    FOREIGN KEY (revision, npc_key)
                    REFERENCES public.npc_dialogue_texts (
                        revision,
                        npc_key
                    )
                    ON DELETE RESTRICT,
                CONSTRAINT fk_npc_dialogue_bindings_profile
                    FOREIGN KEY (revision, profile_key)
                    REFERENCES public.npc_dialogue_profiles (
                        revision,
                        profile_key
                    )
                    ON DELETE RESTRICT,
                CONSTRAINT ck_npc_dialogue_bindings_script_key
                    CHECK (
                        client_script_key = npc_key
                        AND client_script_key ~ '^[A-Za-z0-9_]+$'
                    )
            );

            CREATE INDEX ix_npc_dialogue_bindings_profile
                ON public.npc_dialogue_bindings (
                    revision,
                    profile_key,
                    npc_key
                );

            CREATE TABLE public.npc_dialogue_publication (
                family varchar(24) PRIMARY KEY,
                revision varchar(64) NOT NULL,
                published_at timestamptz NOT NULL DEFAULT now(),
                publisher varchar(64) NOT NULL,
                CONSTRAINT ck_npc_dialogue_publication_family
                    CHECK (family = 'npc-dialogues'),
                CONSTRAINT ck_npc_dialogue_publication_publisher
                    CHECK (btrim(publisher) <> ''),
                CONSTRAINT fk_npc_dialogue_publication_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.npc_dialogue_revisions (revision)
                    ON DELETE RESTRICT
            );

            CREATE OR REPLACE FUNCTION
                public.reject_immutable_npc_dialogue_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $reject_immutable_npc_dialogue_mutation$
            BEGIN
                RAISE EXCEPTION
                    'NPC dialogue releases are immutable; publish a new revision instead.';
            END;
            $reject_immutable_npc_dialogue_mutation$;

            CREATE TRIGGER trg_npc_dialogue_revisions_immutable
            BEFORE UPDATE OR DELETE ON public.npc_dialogue_revisions
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_immutable_npc_dialogue_mutation();

            CREATE TRIGGER trg_npc_dialogue_texts_immutable
            BEFORE UPDATE OR DELETE ON public.npc_dialogue_texts
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_immutable_npc_dialogue_mutation();

            CREATE TRIGGER trg_npc_dialogue_profiles_immutable
            BEFORE UPDATE OR DELETE ON public.npc_dialogue_profiles
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_immutable_npc_dialogue_mutation();

            CREATE TRIGGER trg_npc_dialogue_profile_entries_immutable
            BEFORE UPDATE OR DELETE ON public.npc_dialogue_profile_entries
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_immutable_npc_dialogue_mutation();

            CREATE TRIGGER trg_npc_dialogue_bindings_immutable
            BEFORE UPDATE OR DELETE ON public.npc_dialogue_bindings
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_immutable_npc_dialogue_mutation();

            CREATE OR REPLACE FUNCTION
                public.guard_npc_dialogue_content_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_npc_dialogue_content_insert$
            DECLARE
                expected_count integer;
                stored_count integer;
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.npc_dialogue_publication
                    WHERE revision = NEW.revision
                ) THEN
                    RAISE EXCEPTION
                        'Published NPC dialogue revision % is immutable.',
                        NEW.revision;
                END IF;

                IF TG_TABLE_NAME = 'npc_dialogue_texts' THEN
                    SELECT text_count INTO STRICT expected_count
                    FROM public.npc_dialogue_revisions
                    WHERE revision = NEW.revision
                    FOR UPDATE;
                ELSIF TG_TABLE_NAME = 'npc_dialogue_profiles' THEN
                    SELECT profile_count INTO STRICT expected_count
                    FROM public.npc_dialogue_revisions
                    WHERE revision = NEW.revision
                    FOR UPDATE;
                ELSIF TG_TABLE_NAME = 'npc_dialogue_bindings' THEN
                    SELECT route_count INTO STRICT expected_count
                    FROM public.npc_dialogue_revisions
                    WHERE revision = NEW.revision
                    FOR UPDATE;
                ELSE
                    SELECT menu_entry_count INTO STRICT expected_count
                    FROM public.npc_dialogue_revisions
                    WHERE revision = NEW.revision
                    FOR UPDATE;
                END IF;

                EXECUTE format(
                    'SELECT COUNT(*)::integer FROM public.%I WHERE revision = $1',
                    TG_TABLE_NAME
                )
                INTO stored_count
                USING NEW.revision;

                IF stored_count >= expected_count THEN
                    RAISE EXCEPTION
                        'NPC dialogue revision % already contains its declared % rows in %.',
                        NEW.revision,
                        expected_count,
                        TG_TABLE_NAME;
                END IF;

                RETURN NEW;
            END;
            $guard_npc_dialogue_content_insert$;

            CREATE TRIGGER trg_npc_dialogue_texts_bounded_insert
            BEFORE INSERT ON public.npc_dialogue_texts
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_npc_dialogue_content_insert();

            CREATE TRIGGER trg_npc_dialogue_profiles_bounded_insert
            BEFORE INSERT ON public.npc_dialogue_profiles
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_npc_dialogue_content_insert();

            CREATE TRIGGER trg_npc_dialogue_entries_bounded_insert
            BEFORE INSERT ON public.npc_dialogue_profile_entries
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_npc_dialogue_content_insert();

            CREATE TRIGGER trg_npc_dialogue_bindings_bounded_insert
            BEFORE INSERT ON public.npc_dialogue_bindings
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_npc_dialogue_content_insert();

            CREATE OR REPLACE FUNCTION
                public.validate_npc_dialogue_publication()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $validate_npc_dialogue_publication$
            DECLARE
                release public.npc_dialogue_revisions%ROWTYPE;
                stored_texts integer;
                stored_profiles integer;
                stored_routes integer;
                stored_entries integer;
                incompatible_rows integer;
                spawn_entry_count integer;
            BEGIN
                SELECT * INTO STRICT release
                FROM public.npc_dialogue_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;

                SELECT entry_count INTO STRICT spawn_entry_count
                FROM public.npc_content_revisions
                WHERE revision = release.spawn_revision;

                SELECT COUNT(*)::integer INTO stored_texts
                FROM public.npc_dialogue_texts
                WHERE revision = NEW.revision;
                SELECT COUNT(*)::integer INTO stored_profiles
                FROM public.npc_dialogue_profiles
                WHERE revision = NEW.revision;
                SELECT COUNT(*)::integer INTO stored_routes
                FROM public.npc_dialogue_bindings
                WHERE revision = NEW.revision;
                SELECT COUNT(*)::integer INTO stored_entries
                FROM public.npc_dialogue_profile_entries
                WHERE revision = NEW.revision;

                IF (stored_texts, stored_profiles, stored_routes, stored_entries)
                   <> (
                       release.text_count,
                       release.profile_count,
                       release.route_count,
                       release.menu_entry_count
                   )
                   OR release.text_count <> spawn_entry_count THEN
                    RAISE EXCEPTION
                        'NPC dialogue revision % is incomplete.',
                        NEW.revision;
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM public.npc_content_publication
                    WHERE family = 'npcs'
                      AND revision = release.spawn_revision
                ) THEN
                    RAISE EXCEPTION
                        'NPC dialogue revision % targets an unpublished spawn revision.',
                        NEW.revision;
                END IF;

                SELECT COUNT(*)::integer INTO incompatible_rows
                FROM (
                    SELECT text_row.npc_key
                    FROM public.npc_dialogue_texts text_row
                    LEFT JOIN public.npc_spawn_definitions spawn
                      ON spawn.revision = release.spawn_revision
                     AND spawn.npc_key = text_row.npc_key
                    WHERE text_row.revision = NEW.revision
                    GROUP BY text_row.npc_key
                    HAVING COUNT(spawn.object_id) <> 1
                ) incompatible;

                IF incompatible_rows <> 0 THEN
                    RAISE EXCEPTION
                        'NPC dialogue revision % does not match its spawn revision.',
                        NEW.revision;
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.npc_dialogue_profile_entries entry
                    WHERE entry.revision = NEW.revision
                    GROUP BY entry.profile_key
                    HAVING MIN(entry.menu_order) <> 0
                       OR MAX(entry.menu_order) <> COUNT(*) - 1
                ) THEN
                    RAISE EXCEPTION
                        'NPC dialogue revision % has a non-contiguous menu.',
                        NEW.revision;
                END IF;

                RETURN NEW;
            END;
            $validate_npc_dialogue_publication$;

            CREATE TRIGGER trg_npc_dialogue_publication_complete
            BEFORE INSERT OR UPDATE ON public.npc_dialogue_publication
            FOR EACH ROW
            EXECUTE FUNCTION public.validate_npc_dialogue_publication();

            CREATE OR REPLACE FUNCTION
                public.reject_npc_dialogue_publication_delete()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $reject_npc_dialogue_publication_delete$
            BEGIN
                RAISE EXCEPTION
                    'The NPC dialogue publication pointer cannot be deleted.';
            END;
            $reject_npc_dialogue_publication_delete$;

            CREATE TRIGGER trg_npc_dialogue_publication_no_delete
            BEFORE DELETE ON public.npc_dialogue_publication
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_npc_dialogue_publication_delete();
            """);
}
