namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string ElementalAttributeConstraintsSql = """
        ALTER TABLE public.character_items
            DROP CONSTRAINT ck_character_items_class_attribute2,
            DROP CONSTRAINT ck_character_items_class_attribute_order,
            DROP CONSTRAINT ck_character_items_distinct_class_attributes,
            DROP CONSTRAINT ck_character_items_class_attribute_placement,
            DROP CONSTRAINT ck_character_items_class_attribute2_placement,
            DROP CONSTRAINT ck_character_items_class_attribute3_placement,
            DROP CONSTRAINT ck_character_items_class_attribute4_placement,
            DROP CONSTRAINT ck_character_items_class_attribute5_placement,
            ADD CONSTRAINT ck_character_items_class_attribute2_deprecated
                CHECK (class_attribute2 IS NULL),
            ADD CONSTRAINT ck_character_items_elemental_attribute1
                CHECK (
                    elemental_attribute1 IS NULL
                    OR elemental_attribute1 BETWEEN 480 AND 500),
            ADD CONSTRAINT ck_character_items_elemental_attribute2
                CHECK (
                    elemental_attribute2 IS NULL
                    OR elemental_attribute2 BETWEEN 480 AND 500),
            ADD CONSTRAINT ck_character_items_elemental_attribute_order
                CHECK (
                    elemental_attribute2 IS NULL
                    OR elemental_attribute1 IS NOT NULL),
            ADD CONSTRAINT ck_character_items_elemental_distinct_elements
                CHECK (
                    elemental_attribute1 IS NULL
                    OR elemental_attribute2 IS NULL
                    OR ((elemental_attribute1 - 480) / 3) <>
                       ((elemental_attribute2 - 480) / 3)),
            ADD CONSTRAINT ck_character_items_dedicated_attribute_grade
                CHECK (
                    (class_attribute1 IS NULL
                     AND elemental_attribute1 IS NULL)
                    OR item_grade BETWEEN 1 AND 25),
            ADD CONSTRAINT ck_character_items_class_attribute_placement
                CHECK (
                    attribute1 IS NULL
                    OR (attribute1 NOT IN (
                        200, 201, 210, 211,
                        220, 221, 230, 231)
                        AND attribute1 NOT BETWEEN 480 AND 500)) NOT VALID,
            ADD CONSTRAINT ck_character_items_class_attribute2_placement
                CHECK (
                    attribute2 IS NULL
                    OR (attribute2 NOT IN (
                        200, 201, 210, 211,
                        220, 221, 230, 231)
                        AND attribute2 NOT BETWEEN 480 AND 500)) NOT VALID,
            ADD CONSTRAINT ck_character_items_class_attribute3_placement
                CHECK (
                    attribute3 IS NULL
                    OR (attribute3 NOT IN (
                        200, 201, 210, 211,
                        220, 221, 230, 231)
                        AND attribute3 NOT BETWEEN 480 AND 500)) NOT VALID,
            ADD CONSTRAINT ck_character_items_class_attribute4_placement
                CHECK (
                    attribute4 IS NULL
                    OR (attribute4 NOT IN (
                        200, 201, 210, 211,
                        220, 221, 230, 231)
                        AND attribute4 NOT BETWEEN 480 AND 500)) NOT VALID,
            ADD CONSTRAINT ck_character_items_class_attribute5_placement
                CHECK (
                    attribute5 IS NULL
                    OR (attribute5 NOT IN (
                        200, 201, 210, 211,
                        220, 221, 230, 231)
                        AND attribute5 NOT BETWEEN 480 AND 500)) NOT VALID,
            ADD CONSTRAINT ck_character_items_elemental_attribute_eligible_gear
                CHECK (
                    elemental_attribute1 IS NULL
                    OR prop_id IN (
        """ +
        ClassSuitAttributeEligibleItemIdsSql +
        """
                    ));

        ALTER TABLE public.character_items
            VALIDATE CONSTRAINT
                ck_character_items_class_attribute_placement,
            VALIDATE CONSTRAINT
                ck_character_items_class_attribute2_placement,
            VALIDATE CONSTRAINT
                ck_character_items_class_attribute3_placement,
            VALIDATE CONSTRAINT
                ck_character_items_class_attribute4_placement,
            VALIDATE CONSTRAINT
                ck_character_items_class_attribute5_placement;

        """;
}
