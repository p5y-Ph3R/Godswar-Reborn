namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string ItemMaterialRecipeGuardSql = """

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
                    RAISE EXCEPTION
                        'unknown item-content revision %', NEW.revision;
                END IF;
                IF release.sealed_at IS NOT NULL THEN
                    RAISE EXCEPTION
                        'item-content revision % is already sealed',
                        NEW.revision;
                END IF;
                IF release.manifest_version NOT IN (2, 3, 4) THEN
                    RAISE EXCEPTION
                        'item policy requires manifest version 2, 3, or 4';
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

            CREATE OR REPLACE FUNCTION public.guard_item_material_content_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_item_material_content_insert$
            DECLARE
                release public.item_template_content_revisions%ROWTYPE;
                current_count integer;
                current_recipe_count integer;
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
                IF release.manifest_version NOT IN (3, 4)
                   OR release.material_policy_count <= 0 THEN
                    RAISE EXCEPTION
                        'item material policy requires manifest version 3 or 4';
                END IF;
                IF release.manifest_version = 3
                   AND NEW.recipe_kind IS NOT NULL THEN
                    RAISE EXCEPTION
                        'item material recipes require manifest version 4';
                END IF;

                SELECT count(*)::integer INTO current_count
                FROM public.item_material_content_definitions
                WHERE revision = NEW.revision;
                IF current_count >= release.material_policy_count THEN
                    RAISE EXCEPTION
                        'item revision % already has its declared % material rows',
                        NEW.revision, release.material_policy_count;
                END IF;

                IF NEW.recipe_kind IS NOT NULL THEN
                    SELECT count(*)::integer INTO current_recipe_count
                    FROM public.item_material_content_definitions
                    WHERE revision = NEW.revision
                      AND recipe_kind IS NOT NULL;
                    IF current_recipe_count >= release.material_recipe_count THEN
                        RAISE EXCEPTION
                            'item revision % already has its declared % recipe rows',
                            NEW.revision, release.material_recipe_count;
                    END IF;
                END IF;
                RETURN NEW;
            END
            $guard_item_material_content_insert$;

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
                recipe_count integer;
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

                IF release.manifest_version IN (2, 3, 4) THEN
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
                    SELECT count(*)::integer INTO recipe_count
                    FROM public.item_material_content_definitions
                    WHERE revision = NEW.revision
                      AND recipe_kind IS NOT NULL;
                    IF attribute_count <> release.attribute_count
                       OR rank_count <> release.equipment_rank_count
                       OR suit_count <> release.holy_suit_effect_count THEN
                        RAISE EXCEPTION
                            'item revision % policy counts are incomplete',
                            NEW.revision;
                    END IF;
                    IF release.manifest_version = 2
                       AND (release.material_policy_count <> 0
                            OR release.material_recipe_count <> 0
                            OR material_count <> 0
                            OR recipe_count <> 0) THEN
                        RAISE EXCEPTION
                            'item manifest version 2 cannot contain material policy';
                    END IF;
                    IF release.manifest_version = 3
                       AND (release.material_policy_count <= 0
                            OR material_count <>
                               release.material_policy_count
                            OR release.material_recipe_count <> 0
                            OR recipe_count <> 0) THEN
                        RAISE EXCEPTION
                            'item revision % material policy is incomplete',
                            NEW.revision;
                    END IF;
                    IF release.manifest_version = 4
                       AND (release.material_policy_count <= 0
                            OR material_count <>
                               release.material_policy_count
                            OR release.material_recipe_count <= 0
                            OR recipe_count <>
                               release.material_recipe_count) THEN
                        RAISE EXCEPTION
                            'item revision % material recipes are incomplete',
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
                       OLD.material_policy_count
                   AND NEW.material_recipe_count =
                       OLD.material_recipe_count THEN
                    RETURN NEW;
                END IF;
                RAISE EXCEPTION
                    'published item-template revisions are immutable';
            END
            $immutable_item_template_content$;
        """;
}
