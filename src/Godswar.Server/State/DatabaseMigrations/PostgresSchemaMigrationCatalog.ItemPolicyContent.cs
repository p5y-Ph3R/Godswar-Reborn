namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateItemPolicyContentRelease() => new(
            "20260801_041_item_policy_content_release",
            "Extend item releases with immutable attribute, rank, and holy-suit policy",
            """
            ALTER TABLE public.item_template_content_revisions
                ADD COLUMN manifest_version smallint NOT NULL DEFAULT 1,
                ADD COLUMN attribute_count integer NOT NULL DEFAULT 0,
                ADD COLUMN equipment_rank_count integer NOT NULL DEFAULT 0,
                ADD COLUMN holy_suit_effect_count integer NOT NULL DEFAULT 0,
                ADD CONSTRAINT ck_item_content_manifest_version
                    CHECK (manifest_version IN (1, 2)),
                ADD CONSTRAINT ck_item_content_policy_counts
                    CHECK (
                        (manifest_version = 1
                         AND attribute_count = 0
                         AND equipment_rank_count = 0
                         AND holy_suit_effect_count = 0)
                        OR
                        (manifest_version = 2
                         AND attribute_count BETWEEN 1 AND 100000
                         AND equipment_rank_count BETWEEN 1 AND 10000
                         AND holy_suit_effect_count BETWEEN 1 AND 10000)
                    );

            CREATE TABLE public.item_attribute_content_definitions (
                revision varchar(64) NOT NULL,
                id integer NOT NULL,
                name_key varchar(64) NOT NULL,
                stat_type smallint NOT NULL,
                distribution smallint[] NOT NULL,
                percent boolean NOT NULL,
                max_level smallint NOT NULL,
                level_values numeric[] NOT NULL,
                stats jsonb NOT NULL,
                CONSTRAINT pk_item_attribute_content_definitions
                    PRIMARY KEY (revision, id),
                CONSTRAINT fk_item_attribute_content_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.item_template_content_revisions (revision)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_item_attribute_content_name
                    CHECK (btrim(name_key) <> ''),
                CONSTRAINT ck_item_attribute_content_level
                    CHECK (max_level > 0),
                CONSTRAINT ck_item_attribute_content_stats
                    CHECK (jsonb_typeof(stats) = 'object')
            );

            CREATE TABLE public.equipment_rank_content_definitions (
                revision varchar(64) NOT NULL,
                rank_kind varchar(16) NOT NULL,
                rank_level smallint NOT NULL,
                required_score integer NOT NULL,
                aura_effect integer NOT NULL,
                source varchar(64) NOT NULL,
                CONSTRAINT pk_equipment_rank_content_definitions
                    PRIMARY KEY (revision, rank_kind, rank_level),
                CONSTRAINT fk_equipment_rank_content_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.item_template_content_revisions (revision)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_equipment_rank_content_kind
                    CHECK (btrim(rank_kind) <> ''),
                CONSTRAINT ck_equipment_rank_content_values
                    CHECK (rank_level > 0 AND required_score >= 0),
                CONSTRAINT ck_equipment_rank_content_source
                    CHECK (btrim(source) <> '')
            );

            CREATE TABLE public.holy_suit_effect_content_definitions (
                revision varchar(64) NOT NULL,
                effect_key varchar(32) NOT NULL,
                stat_type smallint NOT NULL,
                unlock_points smallint NOT NULL,
                effect_value numeric NOT NULL,
                source varchar(128) NOT NULL,
                CONSTRAINT pk_holy_suit_effect_content_definitions
                    PRIMARY KEY (revision, effect_key),
                CONSTRAINT fk_holy_suit_effect_content_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.item_template_content_revisions (revision)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_holy_suit_effect_content_key
                    CHECK (btrim(effect_key) <> ''),
                CONSTRAINT ck_holy_suit_effect_content_unlock
                    CHECK (unlock_points >= 0),
                CONSTRAINT ck_holy_suit_effect_content_source
                    CHECK (btrim(source) <> '')
            );

            CREATE OR REPLACE FUNCTION public.guard_item_policy_content_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_item_policy_content_insert$
            DECLARE
                release public.item_template_content_revisions%ROWTYPE;
                current_count integer;
                expected_count integer;
            BEGIN
                SELECT * INTO release
                FROM public.item_template_content_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'unknown item-content revision %', NEW.revision;
                END IF;
                IF release.sealed_at IS NOT NULL THEN
                    RAISE EXCEPTION 'item-content revision % is already sealed', NEW.revision;
                END IF;
                IF release.manifest_version <> 2 THEN
                    RAISE EXCEPTION 'item policy requires manifest version 2';
                END IF;

                CASE TG_TABLE_NAME
                    WHEN 'item_attribute_content_definitions' THEN
                        expected_count := release.attribute_count;
                        SELECT count(*)::integer INTO current_count
                        FROM public.item_attribute_content_definitions
                        WHERE revision = NEW.revision;
                    WHEN 'equipment_rank_content_definitions' THEN
                        expected_count := release.equipment_rank_count;
                        SELECT count(*)::integer INTO current_count
                        FROM public.equipment_rank_content_definitions
                        WHERE revision = NEW.revision;
                    WHEN 'holy_suit_effect_content_definitions' THEN
                        expected_count := release.holy_suit_effect_count;
                        SELECT count(*)::integer INTO current_count
                        FROM public.holy_suit_effect_content_definitions
                        WHERE revision = NEW.revision;
                    ELSE
                        RAISE EXCEPTION
                            'unexpected item-policy table %', TG_TABLE_NAME;
                END CASE;
                IF current_count >= expected_count THEN
                    RAISE EXCEPTION
                        'item revision % already has its declared % rows for %',
                        NEW.revision, expected_count, TG_TABLE_NAME;
                END IF;
                RETURN NEW;
            END
            $guard_item_policy_content_insert$;

            CREATE TRIGGER trg_item_attribute_content_insert_guard
            BEFORE INSERT ON public.item_attribute_content_definitions
            FOR EACH ROW EXECUTE FUNCTION public.guard_item_policy_content_insert();
            CREATE TRIGGER trg_equipment_rank_content_insert_guard
            BEFORE INSERT ON public.equipment_rank_content_definitions
            FOR EACH ROW EXECUTE FUNCTION public.guard_item_policy_content_insert();
            CREATE TRIGGER trg_holy_suit_effect_content_insert_guard
            BEFORE INSERT ON public.holy_suit_effect_content_definitions
            FOR EACH ROW EXECUTE FUNCTION public.guard_item_policy_content_insert();

            CREATE TRIGGER trg_item_attribute_content_immutable
            BEFORE UPDATE OR DELETE ON public.item_attribute_content_definitions
            FOR EACH ROW EXECUTE FUNCTION public.reject_item_template_content_mutation();
            CREATE TRIGGER trg_equipment_rank_content_immutable
            BEFORE UPDATE OR DELETE ON public.equipment_rank_content_definitions
            FOR EACH ROW EXECUTE FUNCTION public.reject_item_template_content_mutation();
            CREATE TRIGGER trg_holy_suit_effect_content_immutable
            BEFORE UPDATE OR DELETE ON public.holy_suit_effect_content_definitions
            FOR EACH ROW EXECUTE FUNCTION public.reject_item_template_content_mutation();

            CREATE OR REPLACE FUNCTION public.validate_item_template_content_publication()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $validate_item_template_content_publication$
            DECLARE
                release public.item_template_content_revisions%ROWTYPE;
                template_count integer;
                attribute_count integer;
                rank_count integer;
                suit_count integer;
            BEGIN
                SELECT * INTO release
                FROM public.item_template_content_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'unknown item-content revision %', NEW.revision;
                END IF;

                SELECT count(*)::integer INTO template_count
                FROM public.item_template_content_definitions
                WHERE revision = NEW.revision;
                IF template_count <> release.entry_count THEN
                    RAISE EXCEPTION
                        'item revision % has % templates; expected %',
                        NEW.revision, template_count, release.entry_count;
                END IF;

                IF release.manifest_version = 2 THEN
                    SELECT count(*)::integer INTO attribute_count
                    FROM public.item_attribute_content_definitions
                    WHERE revision = NEW.revision;
                    SELECT count(*)::integer INTO rank_count
                    FROM public.equipment_rank_content_definitions
                    WHERE revision = NEW.revision;
                    SELECT count(*)::integer INTO suit_count
                    FROM public.holy_suit_effect_content_definitions
                    WHERE revision = NEW.revision;
                    IF attribute_count <> release.attribute_count
                       OR rank_count <> release.equipment_rank_count
                       OR suit_count <> release.holy_suit_effect_count THEN
                        RAISE EXCEPTION
                            'item revision % policy counts are incomplete',
                            NEW.revision;
                    END IF;
                END IF;

                UPDATE public.item_template_content_revisions
                SET sealed_at = now()
                WHERE revision = NEW.revision
                  AND sealed_at IS NULL;
                RETURN NEW;
            END
            $validate_item_template_content_publication$;

            CREATE OR REPLACE VIEW public.official_item_attribute_content
            WITH (security_barrier = true) AS
            SELECT definition.*
            FROM public.item_template_content_publication publication
            JOIN public.item_template_content_revisions release
              ON release.revision = publication.revision
             AND release.sealed_at IS NOT NULL
             AND release.manifest_version = 2
            JOIN public.item_attribute_content_definitions definition
              ON definition.revision = release.revision
            WHERE publication.family = 'items';

            CREATE OR REPLACE VIEW public.official_equipment_rank_content
            WITH (security_barrier = true) AS
            SELECT definition.*
            FROM public.item_template_content_publication publication
            JOIN public.item_template_content_revisions release
              ON release.revision = publication.revision
             AND release.sealed_at IS NOT NULL
             AND release.manifest_version = 2
            JOIN public.equipment_rank_content_definitions definition
              ON definition.revision = release.revision
            WHERE publication.family = 'items';

            CREATE OR REPLACE VIEW public.official_holy_suit_effect_content
            WITH (security_barrier = true) AS
            SELECT definition.*
            FROM public.item_template_content_publication publication
            JOIN public.item_template_content_revisions release
              ON release.revision = publication.revision
             AND release.sealed_at IS NOT NULL
             AND release.manifest_version = 2
            JOIN public.holy_suit_effect_content_definitions definition
              ON definition.revision = release.revision
            WHERE publication.family = 'items';

            DO $cut_item_policy_views_over$
            DECLARE
                source_table text;
                target_view text;
                dependent_view record;
                definition text;
            BEGIN
                FOR source_table, target_view IN
                    SELECT * FROM (VALUES
                        ('item_attribute_templates', 'official_item_attribute_content'),
                        ('equipment_rank_rules', 'official_equipment_rank_content'),
                        ('holy_suit_effect_templates', 'official_holy_suit_effect_content')
                    ) mappings(source_table, target_view)
                LOOP
                    FOR dependent_view IN
                        SELECT DISTINCT view_class.relname AS view_name
                        FROM pg_rewrite rewrite
                        JOIN pg_class view_class ON view_class.oid = rewrite.ev_class
                        JOIN pg_namespace view_namespace
                          ON view_namespace.oid = view_class.relnamespace
                        JOIN pg_depend dependency
                          ON dependency.classid = 'pg_rewrite'::regclass
                         AND dependency.objid = rewrite.oid
                        WHERE dependency.refobjid =
                                  format('public.%I', source_table)::regclass
                          AND view_class.relkind = 'v'
                          AND view_namespace.nspname = 'public'
                    LOOP
                        definition := pg_get_viewdef(
                            format('public.%I', dependent_view.view_name)::regclass,
                            true);
                        definition := replace(
                            definition,
                            'public.' || source_table,
                            'public.' || target_view);
                        definition := replace(
                            definition,
                            source_table,
                            'public.' || target_view);
                        EXECUTE format(
                            'CREATE OR REPLACE VIEW public.%I AS %s',
                            dependent_view.view_name,
                            definition);
                    END LOOP;
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
                    WHERE dependency.refobjid = ANY(ARRAY[
                              'public.item_attribute_templates'::regclass,
                              'public.equipment_rank_rules'::regclass,
                              'public.holy_suit_effect_templates'::regclass
                          ]::oid[])
                      AND view_class.relkind = 'v'
                      AND view_namespace.nspname = 'public'
                ) THEN
                    RAISE EXCEPTION
                        'A public runtime view still depends on mutable item policy';
                END IF;
            END
            $cut_item_policy_views_over$;
            """);
}
