namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateClassSuitAttributeSlots() =>
        new(
            "20260803_053_class_suit_attribute_slots",
            "Separate two Class Suit attributes from five ordinary item attributes",
            """
            ALTER TABLE public.character_items
                ADD COLUMN class_attribute1 smallint,
                ADD COLUMN class_attribute2 smallint;

            CREATE OR REPLACE FUNCTION
                public.canonical_character_item_state_v2(item_state jsonb)
            RETURNS jsonb
            LANGUAGE sql
            IMMUTABLE
            STRICT
            PARALLEL SAFE
            AS $canonical_character_item_state_v2$
            WITH attribute_slots AS (
                SELECT *
                FROM (VALUES
                    (
                        1,
                        NULLIF(item_state ->> 'attribute1', '')::smallint,
                        NULLIF(item_state ->> 'attribute_level1', '')::smallint
                    ),
                    (
                        2,
                        NULLIF(item_state ->> 'attribute2', '')::smallint,
                        NULLIF(item_state ->> 'attribute_level2', '')::smallint
                    ),
                    (
                        3,
                        NULLIF(item_state ->> 'attribute3', '')::smallint,
                        NULLIF(item_state ->> 'attribute_level3', '')::smallint
                    ),
                    (
                        4,
                        NULLIF(item_state ->> 'attribute4', '')::smallint,
                        NULLIF(item_state ->> 'attribute_level4', '')::smallint
                    ),
                    (
                        5,
                        NULLIF(item_state ->> 'attribute5', '')::smallint,
                        NULLIF(item_state ->> 'attribute_level5', '')::smallint
                    )
                ) AS slot(ordinality, attribute_id, attribute_level)
            ),
            reshaped AS (
                SELECT
                    ARRAY(
                        SELECT attribute_id
                        FROM attribute_slots
                        WHERE attribute_id IS NOT NULL
                          AND attribute_id NOT IN (
                              200, 201, 210, 211,
                              220, 221, 230, 231)
                        ORDER BY ordinality
                    ) AS ordinary_attributes,
                    ARRAY(
                        SELECT attribute_level
                        FROM attribute_slots
                        WHERE attribute_id IS NOT NULL
                          AND attribute_id NOT IN (
                              200, 201, 210, 211,
                              220, 221, 230, 231)
                        ORDER BY ordinality
                    ) AS ordinary_levels,
                    ARRAY(
                        SELECT attribute_id
                        FROM attribute_slots
                        WHERE attribute_id IN (
                            200, 201, 210, 211,
                            220, 221, 230, 231)
                        ORDER BY ordinality
                    ) AS legacy_class_attributes
            ),
            dedicated AS (
                SELECT
                    NULLIF(
                        item_state ->> 'class_attribute1',
                        '')::smallint AS class_attribute1,
                    NULLIF(
                        item_state ->> 'class_attribute2',
                        '')::smallint AS class_attribute2
            )
            SELECT item_state || jsonb_build_object(
                'attribute1', reshaped.ordinary_attributes[1],
                'attribute2', reshaped.ordinary_attributes[2],
                'attribute3', reshaped.ordinary_attributes[3],
                'attribute4', reshaped.ordinary_attributes[4],
                'attribute5', reshaped.ordinary_attributes[5],
                'attribute_level1', reshaped.ordinary_levels[1],
                'attribute_level2', reshaped.ordinary_levels[2],
                'attribute_level3', reshaped.ordinary_levels[3],
                'attribute_level4', reshaped.ordinary_levels[4],
                'attribute_level5', reshaped.ordinary_levels[5],
                'class_attribute1', COALESCE(
                    dedicated.class_attribute1,
                    reshaped.legacy_class_attributes[1]),
                'class_attribute2', COALESCE(
                    dedicated.class_attribute2,
                    reshaped.legacy_class_attributes[2])
            )
            FROM reshaped
            CROSS JOIN dedicated;
            $canonical_character_item_state_v2$;

            DO $class_suit_attribute_backfill_guard$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_items item
                    CROSS JOIN LATERAL (
                        SELECT
                            array_agg(value.attribute_id ORDER BY value.ordinality)
                                FILTER (WHERE value.attribute_id IN (
                                    200, 201, 210, 211,
                                    220, 221, 230, 231)) AS class_attributes,
                            count(DISTINCT value.attribute_id)
                                FILTER (WHERE value.attribute_id IN (
                                    200, 201, 210, 211,
                                    220, 221, 230, 231)) AS distinct_class_attributes
                        FROM unnest(ARRAY[
                            item.attribute1,
                            item.attribute2,
                            item.attribute3,
                            item.attribute4,
                            item.attribute5
                        ]) WITH ORDINALITY AS value(attribute_id, ordinality)
                    ) discovered
                    WHERE cardinality(discovered.class_attributes) > 2
                       OR cardinality(discovered.class_attributes) <>
                          discovered.distinct_class_attributes
                ) THEN
                    RAISE EXCEPTION
                        'Cannot migrate character_items with duplicate or more than two Class Suit attributes';
                END IF;
            END
            $class_suit_attribute_backfill_guard$;

            WITH reshaped AS (
                SELECT
                    item.id,
                    ARRAY(
                        SELECT value.attribute_id
                        FROM unnest(
                            ARRAY[
                                item.attribute1,
                                item.attribute2,
                                item.attribute3,
                                item.attribute4,
                                item.attribute5
                            ],
                            ARRAY[
                                item.attribute_level1,
                                item.attribute_level2,
                                item.attribute_level3,
                                item.attribute_level4,
                                item.attribute_level5
                            ]
                        ) WITH ORDINALITY AS value(
                            attribute_id,
                            attribute_level,
                            ordinality)
                        WHERE value.attribute_id IS NOT NULL
                          AND value.attribute_id NOT IN (
                              200, 201, 210, 211,
                              220, 221, 230, 231)
                        ORDER BY value.ordinality
                    ) AS ordinary_attributes,
                    ARRAY(
                        SELECT value.attribute_level
                        FROM unnest(
                            ARRAY[
                                item.attribute1,
                                item.attribute2,
                                item.attribute3,
                                item.attribute4,
                                item.attribute5
                            ],
                            ARRAY[
                                item.attribute_level1,
                                item.attribute_level2,
                                item.attribute_level3,
                                item.attribute_level4,
                                item.attribute_level5
                            ]
                        ) WITH ORDINALITY AS value(
                            attribute_id,
                            attribute_level,
                            ordinality)
                        WHERE value.attribute_id IS NOT NULL
                          AND value.attribute_id NOT IN (
                              200, 201, 210, 211,
                              220, 221, 230, 231)
                        ORDER BY value.ordinality
                    ) AS ordinary_levels,
                    ARRAY(
                        SELECT value.attribute_id
                        FROM unnest(ARRAY[
                            item.attribute1,
                            item.attribute2,
                            item.attribute3,
                            item.attribute4,
                            item.attribute5
                        ]) WITH ORDINALITY AS value(attribute_id, ordinality)
                        WHERE value.attribute_id IN (
                            200, 201, 210, 211,
                            220, 221, 230, 231)
                        ORDER BY value.ordinality
                    ) AS class_attributes
                FROM public.character_items item
            )
            UPDATE public.character_items item
            SET attribute1 = reshaped.ordinary_attributes[1],
                attribute2 = reshaped.ordinary_attributes[2],
                attribute3 = reshaped.ordinary_attributes[3],
                attribute4 = reshaped.ordinary_attributes[4],
                attribute5 = reshaped.ordinary_attributes[5],
                attribute_level1 = reshaped.ordinary_levels[1],
                attribute_level2 = reshaped.ordinary_levels[2],
                attribute_level3 = reshaped.ordinary_levels[3],
                attribute_level4 = reshaped.ordinary_levels[4],
                attribute_level5 = reshaped.ordinary_levels[5],
                class_attribute1 = reshaped.class_attributes[1],
                class_attribute2 = reshaped.class_attributes[2]
            FROM reshaped
            WHERE reshaped.id = item.id
              AND cardinality(reshaped.class_attributes) > 0;

            CREATE OR REPLACE VIEW
                public.character_item_compact_entries
            AS
            SELECT
                ci.user_id,
                ci.item_location,
                ci.slot_index,
                '[' ||
                ci.prop_id::text || ',' ||
                COALESCE(ci.attribute1::text, '') || ',' ||
                COALESCE(ci.attribute2::text, '') || ',' ||
                COALESCE(ci.attribute3::text, '') || ',' ||
                COALESCE(ci.attribute4::text, '') || ',' ||
                COALESCE(ci.attribute5::text, '') || ',' ||
                LEAST(
                    GREATEST(ci.item_quality::integer, 1),
                    COALESCE(
                        quality_limits.base_levels,
                        ci.item_quality::integer))::text || ',' ||
                LEAST(
                    GREATEST(ci.item_grade::integer, 1),
                    LEAST(
                        COALESCE(
                            quality_limits.grade_levels,
                            ci.item_grade::integer),
                        25))::text || ',' ||
                ci.bound::text || ',' ||
                ci.stack::text || ',' ||
                ci.item_exp::text || ',' ||
                ci.holy_suit_code::text || ',' ||
                COALESCE(ci.attribute_level1::text, '') || ',' ||
                COALESCE(ci.attribute_level2::text, '') || ',' ||
                COALESCE(ci.attribute_level3::text, '') || ',' ||
                COALESCE(ci.attribute_level4::text, '') || ',' ||
                COALESCE(ci.attribute_level5::text, '') || ',' ||
                COALESCE(ci.holy_socket_count::text, '0') || ',' ||
                COALESCE(ci.holy_socket1_effect_id::text, '') || ',' ||
                COALESCE(ci.holy_socket1_level::text, '') || ',' ||
                COALESCE(ci.holy_socket2_effect_id::text, '') || ',' ||
                COALESCE(ci.holy_socket2_level::text, '') || ',' ||
                COALESCE(ci.holy_socket3_effect_id::text, '') || ',' ||
                COALESCE(ci.holy_socket3_level::text, '') || ',' ||
                COALESCE(ci.holy_socket4_effect_id::text, '') || ',' ||
                COALESCE(ci.holy_socket4_level::text, '') || ',' ||
                COALESCE(ci.holy_socket5_effect_id::text, '') || ',' ||
                COALESCE(ci.holy_socket5_level::text, '') || ',' ||
                COALESCE(ci.holy_socket6_effect_id::text, '') || ',' ||
                COALESCE(ci.holy_socket6_level::text, '') ||
                CASE
                    WHEN ci.class_attribute1 IS NULL
                     AND ci.class_attribute2 IS NULL
                        THEN ''
                    ELSE
                        ',' ||
                        COALESCE(ci.class_attribute1::text, '') || ',' ||
                        COALESCE(ci.class_attribute2::text, '')
                END ||
                ']' AS compact_entry
            FROM public.character_items ci
            LEFT JOIN public.official_item_template_content it
                ON it.id = ci.prop_id
            LEFT JOIN LATERAL (
                SELECT
                    NULLIF(array_length(string_to_array(
                        NULLIF(it.stats->>'BaseFraction', ''),
                        ','), 1), 0) AS base_levels,
                    NULLIF(array_length(string_to_array(
                        NULLIF(it.stats->>'AppFraction', ''),
                        ','), 1), 0) AS grade_levels
            ) quality_limits ON true;

            ALTER TABLE public.character_items
                ADD CONSTRAINT ck_character_items_class_attribute1
                    CHECK (
                        class_attribute1 IS NULL
                        OR class_attribute1 IN (
                            200, 201, 210, 211,
                            220, 221, 230, 231)),
                ADD CONSTRAINT ck_character_items_class_attribute2
                    CHECK (
                        class_attribute2 IS NULL
                        OR class_attribute2 IN (
                            200, 201, 210, 211,
                            220, 221, 230, 231)),
                ADD CONSTRAINT ck_character_items_class_attribute_order
                    CHECK (class_attribute2 IS NULL OR class_attribute1 IS NOT NULL),
                ADD CONSTRAINT ck_character_items_distinct_class_attributes
                    CHECK (
                        class_attribute1 IS NULL
                        OR class_attribute2 IS NULL
                        OR class_attribute1 <> class_attribute2),
                ADD CONSTRAINT ck_character_items_class_attribute_placement
                    CHECK (
                        attribute1 IS NULL OR attribute1 NOT IN (
                            200, 201, 210, 211,
                            220, 221, 230, 231)
                    ) NOT VALID,
                ADD CONSTRAINT ck_character_items_class_attribute2_placement
                    CHECK (
                        attribute2 IS NULL OR attribute2 NOT IN (
                            200, 201, 210, 211,
                            220, 221, 230, 231)
                    ) NOT VALID,
                ADD CONSTRAINT ck_character_items_class_attribute3_placement
                    CHECK (
                        attribute3 IS NULL OR attribute3 NOT IN (
                            200, 201, 210, 211,
                            220, 221, 230, 231)
                    ) NOT VALID,
                ADD CONSTRAINT ck_character_items_class_attribute4_placement
                    CHECK (
                        attribute4 IS NULL OR attribute4 NOT IN (
                            200, 201, 210, 211,
                            220, 221, 230, 231)
                    ) NOT VALID,
                ADD CONSTRAINT ck_character_items_class_attribute5_placement
                    CHECK (
                        attribute5 IS NULL OR attribute5 NOT IN (
                            200, 201, 210, 211,
                            220, 221, 230, 231)
                    ) NOT VALID,
                ADD CONSTRAINT ck_character_items_class_attribute_eligible_gear
                    CHECK (
                        class_attribute1 IS NULL
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
            """ +
            CharacterInventoryReconciliationV2Sql);
}
