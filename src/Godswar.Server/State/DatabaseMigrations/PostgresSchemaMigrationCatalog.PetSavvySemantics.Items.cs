namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string PetSavvySemanticsItemAndValidationSql =
        """
        WITH rebirth_items (
            id,
            name_key,
            display_name,
            texture,
            icon,
            pet_limit
        ) AS (
            VALUES
                (
                    10145,
                    'Pet10145',
                    'Juice of Rebirth',
                    './Localization/en_us/UI/Texture/Icon.gwo',
                    '252,36',
                    NULL::text
                ),
                (
                    10146,
                    'Pet10146',
                    'Juice of Rebirth (Limited)',
                    './Localization/en_us/UI/Texture/Icon.gwo',
                    '252,36',
                    '1'
                ),
                (
                    11010,
                    'Pet11010',
                    'Spring Water (Restricted)',
                    './Localization/en_us/UI/Texture/Icon2.gwo',
                    '900,972',
                    '1'
                ),
                (
                    11095,
                    'Pet11095',
                    'Ambrosia of Rebirth',
                    './Localization/en_us/UI/Texture/Icon.gwo',
                    '252,36',
                    NULL
                )
        )
        INSERT INTO public.item_templates (
            id,
            kind,
            name_key,
            display_name,
            equipment_slot,
            class_ids,
            min_level,
            max_level,
            hand,
            skill_flag,
            texture,
            icon,
            stats
        )
        SELECT
            id,
            'consume item',
            name_key,
            display_name,
            -1,
            '{}'::smallint[],
            NULL,
            NULL,
            NULL,
            NULL,
            texture,
            icon,
            jsonb_strip_nulls(jsonb_build_object(
                'ID', id::text,
                'Type', 'consume item',
                'Texture', texture,
                'Icon', icon,
                'Random', '0',
                'Distribution', '0,0',
                'Money', '0',
                'Overlap', '99',
                'Use', '1',
                'ItemType', '22',
                'Petlimit', pet_limit
            ))
        FROM rebirth_items
        ON CONFLICT (id) DO UPDATE
        SET kind = EXCLUDED.kind,
            name_key = EXCLUDED.name_key,
            display_name = EXCLUDED.display_name,
            equipment_slot = EXCLUDED.equipment_slot,
            class_ids = EXCLUDED.class_ids,
            min_level = EXCLUDED.min_level,
            max_level = EXCLUDED.max_level,
            hand = EXCLUDED.hand,
            skill_flag = EXCLUDED.skill_flag,
            texture = EXCLUDED.texture,
            icon = EXCLUDED.icon,
            stats = EXCLUDED.stats;

        DO $validate_pet_savvy_semantics$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM public.character_pets pet
                INNER JOIN public.pet_aptitude_templates aptitude
                    ON aptitude.aptitude = pet.aptitude
                LEFT JOIN public.character_pet_stat_values stat
                    ON stat.pet_id = pet.id
                WHERE pet.rarity_added_savvy_baseline_total IS NOT NULL
                GROUP BY
                    pet.id,
                    pet.rarity_added_savvy_baseline_total,
                    pet.rarity_added_savvy_policy_version,
                    pet.initial_savvy_source_version,
                    aptitude.minimum_added_savvy,
                    aptitude.maximum_added_savvy
                HAVING count(stat.stat_code) <> 6
                    OR count(DISTINCT stat.stat_code) <> 6
                    OR pet.rarity_added_savvy_policy_version
                        <> 'project-v2'
                    OR pet.initial_savvy_source_version
                        <> 'growth-x1-v1'
                    OR pet.rarity_added_savvy_baseline_total
                        < aptitude.minimum_added_savvy
                    OR pet.rarity_added_savvy_baseline_total
                        > aptitude.maximum_added_savvy
                    OR count(*) FILTER (
                        WHERE stat.birth_initial_savvy IS NULL
                           OR stat.rarity_added_savvy IS NULL
                           OR stat.initial_savvy
                                < stat.birth_initial_savvy
                           OR stat.added_savvy
                                < stat.rarity_added_savvy
                    ) > 0
                    OR COALESCE(sum(stat.rarity_added_savvy), 0)
                        <> pet.rarity_added_savvy_baseline_total
            ) THEN
                RAISE EXCEPTION
                    'Pet savvy semantics correction produced an invalid baseline state';
            END IF;
        END
        $validate_pet_savvy_semantics$;
        """;
}
