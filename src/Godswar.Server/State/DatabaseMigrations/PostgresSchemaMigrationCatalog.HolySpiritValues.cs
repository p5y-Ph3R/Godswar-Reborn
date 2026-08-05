namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateHolySpiritEffectivenessValues() => new(
        "20260805_059_holy_spirit_effectiveness_values",
        "Persist the rolled effectiveness of mounted Holy Spirits",
        string.Concat(
        """
        ALTER TABLE public.character_items
            ADD COLUMN IF NOT EXISTS holy_socket1_value smallint NULL,
            ADD COLUMN IF NOT EXISTS holy_socket2_value smallint NULL,
            ADD COLUMN IF NOT EXISTS holy_socket3_value smallint NULL,
            ADD COLUMN IF NOT EXISTS holy_socket4_value smallint NULL;

        ALTER TABLE public.character_items
            DROP CONSTRAINT IF EXISTS ck_character_items_holy_socket_values,
            ADD CONSTRAINT ck_character_items_holy_socket_values CHECK (
                (holy_socket1_value IS NULL OR
                    holy_socket1_value > 0 AND
                    holy_socket1_effect_id IS NOT NULL AND
                    holy_socket1_level IS NOT NULL) AND
                (holy_socket2_value IS NULL OR
                    holy_socket2_value > 0 AND
                    holy_socket2_effect_id IS NOT NULL AND
                    holy_socket2_level IS NOT NULL) AND
                (holy_socket3_value IS NULL OR
                    holy_socket3_value > 0 AND
                    holy_socket3_effect_id IS NOT NULL AND
                    holy_socket3_level IS NOT NULL) AND
                (holy_socket4_value IS NULL OR
                    holy_socket4_value > 0 AND
                    holy_socket4_effect_id IS NOT NULL AND
                    holy_socket4_level IS NOT NULL)
            );

        COMMENT ON COLUMN public.character_items.holy_socket1_value IS
            'Final rolled Holy Spirit effectiveness value emitted unchanged on the wire.';
        COMMENT ON COLUMN public.character_items.holy_socket2_value IS
            'Final rolled Holy Spirit effectiveness value emitted unchanged on the wire.';
        COMMENT ON COLUMN public.character_items.holy_socket3_value IS
            'Final rolled Holy Spirit effectiveness value emitted unchanged on the wire.';
        COMMENT ON COLUMN public.character_items.holy_socket4_value IS
            'Final rolled Holy Spirit effectiveness value emitted unchanged on the wire.';
        """,
        HolySpiritEffectivenessCanonicalStateSql,
        CharacterInventoryReconciliationV3Sql,
        HolySpiritEffectivenessCompactViewSql));
}
