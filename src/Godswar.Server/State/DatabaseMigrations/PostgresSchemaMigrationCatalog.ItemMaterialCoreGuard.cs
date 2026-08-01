namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string ItemContentV3CorePolicyGuardSql = """

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
            IF release.manifest_version NOT IN (2, 3) THEN
                RAISE EXCEPTION
                    'item policy requires manifest version 2 or 3';
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

        """;
}
