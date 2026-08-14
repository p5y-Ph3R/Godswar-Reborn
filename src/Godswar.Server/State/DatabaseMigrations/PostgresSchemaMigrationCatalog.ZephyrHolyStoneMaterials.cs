namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        ReconcileZephyrHolyStoneMaterialTemplates() => new(
        "20260810_064_zephyr_holy_stone_material_templates",
        "Reconcile Zephyr Holy Stone materials without rewriting migration 058",
        """
        WITH materials(
            id, name_key, display_name, texture, icon, overlap, special_flag
        ) AS (
            VALUES
                (9032, 'Stone9032', 'Zephyr Holy Stone',
                    './Localization/en_us/UI/Texture/Icon5.gwo',
                    '612,0', '1', 'PreStone'),
                (9090, 'Zephyrholiness1', 'Daedalus Spirit of Attunement',
                    './Localization/en_us/UI/Texture/Icon5.gwo',
                    '648,0', '99', NULL),
                (9091, 'Zephyrholiness2', 'Hephaestus Spirit of Tempering',
                    './Localization/en_us/UI/Texture/Icon5.gwo',
                    '684,0', '99', NULL),
                (9092, 'Zephyrholiness3', 'Mnemosyne Spirit of Preservation',
                    './Localization/en_us/UI/Texture/Icon5.gwo',
                    '720,0', '99', NULL),
                (9093, 'Zephyrholiness4', 'Themis Spirit of Continuity',
                    './Localization/en_us/UI/Texture/Icon5.gwo',
                    '756,0', '99', NULL)
        )
        INSERT INTO public.item_templates (
            id, kind, name_key, display_name, equipment_slot, class_ids,
            min_level, max_level, hand, skill_flag, texture, icon, stats
        )
        SELECT
            material.id,
            'consume item',
            material.name_key,
            material.display_name,
            0,
            '{}'::smallint[],
            NULL,
            NULL,
            NULL,
            NULL,
            material.texture,
            material.icon,
            jsonb_strip_nulls(jsonb_build_object(
                'ID', material.id::text,
                'Type', 'consume item',
                'Texture', material.texture,
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
