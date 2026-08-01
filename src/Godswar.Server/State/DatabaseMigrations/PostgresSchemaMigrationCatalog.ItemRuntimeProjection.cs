namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateItemRuntimeProjectionCutover() => new(
            "20260801_040_item_runtime_projection_cutover",
            "Route all item-derived database views through the official immutable publication",
            """
            CREATE OR REPLACE VIEW public.official_item_template_content
            WITH (security_barrier = true)
            AS
            SELECT
                definition.revision,
                definition.id,
                definition.kind::varchar(32) AS kind,
                definition.name_key,
                definition.display_name,
                definition.equipment_slot,
                definition.class_ids,
                definition.min_level,
                definition.max_level,
                definition.hand,
                definition.skill_flag,
                definition.texture::varchar(255) AS texture,
                definition.icon::varchar(32) AS icon,
                definition.stats
            FROM public.item_template_content_publication publication
            JOIN public.item_template_content_revisions revision
              ON revision.revision = publication.revision
             AND revision.sealed_at IS NOT NULL
            JOIN public.item_template_content_definitions definition
              ON definition.revision = revision.revision
            WHERE publication.family = 'items';

            DO $cut_item_runtime_views_over$
            DECLARE
                dependent_view record;
                prior_definition text;
                next_definition text;
            BEGIN
                FOR dependent_view IN
                    SELECT DISTINCT view_class.relname AS view_name
                    FROM pg_rewrite rewrite
                    JOIN pg_class view_class
                      ON view_class.oid = rewrite.ev_class
                    JOIN pg_namespace view_namespace
                      ON view_namespace.oid = view_class.relnamespace
                    JOIN pg_depend dependency
                      ON dependency.classid = 'pg_rewrite'::regclass
                     AND dependency.objid = rewrite.oid
                    WHERE dependency.refobjid =
                              'public.item_templates'::regclass
                      AND view_class.relkind = 'v'
                      AND view_namespace.nspname = 'public'
                    ORDER BY view_class.relname
                LOOP
                    prior_definition := pg_get_viewdef(
                        format(
                            'public.%I',
                            dependent_view.view_name)::regclass,
                        true);
                    next_definition := replace(
                        replace(
                            prior_definition,
                            'public.item_templates it',
                            'public.official_item_template_content it'),
                        'item_templates it',
                        'public.official_item_template_content it');
                    IF next_definition = prior_definition THEN
                        RAISE EXCEPTION
                            'Item-derived view % uses an unreviewed item_templates alias',
                            dependent_view.view_name;
                    END IF;

                    EXECUTE format(
                        'CREATE OR REPLACE VIEW public.%I AS %s',
                        dependent_view.view_name,
                        next_definition);
                END LOOP;

                IF EXISTS (
                    SELECT 1
                    FROM pg_rewrite rewrite
                    JOIN pg_class view_class
                      ON view_class.oid = rewrite.ev_class
                    JOIN pg_namespace view_namespace
                      ON view_namespace.oid = view_class.relnamespace
                    JOIN pg_depend dependency
                      ON dependency.classid = 'pg_rewrite'::regclass
                     AND dependency.objid = rewrite.oid
                    WHERE dependency.refobjid =
                              'public.item_templates'::regclass
                      AND view_class.relkind = 'v'
                      AND view_namespace.nspname = 'public'
                ) THEN
                    RAISE EXCEPTION
                        'A public runtime view still depends on mutable item_templates';
                END IF;
            END
            $cut_item_runtime_views_over$;
            """);
}
