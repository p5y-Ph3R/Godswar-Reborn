namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetInitialSavvyPolicy() => new(
            "20260728_019_pet_initial_savvy_policy",
            "Persist aptitude-based initial-savvy brackets and reconcile zero-savvy pets",
            """
            CREATE TABLE public.pet_initial_savvy_reconciliation_archive (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                migration_id varchar(128) NOT NULL,
                pet_id_snapshot bigint NOT NULL,
                owner_user_id_snapshot integer NOT NULL,
                aptitude_snapshot smallint NOT NULL
                    CHECK (aptitude_snapshot BETWEEN 1 AND 16),
                stat_code smallint NOT NULL
                    CHECK (stat_code BETWEEN 1 AND 6),
                old_initial_savvy numeric(18, 6) NOT NULL,
                old_revision bigint NOT NULL CHECK (old_revision >= 0),
                old_pet_revision bigint NOT NULL
                    CHECK (old_pet_revision >= 0),
                old_initial_savvy_baseline_total integer,
                old_initial_savvy_policy_version varchar(32),
                archived_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT ux_pet_initial_savvy_reconciliation_archive
                    UNIQUE (migration_id, pet_id_snapshot, stat_code)
            );

            ALTER TABLE public.pet_aptitude_templates
                ADD COLUMN minimum_initial_savvy integer,
                ADD COLUMN maximum_initial_savvy integer,
                ADD COLUMN maximum_initial_savvy_stat_deviation
                    numeric(5, 4),
                ADD COLUMN initial_savvy_policy_version varchar(32);

            DO $install_pet_initial_savvy_policy$
            DECLARE
                updated_aptitudes integer;
            BEGIN
                UPDATE public.pet_aptitude_templates aptitude
                SET minimum_initial_savvy = policy.minimum_initial_savvy,
                    maximum_initial_savvy = policy.maximum_initial_savvy,
                    maximum_initial_savvy_stat_deviation = 0.1200,
                    initial_savvy_policy_version = 'project-v1'
                FROM (
                    VALUES
                        (1::smallint, 250, 349),
                        (2::smallint, 350, 449),
                        (3::smallint, 450, 574),
                        (4::smallint, 575, 699),
                        (5::smallint, 700, 849),
                        (6::smallint, 850, 1024),
                        (7::smallint, 1025, 1224),
                        (8::smallint, 1225, 1474),
                        (9::smallint, 1475, 1774),
                        (10::smallint, 1775, 2124),
                        (11::smallint, 2125, 2524),
                        (12::smallint, 2525, 2974),
                        (13::smallint, 2975, 3474),
                        (14::smallint, 3475, 4024),
                        (15::smallint, 4025, 4624),
                        (16::smallint, 4625, 5324)
                ) AS policy(
                    aptitude,
                    minimum_initial_savvy,
                    maximum_initial_savvy)
                WHERE aptitude.aptitude = policy.aptitude;

                GET DIAGNOSTICS updated_aptitudes = ROW_COUNT;
                IF updated_aptitudes <> 16 THEN
                    RAISE EXCEPTION
                        'Expected 16 pet aptitude rows, updated %',
                        updated_aptitudes;
                END IF;
            END
            $install_pet_initial_savvy_policy$;

            ALTER TABLE public.pet_aptitude_templates
                ALTER COLUMN minimum_initial_savvy SET NOT NULL,
                ALTER COLUMN maximum_initial_savvy SET NOT NULL,
                ALTER COLUMN maximum_initial_savvy_stat_deviation
                    SET NOT NULL,
                ALTER COLUMN initial_savvy_policy_version SET NOT NULL;

            ALTER TABLE public.pet_aptitude_templates
                ADD CONSTRAINT ck_pet_aptitude_initial_savvy_bracket
                CHECK (
                    minimum_initial_savvy > 0
                    AND maximum_initial_savvy
                        >= minimum_initial_savvy
                ) NOT VALID,
                ADD CONSTRAINT
                    ck_pet_aptitude_initial_savvy_deviation
                CHECK (
                    maximum_initial_savvy_stat_deviation > 0
                    AND maximum_initial_savvy_stat_deviation <= 0.2500
                ) NOT VALID,
                ADD CONSTRAINT
                    ck_pet_aptitude_initial_savvy_policy_version
                CHECK (
                    btrim(initial_savvy_policy_version) <> ''
                ) NOT VALID;

            ALTER TABLE public.pet_aptitude_templates
                VALIDATE CONSTRAINT
                    ck_pet_aptitude_initial_savvy_bracket,
                VALIDATE CONSTRAINT
                    ck_pet_aptitude_initial_savvy_deviation,
                VALIDATE CONSTRAINT
                    ck_pet_aptitude_initial_savvy_policy_version;

            ALTER TABLE public.character_pets
                ADD COLUMN initial_savvy_baseline_total integer,
                ADD COLUMN initial_savvy_policy_version varchar(32);

            ALTER TABLE public.character_pets
                ADD CONSTRAINT
                    ck_character_pets_initial_savvy_baseline
                CHECK (
                    initial_savvy_baseline_total IS NULL
                    OR initial_savvy_baseline_total > 0
                ) NOT VALID,
                ADD CONSTRAINT
                    ck_character_pets_initial_savvy_provenance
                CHECK (
                    (
                        initial_savvy_baseline_total IS NULL
                        AND initial_savvy_policy_version IS NULL
                    )
                    OR (
                        initial_savvy_baseline_total IS NOT NULL
                        AND btrim(initial_savvy_policy_version) <> ''
                    )
                ) NOT VALID;

            ALTER TABLE public.character_pets
                VALIDATE CONSTRAINT
                    ck_character_pets_initial_savvy_baseline,
                VALIDATE CONSTRAINT
                    ck_character_pets_initial_savvy_provenance;

            WITH complete_zero_savvy_pets AS MATERIALIZED (
                SELECT
                    pet.id AS pet_id,
                    pet.user_id AS owner_user_id_snapshot,
                    pet.aptitude AS aptitude_snapshot,
                    pet.revision AS old_pet_revision,
                    pet.initial_savvy_baseline_total
                        AS old_initial_savvy_baseline_total,
                    pet.initial_savvy_policy_version
                        AS old_initial_savvy_policy_version,
                    round(
                        (
                            aptitude.minimum_initial_savvy
                            + aptitude.maximum_initial_savvy
                        ) / 2.0
                    )::integer AS midpoint_total_savvy
                FROM public.character_pets pet
                INNER JOIN public.pet_aptitude_templates aptitude
                    ON aptitude.aptitude = pet.aptitude
                INNER JOIN public.character_pet_stat_values stat
                    ON stat.pet_id = pet.id
                GROUP BY
                    pet.id,
                    pet.user_id,
                    pet.aptitude,
                    pet.revision,
                    pet.initial_savvy_baseline_total,
                    pet.initial_savvy_policy_version,
                    aptitude.minimum_initial_savvy,
                    aptitude.maximum_initial_savvy
                HAVING count(*) = 6
                   AND count(DISTINCT stat.stat_code) = 6
                   AND count(*) FILTER (
                        WHERE stat.initial_savvy = 0
                   ) = 6
            ),
            midpoint_centipoints AS (
                SELECT
                    pet_id,
                    owner_user_id_snapshot,
                    aptitude_snapshot,
                    old_pet_revision,
                    old_initial_savvy_baseline_total,
                    old_initial_savvy_policy_version,
                    midpoint_total_savvy * 100::bigint
                        AS total_centipoints
                FROM complete_zero_savvy_pets
            ),
            deterministic_distribution AS (
                SELECT
                    midpoint.pet_id,
                    midpoint.owner_user_id_snapshot,
                    midpoint.aptitude_snapshot,
                    midpoint.old_pet_revision,
                    midpoint.old_initial_savvy_baseline_total,
                    midpoint.old_initial_savvy_policy_version,
                    stat.stat_code::smallint AS stat_code,
                    (
                        midpoint.total_centipoints / 6
                        + CASE
                            WHEN stat.stat_code <=
                                mod(midpoint.total_centipoints, 6)
                            THEN 1
                            ELSE 0
                        END
                    )::numeric / 100 AS initial_savvy
                FROM midpoint_centipoints midpoint
                CROSS JOIN generate_series(1, 6)
                    AS stat(stat_code)
            ),
            archived_before_images AS (
                INSERT INTO
                    public.pet_initial_savvy_reconciliation_archive (
                    migration_id,
                    pet_id_snapshot,
                    owner_user_id_snapshot,
                    aptitude_snapshot,
                    stat_code,
                    old_initial_savvy,
                    old_revision,
                    old_pet_revision,
                    old_initial_savvy_baseline_total,
                    old_initial_savvy_policy_version,
                    archived_at
                )
                SELECT
                    '20260728_019_pet_initial_savvy_policy',
                    existing.pet_id,
                    distribution.owner_user_id_snapshot,
                    distribution.aptitude_snapshot,
                    existing.stat_code,
                    existing.initial_savvy,
                    existing.revision,
                    distribution.old_pet_revision,
                    distribution.old_initial_savvy_baseline_total,
                    distribution.old_initial_savvy_policy_version,
                    clock_timestamp()
                FROM public.character_pet_stat_values existing
                INNER JOIN deterministic_distribution distribution
                    ON distribution.pet_id = existing.pet_id
                   AND distribution.stat_code = existing.stat_code
                RETURNING pet_id_snapshot, stat_code
            ),
            updated_stats AS (
                UPDATE public.character_pet_stat_values existing
                SET initial_savvy = distribution.initial_savvy,
                    revision = existing.revision + 1
                FROM deterministic_distribution distribution
                INNER JOIN archived_before_images archived
                    ON archived.pet_id_snapshot =
                        distribution.pet_id
                   AND archived.stat_code =
                        distribution.stat_code
                WHERE existing.pet_id = distribution.pet_id
                  AND existing.stat_code =
                        distribution.stat_code
                RETURNING existing.pet_id, existing.stat_code
            ),
            completely_updated_pets AS (
                SELECT pet_id
                FROM updated_stats
                GROUP BY pet_id
                HAVING count(*) = 6
                   AND count(DISTINCT stat_code) = 6
            )
            UPDATE public.character_pets pet
            SET initial_savvy_baseline_total =
                    zero_pet.midpoint_total_savvy,
                initial_savvy_policy_version = 'project-v1',
                revision = pet.revision + 1,
                updated_at = clock_timestamp()
            FROM complete_zero_savvy_pets zero_pet
            INNER JOIN completely_updated_pets updated
                ON updated.pet_id = zero_pet.pet_id
            WHERE pet.id = zero_pet.pet_id;

            ALTER TABLE public.character_pet_stat_values
                ALTER COLUMN initial_savvy DROP DEFAULT;

            DO $validate_pet_initial_savvy_policy$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_pets pet
                    INNER JOIN public.pet_aptitude_templates aptitude
                        ON aptitude.aptitude = pet.aptitude
                    LEFT JOIN public.character_pet_stat_values stat
                        ON stat.pet_id = pet.id
                    GROUP BY
                        pet.id,
                        pet.initial_savvy_baseline_total,
                        pet.initial_savvy_policy_version,
                        aptitude.minimum_initial_savvy,
                        aptitude.maximum_initial_savvy
                    HAVING count(stat.stat_code) <> 6
                        OR count(DISTINCT stat.stat_code) <> 6
                        OR (
                            pet.initial_savvy_baseline_total IS NULL
                            AND COALESCE(
                                sum(stat.initial_savvy),
                                0
                            ) = 0
                        )
                        OR (
                            pet.initial_savvy_baseline_total
                                IS NOT NULL
                            AND (
                                pet.initial_savvy_policy_version
                                    <> 'project-v1'
                                OR pet.initial_savvy_baseline_total
                                    < aptitude.minimum_initial_savvy
                                OR pet.initial_savvy_baseline_total
                                    > aptitude.maximum_initial_savvy
                                OR count(*) FILTER (
                                    WHERE stat.initial_savvy <= 0
                                ) > 0
                                OR COALESCE(
                                    sum(stat.initial_savvy),
                                    0
                                ) <
                                    pet.initial_savvy_baseline_total
                            )
                        )
                ) THEN
                    RAISE EXCEPTION
                        'Pet initial-savvy reconciliation did not produce a complete in-bracket state';
                END IF;
            END
            $validate_pet_initial_savvy_policy$;
            """);
}
