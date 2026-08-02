namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string HolySuitInsertGuardSql = """
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
            IF release.manifest_version NOT IN (2, 3, 4, 5) THEN
                RAISE EXCEPTION
                    'item policy requires manifest version 2, 3, 4, or 5';
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
            IF release.manifest_version NOT IN (3, 4, 5)
               OR release.material_policy_count <= 0 THEN
                RAISE EXCEPTION
                    'item material policy requires manifest version 3, 4, or 5';
            END IF;
            IF release.manifest_version = 3
               AND NEW.recipe_kind IS NOT NULL THEN
                RAISE EXCEPTION
                    'item material recipes require manifest version 4 or 5';
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

        CREATE OR REPLACE FUNCTION public.guard_holy_suit_content_insert()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_holy_suit_content_insert$
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
            IF release.manifest_version <> 5 THEN
                RAISE EXCEPTION
                    'Holy Suit policy requires item manifest version 5';
            END IF;

            CASE TG_TABLE_NAME
                WHEN 'holy_suit_tier_content_definitions' THEN
                    expected_count := release.holy_suit_tier_count;
                    SELECT count(*)::integer INTO current_count
                    FROM public.holy_suit_tier_content_definitions
                    WHERE revision = NEW.revision;
                WHEN 'holy_suit_upgrade_content_definitions' THEN
                    expected_count := release.holy_suit_upgrade_count;
                    SELECT count(*)::integer INTO current_count
                    FROM public.holy_suit_upgrade_content_definitions
                    WHERE revision = NEW.revision;
                WHEN 'holy_suit_consumable_content_definitions' THEN
                    expected_count := release.holy_suit_consumable_count;
                    SELECT count(*)::integer INTO current_count
                    FROM public.holy_suit_consumable_content_definitions
                    WHERE revision = NEW.revision;
                WHEN 'holy_suit_operation_policy_content_definitions' THEN
                    expected_count := release.holy_suit_policy_count;
                    SELECT count(*)::integer INTO current_count
                    FROM public.holy_suit_operation_policy_content_definitions
                    WHERE revision = NEW.revision;
                ELSE
                    RAISE EXCEPTION
                        'unexpected Holy Suit content table %', TG_TABLE_NAME;
            END CASE;
            IF current_count >= expected_count THEN
                RAISE EXCEPTION
                    'item revision % already has its declared % rows for %',
                    NEW.revision, expected_count, TG_TABLE_NAME;
            END IF;
            RETURN NEW;
        END
        $guard_holy_suit_content_insert$;

        CREATE TRIGGER trg_holy_suit_tier_content_insert_guard
        BEFORE INSERT ON public.holy_suit_tier_content_definitions
        FOR EACH ROW EXECUTE FUNCTION public.guard_holy_suit_content_insert();
        CREATE TRIGGER trg_holy_suit_upgrade_content_insert_guard
        BEFORE INSERT ON public.holy_suit_upgrade_content_definitions
        FOR EACH ROW EXECUTE FUNCTION public.guard_holy_suit_content_insert();
        CREATE TRIGGER trg_holy_suit_consumable_content_insert_guard
        BEFORE INSERT ON public.holy_suit_consumable_content_definitions
        FOR EACH ROW EXECUTE FUNCTION public.guard_holy_suit_content_insert();
        CREATE TRIGGER trg_holy_suit_operation_policy_content_insert_guard
        BEFORE INSERT ON public.holy_suit_operation_policy_content_definitions
        FOR EACH ROW EXECUTE FUNCTION public.guard_holy_suit_content_insert();

        CREATE TRIGGER trg_holy_suit_tier_content_immutable
        BEFORE UPDATE OR DELETE ON public.holy_suit_tier_content_definitions
        FOR EACH ROW EXECUTE FUNCTION public.reject_item_template_content_mutation();
        CREATE TRIGGER trg_holy_suit_upgrade_content_immutable
        BEFORE UPDATE OR DELETE ON public.holy_suit_upgrade_content_definitions
        FOR EACH ROW EXECUTE FUNCTION public.reject_item_template_content_mutation();
        CREATE TRIGGER trg_holy_suit_consumable_content_immutable
        BEFORE UPDATE OR DELETE ON public.holy_suit_consumable_content_definitions
        FOR EACH ROW EXECUTE FUNCTION public.reject_item_template_content_mutation();
        CREATE TRIGGER trg_holy_suit_operation_policy_content_immutable
        BEFORE UPDATE OR DELETE ON public.holy_suit_operation_policy_content_definitions
        FOR EACH ROW EXECUTE FUNCTION public.reject_item_template_content_mutation();

        """;
}
