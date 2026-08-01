namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateItemContentHeaderSealGuard() => new(
            "20260801_043_item_content_header_seal_guard",
            "Protect every item-content header field during sealing",
            """
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
                       OLD.holy_suit_effect_count THEN
                    RETURN NEW;
                END IF;
                RAISE EXCEPTION 'published item-template revisions are immutable';
            END
            $immutable_item_template_content$;
            """);
}
