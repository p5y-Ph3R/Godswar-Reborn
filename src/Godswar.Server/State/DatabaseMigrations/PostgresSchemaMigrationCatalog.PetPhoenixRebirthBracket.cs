namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetPhoenixRebirthBracket() => new(
            "20260813_091_pet_phoenix_rebirth_bracket",
            "Version Phoenix previews for completed-Rebirth bracket widening",
            """
            ALTER TABLE public.character_pet_growth_previews
                ADD COLUMN rate_semantics text NOT NULL
                    DEFAULT 'legacy_base_preserve_acceleration',
                ADD COLUMN completed_rebirths smallint,
                ADD COLUMN rebirth_modifiers numeric(18,6)[];

            ALTER TABLE public.character_pet_growth_previews
                ADD CONSTRAINT ck_pet_growth_preview_rate_semantics
                CHECK (
                    (
                        rate_semantics =
                            'legacy_base_preserve_acceleration' AND
                        completed_rebirths IS NULL AND
                        rebirth_modifiers IS NULL
                    ) OR (
                        rate_semantics =
                            'nature_base_rebirth_modifier_v1' AND
                        completed_rebirths IS NOT NULL AND
                        rebirth_modifiers IS NOT NULL AND
                        completed_rebirths BETWEEN 0 AND 100 AND
                        array_ndims(rebirth_modifiers) = 1 AND
                        array_lower(rebirth_modifiers, 1) = 1 AND
                        array_upper(rebirth_modifiers, 1) = 6 AND
                        cardinality(rebirth_modifiers) = 6 AND
                        array_position(rebirth_modifiers, NULL) IS NULL AND
                        0.10 * completed_rebirths <=
                            ALL(rebirth_modifiers) AND
                        0.20 * completed_rebirths >=
                            ALL(rebirth_modifiers) AND
                        rebirth_modifiers[1] * 100 =
                            trunc(rebirth_modifiers[1] * 100) AND
                        rebirth_modifiers[2] * 100 =
                            trunc(rebirth_modifiers[2] * 100) AND
                        rebirth_modifiers[3] * 100 =
                            trunc(rebirth_modifiers[3] * 100) AND
                        rebirth_modifiers[4] * 100 =
                            trunc(rebirth_modifiers[4] * 100) AND
                        rebirth_modifiers[5] * 100 =
                            trunc(rebirth_modifiers[5] * 100) AND
                        rebirth_modifiers[6] * 100 =
                            trunc(rebirth_modifiers[6] * 100)
                    )
                );
            """);
}
