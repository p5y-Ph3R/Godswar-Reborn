namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateNpcDialogueMultiRouteRelease() =>
        new(
            "20260802_051_npc_dialogue_multi_route",
            "Allow immutable ordered NPC functions and the Class Suit profile",
            """
            ALTER TABLE public.npc_dialogue_profiles
                DROP CONSTRAINT ck_npc_dialogue_profiles_behavior;

            ALTER TABLE public.npc_dialogue_profiles
                ADD CONSTRAINT ck_npc_dialogue_profiles_behavior
                CHECK (behavior BETWEEN 1 AND 5);

            ALTER TABLE public.npc_dialogue_bindings
                ADD COLUMN route_order smallint NOT NULL DEFAULT 0;

            ALTER TABLE public.npc_dialogue_bindings
                DROP CONSTRAINT pk_npc_dialogue_bindings;

            ALTER TABLE public.npc_dialogue_bindings
                ADD CONSTRAINT pk_npc_dialogue_bindings
                PRIMARY KEY (revision, npc_key, route_order);

            ALTER TABLE public.npc_dialogue_bindings
                ADD CONSTRAINT uq_npc_dialogue_bindings_profile
                UNIQUE (revision, npc_key, profile_key);

            ALTER TABLE public.npc_dialogue_bindings
                ADD CONSTRAINT ck_npc_dialogue_bindings_route_order
                CHECK (route_order BETWEEN 0 AND 63);

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

                IF EXISTS (
                    SELECT 1
                    FROM public.npc_dialogue_bindings binding
                    WHERE binding.revision = NEW.revision
                    GROUP BY binding.npc_key
                    HAVING MIN(binding.route_order) <> 0
                       OR MAX(binding.route_order) <> COUNT(*) - 1
                ) THEN
                    RAISE EXCEPTION
                        'NPC dialogue revision % has non-contiguous NPC routes.',
                        NEW.revision;
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.npc_dialogue_profiles profile
                    LEFT JOIN public.npc_dialogue_bindings binding
                      ON binding.revision = profile.revision
                     AND binding.profile_key = profile.profile_key
                    WHERE profile.revision = NEW.revision
                    GROUP BY profile.profile_key
                    HAVING COUNT(binding.npc_key) = 0
                ) THEN
                    RAISE EXCEPTION
                        'NPC dialogue revision % has an unbound profile.',
                        NEW.revision;
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.npc_dialogue_bindings binding
                    JOIN public.npc_dialogue_profiles profile
                      ON profile.revision = binding.revision
                     AND profile.profile_key = binding.profile_key
                    WHERE binding.revision = NEW.revision
                    GROUP BY binding.npc_key, profile.dialog_index
                    HAVING COUNT(*) > 1
                ) THEN
                    RAISE EXCEPTION
                        'NPC dialogue revision % duplicates a client dialog endpoint.',
                        NEW.revision;
                END IF;

                RETURN NEW;
            END;
            $validate_npc_dialogue_publication$;
            """);
}
