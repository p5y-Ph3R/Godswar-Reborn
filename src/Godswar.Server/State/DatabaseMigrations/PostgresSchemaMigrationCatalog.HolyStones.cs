namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateHolyStoneMaterialTemplates() => new(
        "20260730_029_holy_stone_material_templates",
        "Seed the client-authored Heated Holy Stone and Fire Spirits",
        """
        WITH materials(
            id,
            name_key,
            display_name,
            icon,
            overlap,
            special_flag
        ) AS (
            VALUES
                (9030, 'Stone9030', 'Heated Holy Stone',
                    '252,0', '1', 'PreStone'),
                (9060, 'Firegholiness1',
                    'Fire Spirit of Destruction', '360,0', '99', NULL),
                (9061, 'Firegholiness2',
                    'Fire Spirit of Penetration', '396,0', '99', NULL),
                (9062, 'Firegholiness3',
                    'Fire Spirit of Fist', '432,0', '99', NULL),
                (9063, 'Firegholiness4',
                    'Fire Spirit of Fiery', '468,0', '99', NULL),
                (9064, 'Firegholiness5',
                    'Fire Spirit of Blood', '504,0', '99', NULL),
                (9065, 'Firegholiness6',
                    'Fire Spirit of Pressure', '540,0', '99', NULL),
                (9066, 'Firegholiness7',
                    'Fire Spirit of Assail', '864,0', '99', NULL),
                (9067, 'Firegholiness8',
                    'Fire Spirit of Lightning', '900,0', '99', NULL),
                (9088, 'Firegholiness9',
                    'Fire Spirit of Flow', '828,36', '99', NULL),
                (9089, 'Firegholiness10',
                    'Fire Spirit of Tranquility', '864,36', '99', NULL)
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
            material.id,
            'consume item',
            material.name_key,
            material.display_name,
            -1,
            '{}'::smallint[],
            NULL,
            NULL,
            NULL,
            NULL,
            './Localization/en_us/UI/Texture/Icon2.gwo',
            material.icon,
            jsonb_strip_nulls(jsonb_build_object(
                'ID', material.id::text,
                'Type', 'consume item',
                'Texture',
                    './Localization/en_us/UI/Texture/Icon2.gwo',
                'Icon', material.icon,
                'Random', '0',
                'Distribution', '0,0',
                'Money', '0',
                'Overlap', material.overlap,
                'SpecialFlag', material.special_flag
            ))
        FROM materials AS material
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
        """);
}
