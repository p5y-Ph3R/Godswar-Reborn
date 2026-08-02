namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateHolySuitContentRelease() =>
        new(
            "20260801_046_holy_suit_content_release",
            "Publish Holy Suit policy in item manifest v5 and add durable usage state",
            string.Concat(
                HolySuitManifestHeaderSql,
                HolySuitContentTablesSql,
                HolySuitDurableStateSql,
                HolySuitInsertGuardSql,
                HolySuitPublicationGuardSql,
                HolySuitContentViewSql));

    private const string HolySuitManifestHeaderSql = """
        ALTER TABLE public.item_template_content_revisions
            DROP CONSTRAINT ck_item_content_manifest_version,
            DROP CONSTRAINT ck_item_content_policy_counts,
            DROP CONSTRAINT ck_item_content_material_policy_count,
            DROP CONSTRAINT ck_item_content_material_recipe_count,
            ADD COLUMN holy_suit_tier_count integer NOT NULL DEFAULT 0,
            ADD COLUMN holy_suit_upgrade_count integer NOT NULL DEFAULT 0,
            ADD COLUMN holy_suit_consumable_count integer NOT NULL DEFAULT 0,
            ADD COLUMN holy_suit_policy_count integer NOT NULL DEFAULT 0,
            ADD CONSTRAINT ck_item_content_manifest_version
                CHECK (manifest_version IN (1, 2, 3, 4, 5)),
            ADD CONSTRAINT ck_item_content_policy_counts
                CHECK (
                    (manifest_version = 1
                     AND attribute_count = 0
                     AND equipment_rank_count = 0
                     AND holy_suit_effect_count = 0)
                    OR
                    (manifest_version IN (2, 3, 4, 5)
                     AND attribute_count BETWEEN 1 AND 100000
                     AND equipment_rank_count BETWEEN 1 AND 10000
                     AND holy_suit_effect_count BETWEEN 1 AND 10000)
                ),
            ADD CONSTRAINT ck_item_content_material_policy_count
                CHECK (
                    (manifest_version IN (1, 2)
                     AND material_policy_count = 0)
                    OR
                    (manifest_version IN (3, 4, 5)
                     AND material_policy_count BETWEEN 1 AND 10000)
                ),
            ADD CONSTRAINT ck_item_content_material_recipe_count
                CHECK (
                    (manifest_version IN (1, 2, 3)
                     AND material_recipe_count = 0)
                    OR
                    (manifest_version IN (4, 5)
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
                    (manifest_version = 5
                     AND holy_suit_tier_count = 8
                     AND holy_suit_upgrade_count = 70
                     AND holy_suit_consumable_count = 13
                     AND holy_suit_policy_count = 1)
                );

        """;
}
