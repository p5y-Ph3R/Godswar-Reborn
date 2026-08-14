namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetScaledAddedValueSemanticsV3() => new(
            "20260811_078_pet_scaled_added_value_v3",
            "Scale pet Added value by effective Growth and level",
            """
            CREATE TABLE public.pet_scaled_added_value_v3_archive (
                migration_id varchar(128) NOT NULL,
                pet_id bigint NOT NULL,
                owner_user_id integer NOT NULL,
                stat_code smallint NOT NULL,
                old_level smallint NOT NULL,
                old_completed_pet_merges integer NOT NULL,
                old_initial_savvy numeric(18, 6) NOT NULL,
                old_added_savvy numeric(18, 6) NOT NULL,
                old_base_growth_rate numeric(18, 6) NOT NULL,
                old_growth_acceleration numeric(18, 6) NOT NULL,
                old_birth_initial_savvy numeric(18, 6) NOT NULL,
                old_rarity_added_savvy numeric(18, 6) NOT NULL,
                old_stat_revision bigint NOT NULL,
                old_pet_revision bigint NOT NULL,
                old_source_version varchar(32) NOT NULL,
                archived_at timestamptz NOT NULL
                    DEFAULT transaction_timestamp(),
                PRIMARY KEY (migration_id, pet_id, stat_code),
                CONSTRAINT ck_pet_scaled_added_archive_stat
                    CHECK (stat_code BETWEEN 1 AND 6),
                CONSTRAINT ck_pet_scaled_added_archive_level
                    CHECK (old_level BETWEEN 1 AND 120),
                CONSTRAINT ck_pet_scaled_added_archive_merges
                    CHECK (old_completed_pet_merges >= 0),
                CONSTRAINT ck_pet_scaled_added_archive_revisions
                    CHECK (old_stat_revision >= 0 AND old_pet_revision >= 0)
            );

            DO $preflight_pet_scaled_added_value_v3$
            DECLARE
                invalid_pet_id bigint;
            BEGIN
                SELECT pet.id
                INTO invalid_pet_id
                FROM public.character_pets pet
                WHERE pet.initial_savvy_source_version IS NOT NULL
                  AND pet.initial_savvy_source_version <>
                        'savvy-plus-growth-v2'
                ORDER BY pet.id
                LIMIT 1;

                IF invalid_pet_id IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Pet % has unsupported Savvy provenance before V3',
                        invalid_pet_id;
                END IF;

                SELECT pet.id
                INTO invalid_pet_id
                FROM public.character_pets pet
                WHERE pet.initial_savvy_source_version =
                        'savvy-plus-growth-v2'
                  AND pet.completed_pet_merges > 0
                ORDER BY pet.id
                LIMIT 1;

                IF invalid_pet_id IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Pet % has historical Merge gains that cannot be reconstructed safely',
                        invalid_pet_id;
                END IF;

                SELECT pet.id
                INTO invalid_pet_id
                FROM public.character_pets pet
                LEFT JOIN public.character_pet_stat_values stat
                  ON stat.pet_id = pet.id
                WHERE pet.initial_savvy_source_version =
                        'savvy-plus-growth-v2'
                GROUP BY
                    pet.id,
                    pet.level,
                    pet.revision,
                    pet.completed_pet_merges,
                    pet.initial_savvy_baseline_total,
                    pet.initial_savvy_policy_version,
                    pet.rarity_added_savvy_baseline_total,
                    pet.rarity_added_savvy_policy_version
                HAVING pet.level NOT BETWEEN 1 AND 120
                    OR pet.revision < 0
                    OR pet.completed_pet_merges <> 0
                    OR pet.initial_savvy_baseline_total IS NULL
                    OR pet.rarity_added_savvy_baseline_total IS NULL
                    OR pet.initial_savvy_baseline_total IS DISTINCT FROM
                        pet.rarity_added_savvy_baseline_total
                    OR pet.initial_savvy_policy_version IS NULL
                    OR pet.rarity_added_savvy_policy_version IS NULL
                    OR pet.initial_savvy_policy_version IS DISTINCT FROM
                        pet.rarity_added_savvy_policy_version
                    OR count(stat.stat_code) <> 6
                    OR count(DISTINCT stat.stat_code) <> 6
                    OR count(*) FILTER (
                        WHERE stat.stat_code NOT BETWEEN 1 AND 6
                           OR stat.initial_savvy < stat.birth_initial_savvy
                           OR stat.added_savvy < stat.base_growth_rate
                           OR stat.base_growth_rate <= 0
                           OR stat.growth_acceleration < 0
                           OR stat.birth_initial_savvy <= 0
                           OR stat.rarity_added_savvy <= 0
                           OR stat.birth_initial_savvy IS DISTINCT FROM
                                stat.rarity_added_savvy
                           OR stat.revision < 0
                    ) > 0
                    OR sum(stat.birth_initial_savvy) IS DISTINCT FROM
                        pet.initial_savvy_baseline_total
                ORDER BY pet.id
                LIMIT 1;

                IF invalid_pet_id IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Pet % cannot enter scaled Added-value V3 without guessing',
                        invalid_pet_id;
                END IF;
            END
            $preflight_pet_scaled_added_value_v3$;

            INSERT INTO public.pet_scaled_added_value_v3_archive (
                migration_id,
                pet_id,
                owner_user_id,
                stat_code,
                old_level,
                old_completed_pet_merges,
                old_initial_savvy,
                old_added_savvy,
                old_base_growth_rate,
                old_growth_acceleration,
                old_birth_initial_savvy,
                old_rarity_added_savvy,
                old_stat_revision,
                old_pet_revision,
                old_source_version
            )
            SELECT
                '20260811_078_pet_scaled_added_value_v3',
                pet.id,
                pet.user_id,
                stat.stat_code,
                pet.level,
                pet.completed_pet_merges,
                stat.initial_savvy,
                stat.added_savvy,
                stat.base_growth_rate,
                stat.growth_acceleration,
                stat.birth_initial_savvy,
                stat.rarity_added_savvy,
                stat.revision,
                pet.revision,
                pet.initial_savvy_source_version
            FROM public.character_pets pet
            JOIN public.character_pet_stat_values stat
              ON stat.pet_id = pet.id
            WHERE pet.initial_savvy_source_version =
                    'savvy-plus-growth-v2'
            ORDER BY pet.id, stat.stat_code;

            DELETE FROM public.character_pet_character_bonuses bonus
            USING (
                SELECT DISTINCT archived.pet_id
                FROM public.pet_scaled_added_value_v3_archive archived
                WHERE archived.migration_id =
                    '20260811_078_pet_scaled_added_value_v3'
            ) affected
            WHERE bonus.pet_id = affected.pet_id;

            ALTER TABLE public.character_pets
                DROP CONSTRAINT ck_character_pets_savvy_provenance;

            ALTER TABLE public.character_pet_stat_values
                DROP CONSTRAINT ck_pet_stat_savvy_birth_baseline,
                DROP CONSTRAINT ck_pet_stat_savvy_progression,
                DROP CONSTRAINT ck_pet_stat_added_value_progression;

            DO $convert_pet_scaled_added_value_stats_v3$
            DECLARE
                expected_rows integer;
                updated_rows integer;
            BEGIN
                SELECT count(*)::integer
                INTO expected_rows
                FROM public.pet_scaled_added_value_v3_archive
                WHERE migration_id =
                    '20260811_078_pet_scaled_added_value_v3';

                UPDATE public.character_pet_stat_values stat
                SET initial_savvy = archived.old_birth_initial_savvy,
                    added_savvy =
                        (
                            archived.old_base_growth_rate +
                            archived.old_growth_acceleration
                        ) * archived.old_level,
                    revision = archived.old_stat_revision + 1
                FROM public.pet_scaled_added_value_v3_archive archived
                WHERE archived.migration_id =
                        '20260811_078_pet_scaled_added_value_v3'
                  AND stat.pet_id = archived.pet_id
                  AND stat.stat_code = archived.stat_code
                  AND stat.initial_savvy = archived.old_initial_savvy
                  AND stat.added_savvy = archived.old_added_savvy
                  AND stat.base_growth_rate =
                        archived.old_base_growth_rate
                  AND stat.growth_acceleration =
                        archived.old_growth_acceleration
                  AND stat.birth_initial_savvy =
                        archived.old_birth_initial_savvy
                  AND stat.rarity_added_savvy =
                        archived.old_rarity_added_savvy
                  AND stat.revision = archived.old_stat_revision;

                GET DIAGNOSTICS updated_rows = ROW_COUNT;
                IF updated_rows <> expected_rows THEN
                    RAISE EXCEPTION
                        'Expected to convert % pet stat rows, updated %',
                        expected_rows,
                        updated_rows;
                END IF;
            END
            $convert_pet_scaled_added_value_stats_v3$;

            DO $convert_pet_scaled_added_value_parents_v3$
            DECLARE
                expected_rows integer;
                updated_rows integer;
            BEGIN
                SELECT count(DISTINCT pet_id)::integer
                INTO expected_rows
                FROM public.pet_scaled_added_value_v3_archive
                WHERE migration_id =
                    '20260811_078_pet_scaled_added_value_v3';

                UPDATE public.character_pets pet
                SET initial_savvy_source_version =
                        'basic-plus-scaled-growth-v3',
                    revision = archived.old_pet_revision + 1,
                    updated_at = transaction_timestamp()
                FROM (
                    SELECT DISTINCT pet_id, old_pet_revision
                    FROM public.pet_scaled_added_value_v3_archive
                    WHERE migration_id =
                        '20260811_078_pet_scaled_added_value_v3'
                ) archived
                WHERE pet.id = archived.pet_id
                  AND pet.initial_savvy_source_version =
                        'savvy-plus-growth-v2'
                  AND pet.completed_pet_merges = 0
                  AND pet.revision = archived.old_pet_revision;

                GET DIAGNOSTICS updated_rows = ROW_COUNT;
                IF updated_rows <> expected_rows THEN
                    RAISE EXCEPTION
                        'Expected to convert % pet parent rows, updated %',
                        expected_rows,
                        updated_rows;
                END IF;
            END
            $convert_pet_scaled_added_value_parents_v3$;

            ALTER TABLE public.character_pets
                ADD CONSTRAINT ck_character_pets_savvy_provenance
                CHECK (
                    (
                        rarity_added_savvy_baseline_total IS NULL
                        AND rarity_added_savvy_policy_version IS NULL
                        AND initial_savvy_source_version IS NULL
                    )
                    OR (
                        rarity_added_savvy_baseline_total IS NOT NULL
                        AND initial_savvy_baseline_total IS NOT NULL
                        AND initial_savvy_baseline_total =
                            rarity_added_savvy_baseline_total
                        AND rarity_added_savvy_policy_version IS NOT NULL
                        AND initial_savvy_policy_version IS NOT NULL
                        AND initial_savvy_policy_version =
                            rarity_added_savvy_policy_version
                        AND initial_savvy_source_version =
                            'basic-plus-scaled-growth-v3'
                    )
                ) NOT VALID;

            ALTER TABLE public.character_pet_stat_values
                ADD CONSTRAINT ck_pet_stat_savvy_birth_baseline
                CHECK (
                    birth_initial_savvy IS NULL
                    OR birth_initial_savvy = rarity_added_savvy
                ) NOT VALID,
                ADD CONSTRAINT ck_pet_stat_savvy_progression
                CHECK (
                    birth_initial_savvy IS NULL
                    OR initial_savvy >= birth_initial_savvy
                ) NOT VALID,
                ADD CONSTRAINT ck_pet_stat_added_value_progression
                CHECK (
                    birth_initial_savvy IS NULL
                    OR added_savvy >=
                        base_growth_rate + growth_acceleration
                ) NOT VALID;

            DO $validate_pet_scaled_added_value_v3$
            DECLARE
                invalid_pet_id bigint;
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_pets pet
                    WHERE pet.initial_savvy_source_version =
                        'savvy-plus-growth-v2'
                ) THEN
                    RAISE EXCEPTION
                        'Obsolete savvy-plus-growth-v2 provenance remains';
                END IF;

                SELECT archived.pet_id
                INTO invalid_pet_id
                FROM public.pet_scaled_added_value_v3_archive archived
                JOIN public.character_pets pet
                  ON pet.id = archived.pet_id
                JOIN public.character_pet_stat_values stat
                  ON stat.pet_id = archived.pet_id
                 AND stat.stat_code = archived.stat_code
                WHERE archived.migration_id =
                        '20260811_078_pet_scaled_added_value_v3'
                  AND (
                    archived.old_source_version <>
                        'savvy-plus-growth-v2'
                    OR archived.old_completed_pet_merges <> 0
                    OR pet.user_id <> archived.owner_user_id
                    OR pet.level <> archived.old_level
                    OR pet.completed_pet_merges <> 0
                    OR pet.initial_savvy_source_version <>
                        'basic-plus-scaled-growth-v3'
                    OR pet.revision <> archived.old_pet_revision + 1
                    OR stat.initial_savvy <>
                        archived.old_birth_initial_savvy
                    OR stat.added_savvy <>
                        (
                            archived.old_base_growth_rate +
                            archived.old_growth_acceleration
                        ) * archived.old_level
                    OR stat.base_growth_rate <>
                        archived.old_base_growth_rate
                    OR stat.growth_acceleration <>
                        archived.old_growth_acceleration
                    OR stat.birth_initial_savvy <>
                        archived.old_birth_initial_savvy
                    OR stat.rarity_added_savvy <>
                        archived.old_rarity_added_savvy
                    OR stat.revision <> archived.old_stat_revision + 1
                  )
                ORDER BY archived.pet_id
                LIMIT 1;

                IF invalid_pet_id IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Pet % failed scaled Added-value V3 parity validation',
                        invalid_pet_id;
                END IF;

                SELECT archived.pet_id
                INTO invalid_pet_id
                FROM public.pet_scaled_added_value_v3_archive archived
                JOIN public.character_pet_character_bonuses bonus
                  ON bonus.pet_id = archived.pet_id
                WHERE archived.migration_id =
                        '20260811_078_pet_scaled_added_value_v3'
                ORDER BY archived.pet_id
                LIMIT 1;

                IF invalid_pet_id IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Pet % retained a stale owner-Merge bonus projection',
                        invalid_pet_id;
                END IF;

                SELECT pet.id
                INTO invalid_pet_id
                FROM public.character_pets pet
                JOIN public.character_pet_stat_values stat
                  ON stat.pet_id = pet.id
                WHERE pet.initial_savvy_source_version =
                        'basic-plus-scaled-growth-v3'
                GROUP BY pet.id, pet.level, pet.completed_pet_merges
                HAVING count(*) <> 6
                    OR count(DISTINCT stat.stat_code) <> 6
                    OR pet.completed_pet_merges <> 0
                    OR count(*) FILTER (
                        WHERE stat.initial_savvy <>
                                stat.birth_initial_savvy
                           OR stat.added_savvy <>
                                (
                                    stat.base_growth_rate +
                                    stat.growth_acceleration
                                ) * pet.level
                    ) > 0
                ORDER BY pet.id
                LIMIT 1;

                IF invalid_pet_id IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Pet % has invalid scaled Added-value V3 state',
                        invalid_pet_id;
                END IF;
            END
            $validate_pet_scaled_added_value_v3$;

            ALTER TABLE public.character_pets
                VALIDATE CONSTRAINT ck_character_pets_savvy_provenance;

            ALTER TABLE public.character_pet_stat_values
                VALIDATE CONSTRAINT ck_pet_stat_savvy_birth_baseline,
                VALIDATE CONSTRAINT ck_pet_stat_savvy_progression,
                VALIDATE CONSTRAINT ck_pet_stat_added_value_progression;
            """);
}
