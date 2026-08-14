namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CorrectPetGrowthSavvySemanticsV2() => new(
            "20260810_069_pet_growth_savvy_semantics_v2",
            "Make hatch Savvy the basic value and Growth the added value without losing progression",
            """
            CREATE TABLE public.pet_growth_savvy_semantics_v2_archive (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                migration_id varchar(128) NOT NULL,
                pet_id_snapshot bigint NOT NULL,
                owner_user_id_snapshot integer NOT NULL,
                aptitude_snapshot smallint NOT NULL,
                stat_code smallint NOT NULL,
                old_initial_savvy numeric(18, 6) NOT NULL,
                old_added_savvy numeric(18, 6) NOT NULL,
                old_base_growth_rate numeric(18, 6) NOT NULL,
                old_growth_acceleration numeric(18, 6) NOT NULL,
                old_birth_initial_savvy numeric(18, 6) NOT NULL,
                old_rarity_added_savvy numeric(18, 6) NOT NULL,
                old_stat_revision bigint NOT NULL,
                old_pet_revision bigint NOT NULL,
                old_initial_savvy_baseline_total integer,
                old_initial_savvy_policy_version varchar(32),
                old_rarity_savvy_baseline_total integer NOT NULL,
                old_rarity_savvy_policy_version varchar(32) NOT NULL,
                old_initial_savvy_source_version varchar(32) NOT NULL,
                archived_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT ux_pet_growth_savvy_v2_archive
                    UNIQUE (migration_id, pet_id_snapshot, stat_code),
                CONSTRAINT ck_pet_growth_savvy_v2_archive_stat
                    CHECK (stat_code BETWEEN 1 AND 6),
                CONSTRAINT ck_pet_growth_savvy_v2_archive_revision
                    CHECK (old_stat_revision >= 0 AND old_pet_revision >= 0)
            );

            DO $preflight_pet_growth_savvy_semantics_v2$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_pets pet
                    WHERE pet.initial_savvy_source_version IS NOT NULL
                      AND pet.initial_savvy_source_version <>
                            'growth-x1-v1'
                ) THEN
                    RAISE EXCEPTION
                        'Pet growth/Savvy v2 found unsupported existing provenance';
                END IF;

                IF EXISTS (
                    SELECT pet.id
                    FROM public.character_pets pet
                    JOIN public.pet_aptitude_templates aptitude
                      ON aptitude.aptitude = pet.aptitude
                    LEFT JOIN public.character_pet_stat_values stat
                      ON stat.pet_id = pet.id
                    WHERE pet.initial_savvy_source_version =
                            'growth-x1-v1'
                    GROUP BY
                        pet.id,
                        pet.revision,
                        pet.initial_savvy_baseline_total,
                        pet.initial_savvy_policy_version,
                        pet.rarity_added_savvy_baseline_total,
                        pet.rarity_added_savvy_policy_version,
                        aptitude.minimum_total_growth,
                        aptitude.maximum_total_growth,
                        aptitude.minimum_added_savvy,
                        aptitude.maximum_added_savvy,
                        aptitude.added_savvy_policy_version
                    HAVING pet.revision < 0
                        OR pet.initial_savvy_baseline_total IS NOT NULL
                        OR pet.initial_savvy_policy_version IS NOT NULL
                        OR pet.rarity_added_savvy_baseline_total IS NULL
                        OR pet.rarity_added_savvy_policy_version IS NULL
                        OR pet.rarity_added_savvy_policy_version <>
                            aptitude.added_savvy_policy_version
                        OR pet.rarity_added_savvy_baseline_total NOT BETWEEN
                            aptitude.minimum_added_savvy AND
                            aptitude.maximum_added_savvy
                        OR count(stat.stat_code) <> 6
                        OR count(DISTINCT stat.stat_code) <> 6
                        OR count(*) FILTER (
                            WHERE stat.stat_code NOT BETWEEN 1 AND 6
                               OR stat.base_growth_rate <= 0
                               OR stat.birth_initial_savvy IS DISTINCT FROM
                                    stat.base_growth_rate
                               OR stat.rarity_added_savvy IS NULL
                               OR stat.rarity_added_savvy <= 0
                               OR stat.initial_savvy <
                                    stat.birth_initial_savvy
                               OR stat.added_savvy <
                                    stat.rarity_added_savvy
                               OR stat.growth_acceleration < 0
                               OR stat.revision < 0
                        ) > 0
                        OR sum(stat.base_growth_rate) NOT BETWEEN
                            aptitude.minimum_total_growth AND
                            aptitude.maximum_total_growth
                        OR sum(stat.rarity_added_savvy) <>
                            pet.rarity_added_savvy_baseline_total
                ) THEN
                    RAISE EXCEPTION
                        'A growth-x1-v1 pet cannot be reconciled without guessing';
                END IF;
            END
            $preflight_pet_growth_savvy_semantics_v2$;

            INSERT INTO public.pet_growth_savvy_semantics_v2_archive (
                migration_id,
                pet_id_snapshot,
                owner_user_id_snapshot,
                aptitude_snapshot,
                stat_code,
                old_initial_savvy,
                old_added_savvy,
                old_base_growth_rate,
                old_growth_acceleration,
                old_birth_initial_savvy,
                old_rarity_added_savvy,
                old_stat_revision,
                old_pet_revision,
                old_initial_savvy_baseline_total,
                old_initial_savvy_policy_version,
                old_rarity_savvy_baseline_total,
                old_rarity_savvy_policy_version,
                old_initial_savvy_source_version
            )
            SELECT
                '20260810_069_pet_growth_savvy_semantics_v2',
                pet.id,
                pet.user_id,
                pet.aptitude,
                stat.stat_code,
                stat.initial_savvy,
                stat.added_savvy,
                stat.base_growth_rate,
                stat.growth_acceleration,
                stat.birth_initial_savvy,
                stat.rarity_added_savvy,
                stat.revision,
                pet.revision,
                pet.initial_savvy_baseline_total,
                pet.initial_savvy_policy_version,
                pet.rarity_added_savvy_baseline_total,
                pet.rarity_added_savvy_policy_version,
                pet.initial_savvy_source_version
            FROM public.character_pets pet
            JOIN public.character_pet_stat_values stat
              ON stat.pet_id = pet.id
            WHERE pet.initial_savvy_source_version = 'growth-x1-v1'
            ORDER BY pet.id, stat.stat_code;

            ALTER TABLE public.character_pets
                DROP CONSTRAINT ck_character_pets_savvy_provenance;

            ALTER TABLE public.character_pet_stat_values
                DROP CONSTRAINT ck_pet_stat_growth_x1_birth_baseline,
                DROP CONSTRAINT ck_pet_stat_initial_savvy_progression,
                DROP CONSTRAINT ck_pet_stat_added_savvy_progression;

            DO $reconcile_pet_growth_savvy_stat_vectors$
            DECLARE
                expected_rows integer;
                updated_rows integer;
            BEGIN
                SELECT count(*)::integer
                INTO expected_rows
                FROM public.pet_growth_savvy_semantics_v2_archive
                WHERE migration_id =
                    '20260810_069_pet_growth_savvy_semantics_v2';

                UPDATE public.character_pet_stat_values stat
                SET initial_savvy =
                        archived.old_rarity_added_savvy +
                        (
                            archived.old_initial_savvy -
                            archived.old_birth_initial_savvy
                        ),
                    added_savvy =
                        archived.old_base_growth_rate +
                        (
                            archived.old_added_savvy -
                            archived.old_rarity_added_savvy
                        ),
                    base_growth_rate = archived.old_base_growth_rate,
                    growth_acceleration = archived.old_growth_acceleration,
                    birth_initial_savvy =
                        archived.old_rarity_added_savvy,
                    rarity_added_savvy =
                        archived.old_rarity_added_savvy,
                    revision = archived.old_stat_revision + 1
                FROM public.pet_growth_savvy_semantics_v2_archive archived
                WHERE archived.migration_id =
                        '20260810_069_pet_growth_savvy_semantics_v2'
                  AND stat.pet_id = archived.pet_id_snapshot
                  AND stat.stat_code = archived.stat_code;

                GET DIAGNOSTICS updated_rows = ROW_COUNT;
                IF updated_rows <> expected_rows THEN
                    RAISE EXCEPTION
                        'Expected to reconcile % pet stat rows, updated %',
                        expected_rows,
                        updated_rows;
                END IF;
            END
            $reconcile_pet_growth_savvy_stat_vectors$;

            DO $reconcile_pet_growth_savvy_parent_rows$
            DECLARE
                expected_rows integer;
                updated_rows integer;
            BEGIN
                SELECT count(DISTINCT pet_id_snapshot)::integer
                INTO expected_rows
                FROM public.pet_growth_savvy_semantics_v2_archive
                WHERE migration_id =
                    '20260810_069_pet_growth_savvy_semantics_v2';

                UPDATE public.character_pets pet
                SET initial_savvy_baseline_total =
                        archived.old_rarity_savvy_baseline_total,
                    initial_savvy_policy_version =
                        archived.old_rarity_savvy_policy_version,
                    initial_savvy_source_version =
                        'savvy-plus-growth-v2',
                    revision = archived.old_pet_revision + 1,
                    updated_at = transaction_timestamp()
                FROM (
                    SELECT DISTINCT
                        pet_id_snapshot,
                        old_pet_revision,
                        old_rarity_savvy_baseline_total,
                        old_rarity_savvy_policy_version
                    FROM public.pet_growth_savvy_semantics_v2_archive
                    WHERE migration_id =
                        '20260810_069_pet_growth_savvy_semantics_v2'
                ) archived
                WHERE pet.id = archived.pet_id_snapshot;

                GET DIAGNOSTICS updated_rows = ROW_COUNT;
                IF updated_rows <> expected_rows THEN
                    RAISE EXCEPTION
                        'Expected to reconcile % pet parent rows, updated %',
                        expected_rows,
                        updated_rows;
                END IF;
            END
            $reconcile_pet_growth_savvy_parent_rows$;

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
                                'savvy-plus-growth-v2'
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
                        OR added_savvy >= base_growth_rate
                    ) NOT VALID;

            DO $validate_pet_growth_savvy_semantics_v2$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_pets pet
                    WHERE pet.initial_savvy_source_version = 'growth-x1-v1'
                ) THEN
                    RAISE EXCEPTION
                        'The obsolete growth-x1-v1 provenance remains';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.pet_growth_savvy_semantics_v2_archive archived
                    JOIN public.character_pets pet
                      ON pet.id = archived.pet_id_snapshot
                    JOIN public.character_pet_stat_values stat
                      ON stat.pet_id = archived.pet_id_snapshot
                     AND stat.stat_code = archived.stat_code
                    WHERE archived.migration_id =
                            '20260810_069_pet_growth_savvy_semantics_v2'
                      AND (
                          pet.initial_savvy_source_version <>
                              'savvy-plus-growth-v2'
                          OR pet.initial_savvy_baseline_total <>
                              archived.old_rarity_savvy_baseline_total
                          OR pet.initial_savvy_policy_version <>
                              archived.old_rarity_savvy_policy_version
                          OR pet.rarity_added_savvy_baseline_total <>
                              archived.old_rarity_savvy_baseline_total
                          OR pet.revision <> archived.old_pet_revision + 1
                          OR stat.initial_savvy <>
                              archived.old_rarity_added_savvy +
                              (
                                  archived.old_initial_savvy -
                                  archived.old_birth_initial_savvy
                              )
                          OR stat.added_savvy <>
                              archived.old_base_growth_rate +
                              (
                                  archived.old_added_savvy -
                                  archived.old_rarity_added_savvy
                              )
                          OR stat.base_growth_rate <>
                              archived.old_base_growth_rate
                          OR stat.growth_acceleration <>
                              archived.old_growth_acceleration
                          OR stat.birth_initial_savvy <>
                              archived.old_rarity_added_savvy
                          OR stat.rarity_added_savvy <>
                              archived.old_rarity_added_savvy
                          OR stat.revision <> archived.old_stat_revision + 1
                      )
                ) THEN
                    RAISE EXCEPTION
                        'Pet growth/Savvy v2 reconciliation failed parity validation';
                END IF;
            END
            $validate_pet_growth_savvy_semantics_v2$;

            ALTER TABLE public.character_pets
                VALIDATE CONSTRAINT ck_character_pets_savvy_provenance;

            ALTER TABLE public.character_pet_stat_values
                VALIDATE CONSTRAINT ck_pet_stat_savvy_birth_baseline,
                VALIDATE CONSTRAINT ck_pet_stat_savvy_progression,
                VALIDATE CONSTRAINT ck_pet_stat_added_value_progression;
            """);
}
