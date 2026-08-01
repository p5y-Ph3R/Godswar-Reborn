namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateItemMaterialRecipeContentRelease() => new(
            "20260801_045_item_material_recipe_content_release",
            "Complete immutable item-material recipes in manifest version 4",
            string.Concat(
            """
            ALTER TABLE public.item_template_content_revisions
                DROP CONSTRAINT ck_item_content_manifest_version,
                DROP CONSTRAINT ck_item_content_policy_counts,
                DROP CONSTRAINT ck_item_content_material_policy_count,
                ADD COLUMN material_recipe_count integer NOT NULL DEFAULT 0,
                ADD CONSTRAINT ck_item_content_manifest_version
                    CHECK (manifest_version IN (1, 2, 3, 4)),
                ADD CONSTRAINT ck_item_content_policy_counts
                    CHECK (
                        (manifest_version = 1
                         AND attribute_count = 0
                         AND equipment_rank_count = 0
                         AND holy_suit_effect_count = 0)
                        OR
                        (manifest_version IN (2, 3, 4)
                         AND attribute_count BETWEEN 1 AND 100000
                         AND equipment_rank_count BETWEEN 1 AND 10000
                         AND holy_suit_effect_count BETWEEN 1 AND 10000)
                    ),
                ADD CONSTRAINT ck_item_content_material_policy_count
                    CHECK (
                        (manifest_version IN (1, 2)
                         AND material_policy_count = 0)
                        OR
                        (manifest_version IN (3, 4)
                         AND material_policy_count BETWEEN 1 AND 10000)
                    ),
                ADD CONSTRAINT ck_item_content_material_recipe_count
                    CHECK (
                        (manifest_version IN (1, 2, 3)
                         AND material_recipe_count = 0)
                        OR
                        (manifest_version = 4
                         AND material_recipe_count BETWEEN 1
                             AND material_policy_count)
                    );

            ALTER TABLE public.item_material_content_definitions
                DROP CONSTRAINT ck_item_material_content_optional_values,
                DROP CONSTRAINT ck_item_material_content_shape,
                ADD COLUMN recipe_kind varchar(32),
                ADD COLUMN source_quantity integer,
                ADD COLUMN target_quantity integer,
                ADD CONSTRAINT ck_item_material_content_recipe_kind
                    CHECK (
                        recipe_kind IS NULL OR recipe_kind IN (
                            'crystal_transform',
                            'gem_piece_combination'
                        )
                    ),
                ADD CONSTRAINT ck_item_material_content_optional_values
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
                        AND (source_quantity IS NULL OR
                            source_quantity BETWEEN 1 AND 32767)
                        AND (target_quantity IS NULL OR
                            target_quantity BETWEEN 1 AND 32767)
                    ),
                ADD CONSTRAINT ck_item_material_content_shape
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
                         AND recipe_quantity IS NULL
                         AND (
                             (recipe_kind IS NULL
                              AND target_item_id IS NULL
                              AND source_quantity IS NULL
                              AND target_quantity IS NULL)
                             OR
                             (recipe_kind = 'crystal_transform'
                              AND NOT is_piece
                              AND target_item_id IS NOT NULL
                              AND source_quantity IS NOT NULL
                              AND target_quantity IS NOT NULL)
                             OR
                             (recipe_kind = 'gem_piece_combination'
                              AND is_piece
                              AND target_item_id IS NOT NULL
                              AND source_quantity IS NOT NULL
                              AND target_quantity IS NOT NULL)
                         ))
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
                         AND recipe_quantity IS NULL
                         AND recipe_kind IS NULL
                         AND source_quantity IS NULL
                         AND target_quantity IS NULL)
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
                         AND recipe_quantity IS NULL
                         AND recipe_kind IS NULL
                         AND source_quantity IS NULL
                         AND target_quantity IS NULL)
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
                         AND recipe_quantity IS NULL
                         AND recipe_kind IS NULL
                         AND source_quantity IS NULL
                         AND target_quantity IS NULL)
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
                         AND recipe_quantity IS NOT NULL
                         AND recipe_kind IS NULL
                         AND source_quantity IS NULL
                         AND target_quantity IS NULL)
                    );

            """,
            ItemMaterialRecipeGuardSql,
            ItemMaterialRecipeViewSql));
}
