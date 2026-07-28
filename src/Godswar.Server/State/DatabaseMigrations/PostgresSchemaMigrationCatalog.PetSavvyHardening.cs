namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetSavvySemanticsHardening() => new(
            "20260729_021_pet_savvy_semantics_hardening",
            "Enforce complete pet savvy provenance and immutable birth baselines",
            """
            DO $validate_pet_savvy_semantics_hardening$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_pets pet
                    WHERE NOT (
                        (
                            pet.rarity_added_savvy_baseline_total IS NULL
                            AND pet.rarity_added_savvy_policy_version IS NULL
                            AND pet.initial_savvy_source_version IS NULL
                        )
                        OR (
                            pet.rarity_added_savvy_baseline_total IS NOT NULL
                            AND pet.rarity_added_savvy_policy_version IS NOT NULL
                            AND btrim(
                                pet.rarity_added_savvy_policy_version
                            ) <> ''
                            AND pet.initial_savvy_source_version IS NOT NULL
                            AND btrim(
                                pet.initial_savvy_source_version
                            ) <> ''
                        )
                    )
                ) THEN
                    RAISE EXCEPTION
                        'Pet rarity-savvy provenance must be complete or absent';
                END IF;

                IF EXISTS (
                    SELECT pet.id
                    FROM public.character_pets pet
                    LEFT JOIN public.character_pet_stat_values stat
                        ON stat.pet_id = pet.id
                    WHERE pet.initial_savvy_source_version =
                            'growth-x1-v1'
                    GROUP BY pet.id
                    HAVING count(stat.stat_code) <> 6
                        OR count(DISTINCT stat.stat_code) <> 6
                        OR count(
                            DISTINCT stat.rarity_added_savvy
                        ) < 2
                        OR count(*) FILTER (
                            WHERE stat.birth_initial_savvy
                                    IS DISTINCT FROM
                                        stat.base_growth_rate
                               OR stat.rarity_added_savvy IS NULL
                               OR stat.initial_savvy <
                                    stat.birth_initial_savvy
                               OR stat.added_savvy <
                                    stat.rarity_added_savvy
                        ) > 0
                ) THEN
                    RAISE EXCEPTION
                        'A growth-x1-v1 pet has invalid savvy baselines or progression';
                END IF;
            END
            $validate_pet_savvy_semantics_hardening$;

            ALTER TABLE public.character_pets
                DROP CONSTRAINT ck_character_pets_savvy_provenance,
                ADD CONSTRAINT ck_character_pets_savvy_provenance
                    CHECK (
                        (
                            rarity_added_savvy_baseline_total IS NULL
                            AND rarity_added_savvy_policy_version IS NULL
                            AND initial_savvy_source_version IS NULL
                        )
                        OR (
                            rarity_added_savvy_baseline_total IS NOT NULL
                            AND rarity_added_savvy_policy_version IS NOT NULL
                            AND btrim(
                                rarity_added_savvy_policy_version
                            ) <> ''
                            AND initial_savvy_source_version IS NOT NULL
                            AND btrim(initial_savvy_source_version) <> ''
                        )
                    ) NOT VALID;

            ALTER TABLE public.character_pets
                VALIDATE CONSTRAINT
                    ck_character_pets_savvy_provenance;

            ALTER TABLE public.character_pet_stat_values
                ADD CONSTRAINT ck_pet_stat_growth_x1_birth_baseline
                    CHECK (
                        birth_initial_savvy IS NULL
                        OR birth_initial_savvy = base_growth_rate
                    ) NOT VALID,
                ADD CONSTRAINT ck_pet_stat_initial_savvy_progression
                    CHECK (
                        birth_initial_savvy IS NULL
                        OR initial_savvy >= birth_initial_savvy
                    ) NOT VALID,
                ADD CONSTRAINT ck_pet_stat_added_savvy_progression
                    CHECK (
                        rarity_added_savvy IS NULL
                        OR added_savvy >= rarity_added_savvy
                    ) NOT VALID;

            ALTER TABLE public.character_pet_stat_values
                VALIDATE CONSTRAINT
                    ck_pet_stat_growth_x1_birth_baseline,
                VALIDATE CONSTRAINT
                    ck_pet_stat_initial_savvy_progression,
                VALIDATE CONSTRAINT
                    ck_pet_stat_added_savvy_progression;
            """);
}
