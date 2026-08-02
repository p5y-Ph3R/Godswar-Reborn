namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string HolySuitContentViewSql = """
        CREATE OR REPLACE VIEW public.official_item_attribute_content
        WITH (security_barrier = true) AS
        SELECT definition.*
        FROM public.item_template_content_publication publication
        JOIN public.item_template_content_revisions release
          ON release.revision = publication.revision
         AND release.sealed_at IS NOT NULL
         AND release.manifest_version IN (2, 3, 4, 5)
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
         AND release.manifest_version IN (2, 3, 4, 5)
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
         AND release.manifest_version IN (2, 3, 4, 5)
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
         AND release.manifest_version IN (3, 4, 5)
         AND release.material_policy_count > 0
        JOIN public.item_material_content_definitions definition
          ON definition.revision = release.revision
        WHERE publication.family = 'items';

        CREATE VIEW public.official_holy_suit_tier_content
        WITH (security_barrier = true) AS
        SELECT definition.*
        FROM public.item_template_content_publication publication
        JOIN public.item_template_content_revisions release
          ON release.revision = publication.revision
         AND release.sealed_at IS NOT NULL
         AND release.manifest_version = 5
        JOIN public.holy_suit_tier_content_definitions definition
          ON definition.revision = release.revision
        WHERE publication.family = 'items';

        CREATE VIEW public.official_holy_suit_upgrade_content
        WITH (security_barrier = true) AS
        SELECT definition.*
        FROM public.item_template_content_publication publication
        JOIN public.item_template_content_revisions release
          ON release.revision = publication.revision
         AND release.sealed_at IS NOT NULL
         AND release.manifest_version = 5
        JOIN public.holy_suit_upgrade_content_definitions definition
          ON definition.revision = release.revision
        WHERE publication.family = 'items';

        CREATE VIEW public.official_holy_suit_consumable_content
        WITH (security_barrier = true) AS
        SELECT definition.*
        FROM public.item_template_content_publication publication
        JOIN public.item_template_content_revisions release
          ON release.revision = publication.revision
         AND release.sealed_at IS NOT NULL
         AND release.manifest_version = 5
        JOIN public.holy_suit_consumable_content_definitions definition
          ON definition.revision = release.revision
        WHERE publication.family = 'items';

        CREATE VIEW public.official_holy_suit_operation_policy_content
        WITH (security_barrier = true) AS
        SELECT definition.*
        FROM public.item_template_content_publication publication
        JOIN public.item_template_content_revisions release
          ON release.revision = publication.revision
         AND release.sealed_at IS NOT NULL
         AND release.manifest_version = 5
        JOIN public.holy_suit_operation_policy_content_definitions definition
          ON definition.revision = release.revision
        WHERE publication.family = 'items';

        """;
}
