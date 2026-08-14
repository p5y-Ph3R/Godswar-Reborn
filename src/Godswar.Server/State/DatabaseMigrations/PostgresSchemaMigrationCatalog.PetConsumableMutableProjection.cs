namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        NormalizePetConsumableMutableProjection() =>
        new(
            "20260811_076_pet_consumable_mutable_projection",
            "Normalize reviewed stock pet consumables without overwriting custom content",
            """
            DO $normalize_pet_consumable_projection$
            DECLARE
                conflicting_ids text;
            BEGIN
                WITH expected (
                    id,
                    name_key,
                    display_name,
                    icon,
                    stats
                ) AS (
                    VALUES
                        (
                            10103,
                            'Pet10103',
                            'Merged Spirit',
                            '756,972',
                            '{"ID":"10103","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"756,972","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","ItemType":"15"}'::jsonb
                        ),
                        (
                            10104,
                            'Pet10104',
                            'Rebirth Spirit',
                            '792,972',
                            '{"ID":"10104","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"792,972","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","ItemType":"16"}'::jsonb
                        ),
                        (
                            10105,
                            'Pet10105',
                            'Contract Spirit',
                            '828,972',
                            '{"ID":"10105","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"828,972","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","ItemType":"17"}'::jsonb
                        ),
                        (
                            10107,
                            'Pet10107',
                            'Spring Water',
                            '900,972',
                            '{"ID":"10107","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"900,972","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","Use":"1","ItemType":"12"}'::jsonb
                        )
                )
                SELECT string_agg(expected.id::text, ',' ORDER BY expected.id)
                INTO conflicting_ids
                FROM expected
                LEFT JOIN public.item_templates template
                  ON template.id = expected.id
                WHERE template.id IS NULL
                   OR template.kind IS DISTINCT FROM 'consume item'
                   OR template.name_key IS DISTINCT FROM expected.name_key
                   OR template.display_name IS DISTINCT FROM expected.display_name
                   OR template.equipment_slot NOT IN (-1, 0)
                   OR template.class_ids IS DISTINCT FROM '{}'::smallint[]
                   OR template.min_level IS NOT NULL
                   OR template.max_level IS NOT NULL
                   OR template.hand IS NOT NULL
                   OR template.skill_flag IS NOT NULL
                   OR template.texture IS DISTINCT FROM
                        './Localization/en_us/UI/Texture/Icon2.gwo'
                   OR template.icon IS DISTINCT FROM expected.icon
                   OR template.stats IS DISTINCT FROM expected.stats;

                IF conflicting_ids IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Reviewed stock pet consumables conflict at item IDs: %',
                        conflicting_ids;
                END IF;

                UPDATE public.item_templates
                SET equipment_slot = 0
                WHERE id IN (10103, 10104, 10105, 10107)
                  AND equipment_slot = -1;
            END
            $normalize_pet_consumable_projection$;
            """);
}
