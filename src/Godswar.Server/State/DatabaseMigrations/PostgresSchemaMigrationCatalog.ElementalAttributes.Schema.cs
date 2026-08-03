namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string ElementalAttributeSchemaSql = """
        DO $elemental_attribute_preflight$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM public.character_items
                WHERE class_attribute2 IS NOT NULL
            ) OR EXISTS (
                SELECT 1
                FROM (
                    SELECT item_state AS state
                    FROM public.character_inventory_baseline_items
                    UNION ALL
                    SELECT before_state
                    FROM public.character_inventory_ledger
                    WHERE before_state IS NOT NULL
                    UNION ALL
                    SELECT after_state
                    FROM public.character_inventory_ledger
                    WHERE after_state IS NOT NULL
                ) historical
                CROSS JOIN LATERAL (
                    SELECT count(DISTINCT candidate) AS class_count
                    FROM unnest(ARRAY[
                        NULLIF(historical.state ->> 'attribute1', '')::smallint,
                        NULLIF(historical.state ->> 'attribute2', '')::smallint,
                        NULLIF(historical.state ->> 'attribute3', '')::smallint,
                        NULLIF(historical.state ->> 'attribute4', '')::smallint,
                        NULLIF(historical.state ->> 'attribute5', '')::smallint,
                        NULLIF(historical.state ->> 'class_attribute1', '')::smallint,
                        NULLIF(historical.state ->> 'classAttribute1', '')::smallint
                    ]) candidate
                    WHERE candidate IN (
                        200, 201, 210, 211,
                        220, 221, 230, 231)
                ) raw_shape
                WHERE NULLIF(
                          historical.state ->> 'class_attribute2',
                          '') IS NOT NULL
                   OR NULLIF(
                          historical.state ->> 'classAttribute2',
                          '') IS NOT NULL
                   OR public.canonical_character_item_state_v2(
                          historical.state)
                          ->> 'class_attribute2' IS NOT NULL
                   OR raw_shape.class_count > 1
            ) THEN
                RAISE EXCEPTION
                    'migration 054 requires operator repair: deprecated class_attribute2 contains player value';
            END IF;
        END
        $elemental_attribute_preflight$;

        ALTER TABLE public.character_items
            ADD COLUMN elemental_attribute1 smallint,
            ADD COLUMN elemental_attribute2 smallint;

        """;

    private const string ElementalAttributeCanonicalStateSql = """
        CREATE OR REPLACE FUNCTION
            public.canonical_character_item_state_v3(item_state jsonb)
        RETURNS jsonb
        LANGUAGE sql
        IMMUTABLE
        STRICT
        PARALLEL SAFE
        AS $canonical_character_item_state_v3$
        SELECT (
            public.canonical_character_item_state_v2(item_state)
                - 'class_attribute2'
                - 'classAttribute1'
                - 'classAttribute2'
                - 'elementalAttribute1'
                - 'elementalAttribute2'
        ) || jsonb_build_object(
            'class_attribute1', NULLIF(COALESCE(
                item_state ->> 'class_attribute1',
                item_state ->> 'classAttribute1',
                public.canonical_character_item_state_v2(item_state)
                    ->> 'class_attribute1'), '')::smallint,
            'elemental_attribute1', NULLIF(COALESCE(
                item_state ->> 'elemental_attribute1',
                item_state ->> 'elementalAttribute1'), '')::smallint,
            'elemental_attribute2', NULLIF(COALESCE(
                item_state ->> 'elemental_attribute2',
                item_state ->> 'elementalAttribute2'), '')::smallint
        );
        $canonical_character_item_state_v3$;

        """;

    private static string CharacterInventoryReconciliationV3Sql =>
        CharacterInventoryReconciliationV2Sql
            .Replace(
                "canonical_character_item_state_v2",
                "canonical_character_item_state_v3",
                StringComparison.Ordinal)
            .Replace("schema v2", "schema v3", StringComparison.Ordinal);
}
