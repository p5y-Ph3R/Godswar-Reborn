namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateItemMaterialContentRelease() => new(
            "20260801_044_item_material_content_release",
            "Add immutable item-material policy in manifest version 3",
            string.Concat(
            """
            ALTER TABLE public.item_template_content_revisions
                DROP CONSTRAINT ck_item_content_manifest_version,
                DROP CONSTRAINT ck_item_content_policy_counts,
                ADD COLUMN material_policy_count integer NOT NULL DEFAULT 0,
                ADD CONSTRAINT ck_item_content_manifest_version
                    CHECK (manifest_version IN (1, 2, 3)),
                ADD CONSTRAINT ck_item_content_policy_counts
                    CHECK (
                        (manifest_version = 1
                         AND attribute_count = 0
                         AND equipment_rank_count = 0
                         AND holy_suit_effect_count = 0)
                        OR
                        (manifest_version IN (2, 3)
                         AND attribute_count BETWEEN 1 AND 100000
                         AND equipment_rank_count BETWEEN 1 AND 10000
                         AND holy_suit_effect_count BETWEEN 1 AND 10000)
                    ),
                ADD CONSTRAINT ck_item_content_material_policy_count
                    CHECK (
                        (manifest_version IN (1, 2)
                         AND material_policy_count = 0)
                        OR
                        (manifest_version = 3
                         AND material_policy_count BETWEEN 1 AND 10000)
                    );
            """,
            ItemContentV3CorePolicyGuardSql,
            """

            CREATE TABLE public.item_material_content_definitions (
                revision varchar(64) NOT NULL,
                item_id integer NOT NULL,
                policy_kind varchar(32) NOT NULL,
                stack_cap smallint NOT NULL,
                random_value integer NOT NULL,
                distribution varchar(64) NOT NULL,
                granted_bound smallint NOT NULL,
                material varchar(32),
                material_level smallint,
                is_piece boolean NOT NULL DEFAULT false,
                attribute_name varchar(64),
                attribute_ids integer[] NOT NULL DEFAULT '{}',
                can_enhance boolean NOT NULL DEFAULT false,
                source_attribute_level smallint,
                target_attribute_level smallint,
                target_item_id integer,
                recipe_quantity integer,
                CONSTRAINT pk_item_material_content_definitions
                    PRIMARY KEY (revision, item_id),
                CONSTRAINT fk_item_material_content_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.item_template_content_revisions (revision)
                    ON DELETE RESTRICT,
                CONSTRAINT fk_item_material_content_template
                    FOREIGN KEY (revision, item_id)
                    REFERENCES public.item_template_content_definitions
                        (revision, id)
                    ON DELETE RESTRICT,
                CONSTRAINT fk_item_material_content_target_template
                    FOREIGN KEY (revision, target_item_id)
                    REFERENCES public.item_template_content_definitions
                        (revision, id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_item_material_content_item
                    CHECK (item_id > 0),
                CONSTRAINT ck_item_material_content_kind
                    CHECK (policy_kind IN (
                        'forging',
                        'attribute_stone',
                        'quartz_plate',
                        'flame_spark',
                        'water_grain',
                        'attribute_dust'
                    )),
                CONSTRAINT ck_item_material_content_common_values
                    CHECK (
                        stack_cap BETWEEN 1 AND 32767
                        AND random_value >= 0
                        AND btrim(distribution) <> ''
                        AND granted_bound IN (0, 1)
                        AND cardinality(attribute_ids) <= 100
                    ),
                CONSTRAINT ck_item_material_content_optional_values
                    CHECK (
                        (material_level IS NULL OR
                            material_level BETWEEN 1 AND 100)
                        AND (source_attribute_level IS NULL OR
                            source_attribute_level BETWEEN 1 AND 100)
                        AND (target_attribute_level IS NULL OR
                            target_attribute_level BETWEEN 1 AND 100)
                        AND (target_item_id IS NULL OR target_item_id > 0)
                        AND (recipe_quantity IS NULL OR
                            recipe_quantity BETWEEN 1 AND 32767)
                    ),
                CONSTRAINT ck_item_material_content_shape
                    CHECK (
                        (policy_kind = 'forging'
                         AND material IS NOT NULL
                         AND btrim(material) <> ''
                         AND material_level IS NOT NULL
                         AND attribute_name IS NULL
                         AND cardinality(attribute_ids) = 0
                         AND NOT can_enhance
                         AND source_attribute_level IS NULL
                         AND target_attribute_level IS NULL
                         AND target_item_id IS NULL
                         AND recipe_quantity IS NULL)
                        OR
                        (policy_kind = 'attribute_stone'
                         AND material IS NULL
                         AND material_level IS NULL
                         AND NOT is_piece
                         AND attribute_name IS NOT NULL
                         AND btrim(attribute_name) <> ''
                         AND cardinality(attribute_ids) > 0
                         AND source_attribute_level IS NULL
                         AND target_attribute_level IS NULL
                         AND target_item_id IS NULL
                         AND recipe_quantity IS NULL)
                        OR
                        (policy_kind = 'quartz_plate'
                         AND material IS NULL
                         AND material_level IS NULL
                         AND NOT is_piece
                         AND attribute_name IS NULL
                         AND cardinality(attribute_ids) = 0
                         AND NOT can_enhance
                         AND source_attribute_level IS NOT NULL
                         AND target_attribute_level IS NOT NULL
                         AND target_attribute_level > source_attribute_level
                         AND target_item_id IS NULL
                         AND recipe_quantity IS NULL)
                        OR
                        (policy_kind IN ('flame_spark', 'water_grain')
                         AND material IS NULL
                         AND material_level IS NULL
                         AND NOT is_piece
                         AND attribute_name IS NULL
                         AND cardinality(attribute_ids) = 0
                         AND NOT can_enhance
                         AND source_attribute_level IS NULL
                         AND target_attribute_level IS NULL
                         AND target_item_id IS NULL
                         AND recipe_quantity IS NULL)
                        OR
                        (policy_kind = 'attribute_dust'
                         AND material IS NULL
                         AND material_level IS NULL
                         AND NOT is_piece
                         AND attribute_name IS NULL
                         AND cardinality(attribute_ids) = 0
                         AND NOT can_enhance
                         AND source_attribute_level IS NULL
                         AND target_attribute_level IS NULL
                         AND target_item_id IS NOT NULL
                         AND recipe_quantity IS NOT NULL)
                    )
            );

            CREATE INDEX ix_item_material_content_kind
                ON public.item_material_content_definitions
                    (revision, policy_kind, item_id);

            CREATE OR REPLACE FUNCTION public.guard_item_material_content_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_item_material_content_insert$
            DECLARE
                release public.item_template_content_revisions%ROWTYPE;
                current_count integer;
            BEGIN
                SELECT * INTO release
                FROM public.item_template_content_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION
                        'unknown item-content revision %', NEW.revision;
                END IF;
                IF release.sealed_at IS NOT NULL THEN
                    RAISE EXCEPTION
                        'item-content revision % is already sealed',
                        NEW.revision;
                END IF;
                IF release.manifest_version <> 3
                   OR release.material_policy_count <= 0 THEN
                    RAISE EXCEPTION
                        'item material policy requires manifest version 3';
                END IF;

                SELECT count(*)::integer INTO current_count
                FROM public.item_material_content_definitions
                WHERE revision = NEW.revision;
                IF current_count >= release.material_policy_count THEN
                    RAISE EXCEPTION
                        'item revision % already has its declared % material rows',
                        NEW.revision, release.material_policy_count;
                END IF;
                RETURN NEW;
            END
            $guard_item_material_content_insert$;

            CREATE TRIGGER trg_item_material_content_insert_guard
            BEFORE INSERT ON public.item_material_content_definitions
            FOR EACH ROW EXECUTE FUNCTION public.guard_item_material_content_insert();

            CREATE TRIGGER trg_item_material_content_immutable
            BEFORE UPDATE OR DELETE ON public.item_material_content_definitions
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
                material_count integer;
            BEGIN
                SELECT * INTO release
                FROM public.item_template_content_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION
                        'unknown item-content revision %', NEW.revision;
                END IF;

                SELECT count(*)::integer INTO template_count
                FROM public.item_template_content_definitions
                WHERE revision = NEW.revision;
                IF template_count <> release.entry_count THEN
                    RAISE EXCEPTION
                        'item revision % has % templates; expected %',
                        NEW.revision, template_count, release.entry_count;
                END IF;

                IF release.manifest_version IN (2, 3) THEN
                    SELECT count(*)::integer INTO attribute_count
                    FROM public.item_attribute_content_definitions
                    WHERE revision = NEW.revision;
                    SELECT count(*)::integer INTO rank_count
                    FROM public.equipment_rank_content_definitions
                    WHERE revision = NEW.revision;
                    SELECT count(*)::integer INTO suit_count
                    FROM public.holy_suit_effect_content_definitions
                    WHERE revision = NEW.revision;
                    SELECT count(*)::integer INTO material_count
                    FROM public.item_material_content_definitions
                    WHERE revision = NEW.revision;
                    IF attribute_count <> release.attribute_count
                       OR rank_count <> release.equipment_rank_count
                       OR suit_count <> release.holy_suit_effect_count THEN
                        RAISE EXCEPTION
                            'item revision % policy counts are incomplete',
                            NEW.revision;
                    END IF;
                    IF release.manifest_version = 2
                       AND (release.material_policy_count <> 0
                            OR material_count <> 0) THEN
                        RAISE EXCEPTION
                            'item manifest version 2 cannot contain material policy';
                    END IF;
                    IF release.manifest_version = 3
                       AND (release.material_policy_count <= 0
                            OR material_count <>
                               release.material_policy_count) THEN
                        RAISE EXCEPTION
                            'item revision % material policy is incomplete',
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
                   AND NEW.created_at = OLD.created_at
                   AND NEW.manifest_version = OLD.manifest_version
                   AND NEW.attribute_count = OLD.attribute_count
                   AND NEW.equipment_rank_count = OLD.equipment_rank_count
                   AND NEW.holy_suit_effect_count =
                       OLD.holy_suit_effect_count
                   AND NEW.material_policy_count =
                       OLD.material_policy_count THEN
                    RETURN NEW;
                END IF;
                RAISE EXCEPTION
                    'published item-template revisions are immutable';
            END
            $immutable_item_template_content$;

            CREATE OR REPLACE VIEW public.official_item_attribute_content
            WITH (security_barrier = true) AS
            SELECT definition.*
            FROM public.item_template_content_publication publication
            JOIN public.item_template_content_revisions release
              ON release.revision = publication.revision
             AND release.sealed_at IS NOT NULL
             AND release.manifest_version IN (2, 3)
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
             AND release.manifest_version IN (2, 3)
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
             AND release.manifest_version IN (2, 3)
            JOIN public.holy_suit_effect_content_definitions definition
              ON definition.revision = release.revision
            WHERE publication.family = 'items';

            CREATE OR REPLACE VIEW public.official_item_material_content
            WITH (security_barrier = true) AS
            SELECT definition.*
            FROM public.item_template_content_publication publication
            JOIN public.item_template_content_revisions release
              ON release.revision = publication.revision
             AND release.sealed_at IS NOT NULL
             AND release.manifest_version = 3
             AND release.material_policy_count > 0
            JOIN public.item_material_content_definitions definition
              ON definition.revision = release.revision
            WHERE publication.family = 'items';
            """));
}
