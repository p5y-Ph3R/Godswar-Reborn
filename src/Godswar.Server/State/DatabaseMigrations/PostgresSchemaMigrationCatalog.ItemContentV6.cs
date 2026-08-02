namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateItemContentV6Release() =>
        new(
            "20260802_052_class_suit_item_content",
            "Publish Class Suit insignias through immutable item manifest v6",
            string.Concat(
                ItemContentV6HeaderSql,
                ItemContentV6InsertGuardsSql,
                ItemContentV6PublicationGuardSql,
                ItemContentV6ViewsSql));

    private const string ItemContentV6HeaderSql = """
        ALTER TABLE public.item_template_content_revisions
            DROP CONSTRAINT ck_item_content_manifest_version,
            DROP CONSTRAINT ck_item_content_policy_counts,
            DROP CONSTRAINT ck_item_content_material_policy_count,
            DROP CONSTRAINT ck_item_content_material_recipe_count,
            DROP CONSTRAINT ck_item_content_holy_suit_counts,
            ADD CONSTRAINT ck_item_content_manifest_version
                CHECK (manifest_version IN (1, 2, 3, 4, 5, 6)),
            ADD CONSTRAINT ck_item_content_policy_counts
                CHECK (
                    (manifest_version = 1
                     AND attribute_count = 0
                     AND equipment_rank_count = 0
                     AND holy_suit_effect_count = 0)
                    OR
                    (manifest_version IN (2, 3, 4, 5, 6)
                     AND attribute_count BETWEEN 1 AND 100000
                     AND equipment_rank_count BETWEEN 1 AND 10000
                     AND holy_suit_effect_count BETWEEN 1 AND 10000)
                ),
            ADD CONSTRAINT ck_item_content_material_policy_count
                CHECK (
                    (manifest_version IN (1, 2)
                     AND material_policy_count = 0)
                    OR
                    (manifest_version IN (3, 4, 5, 6)
                     AND material_policy_count BETWEEN 1 AND 10000)
                ),
            ADD CONSTRAINT ck_item_content_material_recipe_count
                CHECK (
                    (manifest_version IN (1, 2, 3)
                     AND material_recipe_count = 0)
                    OR
                    (manifest_version IN (4, 5, 6)
                     AND material_recipe_count BETWEEN 1
                         AND material_policy_count)
                ),
            ADD CONSTRAINT ck_item_content_holy_suit_counts
                CHECK (
                    (manifest_version IN (1, 2, 3, 4)
                     AND holy_suit_tier_count = 0
                     AND holy_suit_upgrade_count = 0
                     AND holy_suit_consumable_count = 0
                     AND holy_suit_policy_count = 0)
                    OR
                    (manifest_version IN (5, 6)
                     AND holy_suit_tier_count = 8
                     AND holy_suit_upgrade_count = 70
                     AND holy_suit_consumable_count = 13
                     AND holy_suit_policy_count = 1)
                );

        """;

    private const string ItemContentV6ViewsSql = """
        CREATE OR REPLACE VIEW public.official_item_attribute_content
        WITH (security_barrier = true) AS
        SELECT definition.*
        FROM public.item_template_content_publication publication
        JOIN public.item_template_content_revisions release
          ON release.revision = publication.revision
         AND release.sealed_at IS NOT NULL
         AND release.manifest_version IN (2, 3, 4, 5, 6)
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
         AND release.manifest_version IN (2, 3, 4, 5, 6)
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
         AND release.manifest_version IN (2, 3, 4, 5, 6)
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
         AND release.manifest_version IN (3, 4, 5, 6)
         AND release.material_policy_count > 0
        JOIN public.item_material_content_definitions definition
          ON definition.revision = release.revision
        WHERE publication.family = 'items';

        CREATE OR REPLACE VIEW public.official_holy_suit_tier_content
        WITH (security_barrier = true) AS
        SELECT definition.*
        FROM public.item_template_content_publication publication
        JOIN public.item_template_content_revisions release
          ON release.revision = publication.revision
         AND release.sealed_at IS NOT NULL
         AND release.manifest_version IN (5, 6)
        JOIN public.holy_suit_tier_content_definitions definition
          ON definition.revision = release.revision
        WHERE publication.family = 'items';

        CREATE OR REPLACE VIEW public.official_holy_suit_upgrade_content
        WITH (security_barrier = true) AS
        SELECT definition.*
        FROM public.item_template_content_publication publication
        JOIN public.item_template_content_revisions release
          ON release.revision = publication.revision
         AND release.sealed_at IS NOT NULL
         AND release.manifest_version IN (5, 6)
        JOIN public.holy_suit_upgrade_content_definitions definition
          ON definition.revision = release.revision
        WHERE publication.family = 'items';

        CREATE OR REPLACE VIEW public.official_holy_suit_consumable_content
        WITH (security_barrier = true) AS
        SELECT definition.*
        FROM public.item_template_content_publication publication
        JOIN public.item_template_content_revisions release
          ON release.revision = publication.revision
         AND release.sealed_at IS NOT NULL
         AND release.manifest_version IN (5, 6)
        JOIN public.holy_suit_consumable_content_definitions definition
          ON definition.revision = release.revision
        WHERE publication.family = 'items';

        CREATE OR REPLACE VIEW
            public.official_holy_suit_operation_policy_content
        WITH (security_barrier = true) AS
        SELECT definition.*
        FROM public.item_template_content_publication publication
        JOIN public.item_template_content_revisions release
          ON release.revision = publication.revision
         AND release.sealed_at IS NOT NULL
         AND release.manifest_version IN (5, 6)
        JOIN public.holy_suit_operation_policy_content_definitions definition
          ON definition.revision = release.revision
        WHERE publication.family = 'items';

        """;
}
