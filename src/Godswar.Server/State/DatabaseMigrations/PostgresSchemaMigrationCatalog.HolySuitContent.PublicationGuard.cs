namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string HolySuitPublicationGuardSql = """
        CREATE OR REPLACE FUNCTION public.validate_item_template_content_publication()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $validate_item_template_content_publication$
        DECLARE
            release public.item_template_content_revisions%ROWTYPE;
            template_count integer;
            attribute_count integer;
            rank_count integer;
            suit_effect_count integer;
            material_count integer;
            recipe_count integer;
            suit_tier_count integer;
            suit_upgrade_count integer;
            suit_consumable_count integer;
            suit_policy_count integer;
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

            SELECT count(*)::integer INTO attribute_count
            FROM public.item_attribute_content_definitions
            WHERE revision = NEW.revision;
            SELECT count(*)::integer INTO rank_count
            FROM public.equipment_rank_content_definitions
            WHERE revision = NEW.revision;
            SELECT count(*)::integer INTO suit_effect_count
            FROM public.holy_suit_effect_content_definitions
            WHERE revision = NEW.revision;
            SELECT count(*)::integer INTO material_count
            FROM public.item_material_content_definitions
            WHERE revision = NEW.revision;
            SELECT count(*)::integer INTO recipe_count
            FROM public.item_material_content_definitions
            WHERE revision = NEW.revision
              AND recipe_kind IS NOT NULL;
            SELECT count(*)::integer INTO suit_tier_count
            FROM public.holy_suit_tier_content_definitions
            WHERE revision = NEW.revision;
            SELECT count(*)::integer INTO suit_upgrade_count
            FROM public.holy_suit_upgrade_content_definitions
            WHERE revision = NEW.revision;
            SELECT count(*)::integer INTO suit_consumable_count
            FROM public.holy_suit_consumable_content_definitions
            WHERE revision = NEW.revision;
            SELECT count(*)::integer INTO suit_policy_count
            FROM public.holy_suit_operation_policy_content_definitions
            WHERE revision = NEW.revision;

            IF release.manifest_version = 1 THEN
                IF attribute_count <> 0 OR rank_count <> 0
                   OR suit_effect_count <> 0 OR material_count <> 0
                   OR recipe_count <> 0 THEN
                    RAISE EXCEPTION
                        'item manifest version 1 cannot contain policy';
                END IF;
            ELSE
                IF attribute_count <> release.attribute_count
                   OR rank_count <> release.equipment_rank_count
                   OR suit_effect_count <> release.holy_suit_effect_count THEN
                    RAISE EXCEPTION
                        'item revision % policy counts are incomplete',
                        NEW.revision;
                END IF;
            END IF;

            IF release.manifest_version IN (1, 2) THEN
                IF material_count <> 0 OR recipe_count <> 0
                   OR release.material_policy_count <> 0
                   OR release.material_recipe_count <> 0 THEN
                    RAISE EXCEPTION
                        'item manifest version % cannot contain material policy',
                        release.manifest_version;
                END IF;
            ELSIF release.manifest_version = 3 THEN
                IF material_count <> release.material_policy_count
                   OR release.material_policy_count <= 0
                   OR recipe_count <> 0
                   OR release.material_recipe_count <> 0 THEN
                    RAISE EXCEPTION
                        'item revision % material policy is incomplete',
                        NEW.revision;
                END IF;
            ELSE
                IF material_count <> release.material_policy_count
                   OR release.material_policy_count <= 0
                   OR recipe_count <> release.material_recipe_count
                   OR release.material_recipe_count <= 0 THEN
                    RAISE EXCEPTION
                        'item revision % material recipes are incomplete',
                        NEW.revision;
                END IF;
            END IF;

            IF release.manifest_version = 5 THEN
                IF suit_tier_count <> release.holy_suit_tier_count
                   OR suit_upgrade_count <> release.holy_suit_upgrade_count
                   OR suit_consumable_count <>
                        release.holy_suit_consumable_count
                   OR suit_policy_count <> release.holy_suit_policy_count THEN
                    RAISE EXCEPTION
                        'item revision % Holy Suit policy is incomplete',
                        NEW.revision;
                END IF;
            ELSIF suit_tier_count <> 0 OR suit_upgrade_count <> 0
                  OR suit_consumable_count <> 0 OR suit_policy_count <> 0
                  OR release.holy_suit_tier_count <> 0
                  OR release.holy_suit_upgrade_count <> 0
                  OR release.holy_suit_consumable_count <> 0
                  OR release.holy_suit_policy_count <> 0 THEN
                RAISE EXCEPTION
                    'item manifest version % cannot contain Holy Suit policy',
                    release.manifest_version;
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
               AND NEW.holy_suit_effect_count = OLD.holy_suit_effect_count
               AND NEW.material_policy_count = OLD.material_policy_count
               AND NEW.material_recipe_count = OLD.material_recipe_count
               AND NEW.holy_suit_tier_count = OLD.holy_suit_tier_count
               AND NEW.holy_suit_upgrade_count = OLD.holy_suit_upgrade_count
               AND NEW.holy_suit_consumable_count =
                    OLD.holy_suit_consumable_count
               AND NEW.holy_suit_policy_count = OLD.holy_suit_policy_count THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION
                'published item-template revisions are immutable';
        END
        $immutable_item_template_content$;

        """;
}
