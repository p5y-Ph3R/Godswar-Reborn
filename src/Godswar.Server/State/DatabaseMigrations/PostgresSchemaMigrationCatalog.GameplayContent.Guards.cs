namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string GameplayContentGuardSql =
        """
        CREATE OR REPLACE FUNCTION public.reject_gameplay_content_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_gameplay_content_mutation$
        BEGIN
            RAISE EXCEPTION
                'Gameplay content revisions and definitions are immutable; publish a new revision instead.';
        END;
        $reject_gameplay_content_mutation$;

        CREATE TRIGGER trg_gameplay_revisions_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_content_revisions
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_maps_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_map_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_address_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_map_address_points
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_links_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_map_links
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_monsters_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_monster_templates
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_bosses_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_world_boss_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_pending_bosses_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_pending_world_boss_areas
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_skills_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_skill_combat_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_classes_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_class_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_talent_effects_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_talent_effect_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_talents_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_talent_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE TRIGGER trg_gameplay_skill_books_immutable
        BEFORE UPDATE OR DELETE ON public.gameplay_skill_book_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_content_mutation();

        CREATE OR REPLACE FUNCTION public.guard_gameplay_content_insert()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_gameplay_content_insert$
        DECLARE
            declared_count integer;
            stored_count integer;
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM public.gameplay_content_publication publication
                WHERE publication.revision = NEW.revision
            ) THEN
                RAISE EXCEPTION
                    'Published gameplay revision % cannot accept new rows.',
                    NEW.revision;
            END IF;

            EXECUTE format(
                'SELECT %I FROM public.gameplay_content_revisions WHERE revision = $1 FOR UPDATE',
                TG_ARGV[0]
            )
            INTO STRICT declared_count
            USING NEW.revision;

            EXECUTE format(
                'SELECT COUNT(*)::integer FROM public.%I WHERE revision = $1',
                TG_TABLE_NAME
            )
            INTO stored_count
            USING NEW.revision;

            IF stored_count >= declared_count THEN
                RAISE EXCEPTION
                    'Gameplay revision % table % already contains its declared % rows.',
                    NEW.revision,
                    TG_TABLE_NAME,
                    declared_count;
            END IF;

            RETURN NEW;
        END;
        $guard_gameplay_content_insert$;

        CREATE TRIGGER trg_gameplay_maps_bounded_insert
        BEFORE INSERT ON public.gameplay_map_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert('map_count');

        CREATE TRIGGER trg_gameplay_address_bounded_insert
        BEFORE INSERT ON public.gameplay_map_address_points
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert(
            'address_point_count'
        );

        CREATE TRIGGER trg_gameplay_links_bounded_insert
        BEFORE INSERT ON public.gameplay_map_links
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert('link_count');

        CREATE TRIGGER trg_gameplay_monsters_bounded_insert
        BEFORE INSERT ON public.gameplay_monster_templates
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert(
            'monster_template_count'
        );

        CREATE TRIGGER trg_gameplay_bosses_bounded_insert
        BEFORE INSERT ON public.gameplay_world_boss_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert(
            'world_boss_count'
        );

        CREATE TRIGGER trg_gameplay_pending_bosses_bounded_insert
        BEFORE INSERT ON public.gameplay_pending_world_boss_areas
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert(
            'pending_world_boss_count'
        );

        CREATE TRIGGER trg_gameplay_skills_bounded_insert
        BEFORE INSERT ON public.gameplay_skill_combat_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert('skill_count');

        CREATE TRIGGER trg_gameplay_classes_bounded_insert
        BEFORE INSERT ON public.gameplay_class_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert('class_count');

        CREATE TRIGGER trg_gameplay_talent_effects_bounded_insert
        BEFORE INSERT ON public.gameplay_talent_effect_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert(
            'talent_effect_count'
        );

        CREATE TRIGGER trg_gameplay_talents_bounded_insert
        BEFORE INSERT ON public.gameplay_talent_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert('talent_count');

        CREATE TRIGGER trg_gameplay_skill_books_bounded_insert
        BEFORE INSERT ON public.gameplay_skill_book_definitions
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_gameplay_content_insert(
            'skill_book_count'
        );

        CREATE OR REPLACE FUNCTION public.validate_gameplay_publication()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $validate_gameplay_publication$
        DECLARE
            declared public.gameplay_content_revisions%ROWTYPE;
            actual_maps integer;
            actual_address_points integer;
            actual_links integer;
            actual_monsters integer;
            actual_bosses integer;
            actual_pending_bosses integer;
            actual_classes integer;
            actual_talent_effects integer;
            actual_talents integer;
            actual_skills integer;
            actual_skill_books integer;
        BEGIN
            SELECT * INTO STRICT declared
            FROM public.gameplay_content_revisions
            WHERE revision = NEW.revision
            FOR UPDATE;

            SELECT COUNT(*)::integer INTO actual_maps
            FROM public.gameplay_map_definitions
            WHERE revision = NEW.revision;

            SELECT COUNT(*)::integer INTO actual_address_points
            FROM public.gameplay_map_address_points
            WHERE revision = NEW.revision;

            SELECT COUNT(*)::integer INTO actual_links
            FROM public.gameplay_map_links
            WHERE revision = NEW.revision;

            SELECT COUNT(*)::integer INTO actual_monsters
            FROM public.gameplay_monster_templates
            WHERE revision = NEW.revision;

            SELECT COUNT(*)::integer INTO actual_bosses
            FROM public.gameplay_world_boss_definitions
            WHERE revision = NEW.revision;

            SELECT COUNT(*)::integer INTO actual_pending_bosses
            FROM public.gameplay_pending_world_boss_areas
            WHERE revision = NEW.revision;

            SELECT COUNT(*)::integer INTO actual_skills
            FROM public.gameplay_skill_combat_definitions
            WHERE revision = NEW.revision;

            SELECT COUNT(*)::integer INTO actual_classes
            FROM public.gameplay_class_definitions
            WHERE revision = NEW.revision;

            SELECT COUNT(*)::integer INTO actual_talent_effects
            FROM public.gameplay_talent_effect_definitions
            WHERE revision = NEW.revision;

            SELECT COUNT(*)::integer INTO actual_talents
            FROM public.gameplay_talent_definitions
            WHERE revision = NEW.revision;

            SELECT COUNT(*)::integer INTO actual_skill_books
            FROM public.gameplay_skill_book_definitions
            WHERE revision = NEW.revision;

            IF actual_maps <> declared.map_count OR
               actual_address_points <> declared.address_point_count OR
               actual_links <> declared.link_count OR
               actual_monsters <> declared.monster_template_count OR
               actual_bosses <> declared.world_boss_count OR
               actual_pending_bosses <> declared.pending_world_boss_count OR
               actual_classes <> declared.class_count OR
               actual_talent_effects <> declared.talent_effect_count OR
               actual_talents <> declared.talent_count OR
               actual_skills <> declared.skill_count OR
               actual_skill_books <> declared.skill_book_count THEN
                RAISE EXCEPTION
                    'Gameplay revision % is incomplete or inconsistent.',
                    NEW.revision;
            END IF;

            RETURN NEW;
        END;
        $validate_gameplay_publication$;

        CREATE TRIGGER trg_gameplay_publication_complete
        BEFORE INSERT OR UPDATE ON public.gameplay_content_publication
        FOR EACH ROW
        EXECUTE FUNCTION public.validate_gameplay_publication();

        CREATE OR REPLACE FUNCTION public.reject_gameplay_publication_delete()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_gameplay_publication_delete$
        BEGIN
            RAISE EXCEPTION
                'The gameplay publication pointer cannot be deleted; publish or roll back to another revision.';
        END;
        $reject_gameplay_publication_delete$;

        CREATE TRIGGER trg_gameplay_publication_no_delete
        BEFORE DELETE ON public.gameplay_content_publication
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_gameplay_publication_delete();
        """;
}
