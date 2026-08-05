namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateSocketSpellItemTemplates() =>
        new(
            "20260804_057_socket_spell_item_templates",
            "Seed stock Socket Spells for immutable publication and inventory references",
            """
            WITH socket_spells(id, name_key, display_name) AS (
                VALUES
                    (4270, 'Smithing4270', 'Socket Spell I'),
                    (4271, 'Smithing4271', 'Socket Spell II'),
                    (4272, 'Smithing4272', 'Socket Spell III'),
                    (4273, 'Smithing4273', 'Socket Spell IV')
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
                spell.id,
                'consume item',
                spell.name_key,
                spell.display_name,
                0,
                '{}'::smallint[],
                NULL,
                NULL,
                NULL,
                NULL,
                './Localization/en_us/UI/Texture/Icon.gwo',
                '108,900',
                jsonb_build_object(
                    'ID', spell.id::text,
                    'Type', 'consume item',
                    'Texture',
                        './Localization/en_us/UI/Texture/Icon.gwo',
                    'Icon', '108,900',
                    'Random', '0',
                    'Distribution', '0,0',
                    'Money', '0',
                    'Overlap', '99'
                )
            FROM socket_spells AS spell
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
