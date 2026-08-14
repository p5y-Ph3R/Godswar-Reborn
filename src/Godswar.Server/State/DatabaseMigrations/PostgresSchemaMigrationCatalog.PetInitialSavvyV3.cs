namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetInitialSavvyPolicyV3() => new(
            "20260811_070_pet_initial_savvy_policy_v3",
            "Preserve high-Savvy pets under an explicit legacy policy before publishing the lower hatch ladder",
            """
            CREATE TABLE public.pet_initial_savvy_v3_legacy_archive (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                migration_id varchar(128) NOT NULL,
                pet_id_snapshot bigint NOT NULL,
                owner_user_id_snapshot integer NOT NULL,
                aptitude_snapshot smallint NOT NULL,
                old_pet_revision bigint NOT NULL,
                old_initial_savvy_baseline_total integer NOT NULL,
                old_initial_savvy_policy_version varchar(32) NOT NULL,
                old_rarity_savvy_baseline_total integer NOT NULL,
                old_rarity_savvy_policy_version varchar(32) NOT NULL,
                old_initial_savvy_source_version varchar(32) NOT NULL,
                old_stat_rows jsonb NOT NULL,
                archived_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT ux_pet_initial_savvy_v3_legacy_archive
                    UNIQUE (migration_id, pet_id_snapshot),
                CONSTRAINT ck_pet_initial_savvy_v3_legacy_archive_revision
                    CHECK (old_pet_revision >= 0),
                CONSTRAINT ck_pet_initial_savvy_v3_legacy_archive_stats
                    CHECK (
                        jsonb_typeof(old_stat_rows) = 'array'
                        AND jsonb_array_length(old_stat_rows) = 6
                    )
            );

            DO $preflight_pet_initial_savvy_policy_v3$
            BEGIN
                IF EXISTS (
                    SELECT pet.id
                    FROM public.character_pets pet
                    LEFT JOIN public.character_pet_stat_values stat
                      ON stat.pet_id = pet.id
                    WHERE pet.initial_savvy_source_version =
                            'savvy-plus-growth-v2'
                    GROUP BY
                        pet.id,
                        pet.revision,
                        pet.initial_savvy_baseline_total,
                        pet.initial_savvy_policy_version,
                        pet.rarity_added_savvy_baseline_total,
                        pet.rarity_added_savvy_policy_version
                    HAVING pet.revision < 0
                        OR pet.initial_savvy_baseline_total IS NULL
                        OR pet.rarity_added_savvy_baseline_total IS NULL
                        OR pet.initial_savvy_baseline_total < 1
                        OR pet.initial_savvy_baseline_total IS DISTINCT FROM
                            pet.rarity_added_savvy_baseline_total
                        OR pet.initial_savvy_policy_version IS NULL
                        OR pet.rarity_added_savvy_policy_version IS NULL
                        OR pet.initial_savvy_policy_version IS DISTINCT FROM
                            pet.rarity_added_savvy_policy_version
                        OR pet.initial_savvy_policy_version NOT IN (
                            'project-v1',
                            'project-v2'
                        )
                        OR count(stat.stat_code) <> 6
                        OR count(DISTINCT stat.stat_code) <> 6
                        OR count(*) FILTER (
                            WHERE stat.stat_code NOT BETWEEN 1 AND 6
                               OR stat.birth_initial_savvy IS NULL
                               OR stat.rarity_added_savvy IS NULL
                               OR stat.base_growth_rate <= 0
                               OR stat.initial_savvy <
                                    stat.birth_initial_savvy
                               OR stat.added_savvy <
                                    stat.base_growth_rate
                               OR stat.birth_initial_savvy IS DISTINCT FROM
                                    stat.rarity_added_savvy
                        ) > 0
                        OR sum(stat.birth_initial_savvy) IS DISTINCT FROM
                            pet.initial_savvy_baseline_total
                        OR sum(stat.rarity_added_savvy) IS DISTINCT FROM
                            pet.rarity_added_savvy_baseline_total
                ) THEN
                    RAISE EXCEPTION
                        'A Savvy-v2 pet cannot be assigned the legacy high-Savvy policy without guessing';
                END IF;
            END
            $preflight_pet_initial_savvy_policy_v3$;

            INSERT INTO public.pet_initial_savvy_v3_legacy_archive (
                migration_id,
                pet_id_snapshot,
                owner_user_id_snapshot,
                aptitude_snapshot,
                old_pet_revision,
                old_initial_savvy_baseline_total,
                old_initial_savvy_policy_version,
                old_rarity_savvy_baseline_total,
                old_rarity_savvy_policy_version,
                old_initial_savvy_source_version,
                old_stat_rows
            )
            SELECT
                '20260811_070_pet_initial_savvy_policy_v3',
                pet.id,
                pet.user_id,
                pet.aptitude,
                pet.revision,
                pet.initial_savvy_baseline_total,
                pet.initial_savvy_policy_version,
                pet.rarity_added_savvy_baseline_total,
                pet.rarity_added_savvy_policy_version,
                pet.initial_savvy_source_version,
                (
                    SELECT jsonb_agg(to_jsonb(stat) ORDER BY stat.stat_code)
                    FROM public.character_pet_stat_values stat
                    WHERE stat.pet_id = pet.id
                )
            FROM public.character_pets pet
            WHERE pet.initial_savvy_source_version = 'savvy-plus-growth-v2'
              AND pet.initial_savvy_policy_version IN (
                    'project-v1',
                    'project-v2'
              )
            ORDER BY pet.id;

            DO $label_legacy_high_savvy_pets$
            DECLARE
                expected_rows integer;
                updated_rows integer;
            BEGIN
                SELECT count(*)::integer
                INTO expected_rows
                FROM public.pet_initial_savvy_v3_legacy_archive
                WHERE migration_id =
                    '20260811_070_pet_initial_savvy_policy_v3';

                UPDATE public.character_pets pet
                SET initial_savvy_policy_version =
                        'legacy-high-savvy-range-v1',
                    rarity_added_savvy_policy_version =
                        'legacy-high-savvy-range-v1',
                    revision = archived.old_pet_revision + 1,
                    updated_at = transaction_timestamp()
                FROM public.pet_initial_savvy_v3_legacy_archive archived
                WHERE archived.migration_id =
                        '20260811_070_pet_initial_savvy_policy_v3'
                  AND pet.id = archived.pet_id_snapshot;

                GET DIAGNOSTICS updated_rows = ROW_COUNT;
                IF updated_rows <> expected_rows THEN
                    RAISE EXCEPTION
                        'Expected to label % legacy high-Savvy pets, updated %',
                        expected_rows,
                        updated_rows;
                END IF;
            END
            $label_legacy_high_savvy_pets$;

            DO $validate_pet_initial_savvy_policy_v3$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_pets pet
                    WHERE pet.initial_savvy_source_version =
                            'savvy-plus-growth-v2'
                      AND pet.initial_savvy_policy_version IN (
                            'project-v1',
                            'project-v2'
                      )
                ) THEN
                    RAISE EXCEPTION
                        'An ambiguous pre-v3 Savvy policy label remains';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.pet_initial_savvy_v3_legacy_archive archived
                    JOIN public.character_pets pet
                      ON pet.id = archived.pet_id_snapshot
                    WHERE archived.migration_id =
                            '20260811_070_pet_initial_savvy_policy_v3'
                      AND (
                          pet.user_id <> archived.owner_user_id_snapshot
                          OR pet.aptitude <> archived.aptitude_snapshot
                          OR pet.initial_savvy_baseline_total <>
                              archived.old_initial_savvy_baseline_total
                          OR pet.rarity_added_savvy_baseline_total <>
                              archived.old_rarity_savvy_baseline_total
                          OR pet.initial_savvy_source_version <>
                              archived.old_initial_savvy_source_version
                          OR pet.initial_savvy_policy_version <>
                              'legacy-high-savvy-range-v1'
                          OR pet.rarity_added_savvy_policy_version <>
                              'legacy-high-savvy-range-v1'
                          OR pet.revision <> archived.old_pet_revision + 1
                          OR (
                              SELECT jsonb_agg(
                                  to_jsonb(stat)
                                  ORDER BY stat.stat_code
                              )
                              FROM public.character_pet_stat_values stat
                              WHERE stat.pet_id = pet.id
                          ) <> archived.old_stat_rows
                      )
                ) THEN
                    RAISE EXCEPTION
                        'Legacy high-Savvy pet preservation failed parity validation';
                END IF;
            END
            $validate_pet_initial_savvy_policy_v3$;
            """);
}
