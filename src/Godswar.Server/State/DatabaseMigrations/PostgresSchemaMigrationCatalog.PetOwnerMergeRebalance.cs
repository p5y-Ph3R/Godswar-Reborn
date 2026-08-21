namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    internal static PostgresSchemaMigration
        CreatePetOwnerMergeRebalance() => new(
            "20260821_097_pet_owner_merge_rebalance",
            "Separate Reborn Technique reductions from native Unite effects",
            """
            INSERT INTO public.pet_owner_merge_effect_types (
                effect_code, effect_key, display_name
            ) VALUES
                (
                    1001,
                    'reborn_technique_physical_reduction',
                    'Reborn Technique Physical Reduction'
                ),
                (
                    1002,
                    'reborn_technique_magic_reduction',
                    'Reborn Technique Magical Reduction'
                );

            ALTER TABLE public.character_pet_character_bonuses
                DROP CONSTRAINT
                    character_pet_character_bonuses_effect_code_check,
                ADD CONSTRAINT
                    character_pet_character_bonuses_effect_code_check
                    CHECK (
                        effect_code IN (
                            0, 1, 2, 3, 4, 5, 6, 7,
                            10, 23, 24, 29, 30, 32, 34, 38,
                            1001, 1002
                        )
                    );

            COMMENT ON COLUMN
                public.character_pet_character_bonuses.effect_code IS
                'Native PetUnite effects plus server-only Reborn codes 1001/1002; internal codes are never serialized to PetUnite.';
            """);
}
