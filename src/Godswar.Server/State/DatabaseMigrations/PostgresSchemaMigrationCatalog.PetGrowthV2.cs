namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetGrowthPolicyV2() => new(
        "20260728_018_pet_growth_policy_v2",
        "Install the v2 aptitude growth curve and reconcile out-of-range pets",
        """
        CREATE TABLE public.pet_growth_reconciliation_archive (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            migration_id varchar(128) NOT NULL,
            pet_id_snapshot bigint NOT NULL,
            owner_user_id_snapshot integer NOT NULL,
            aptitude_snapshot smallint NOT NULL
                CHECK (aptitude_snapshot BETWEEN 1 AND 16),
            stat_code smallint NOT NULL
                CHECK (stat_code BETWEEN 1 AND 6),
            old_base_growth_rate numeric(18, 6) NOT NULL,
            old_revision bigint NOT NULL CHECK (old_revision >= 0),
            archived_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CONSTRAINT ux_pet_growth_reconciliation_archive
                UNIQUE (migration_id, pet_id_snapshot, stat_code)
        );

        DO $update_pet_growth_policy_v2$
        DECLARE
            updated_aptitudes integer;
        BEGIN
            UPDATE public.pet_aptitude_templates aptitude
            SET minimum_total_growth = policy.minimum_total_growth,
                maximum_total_growth = policy.maximum_total_growth,
                growth_policy_version = 'project-v2'
            FROM (
                VALUES
                    (1::smallint, 0.01::numeric, 0.10::numeric),
                    (2::smallint, 0.10::numeric, 0.25::numeric),
                    (3::smallint, 0.25::numeric, 0.50::numeric),
                    (4::smallint, 0.50::numeric, 1.00::numeric),
                    (5::smallint, 1.00::numeric, 2.00::numeric),
                    (6::smallint, 2.00::numeric, 4.00::numeric),
                    (7::smallint, 4.00::numeric, 7.00::numeric),
                    (8::smallint, 7.00::numeric, 11.00::numeric),
                    (9::smallint, 11.00::numeric, 16.00::numeric),
                    (10::smallint, 16.00::numeric, 23.00::numeric),
                    (11::smallint, 23.00::numeric, 31.00::numeric),
                    (12::smallint, 31.00::numeric, 40.00::numeric),
                    (13::smallint, 40.00::numeric, 50.00::numeric),
                    (14::smallint, 50.00::numeric, 62.00::numeric),
                    (15::smallint, 62.00::numeric, 75.00::numeric),
                    (16::smallint, 75.00::numeric, 100.00::numeric)
            ) AS policy(
                aptitude,
                minimum_total_growth,
                maximum_total_growth)
            WHERE aptitude.aptitude = policy.aptitude;

            GET DIAGNOSTICS updated_aptitudes = ROW_COUNT;
            IF updated_aptitudes <> 16 THEN
                RAISE EXCEPTION
                    'Expected 16 pet aptitude rows, updated %',
                    updated_aptitudes;
            END IF;
        END
        $update_pet_growth_policy_v2$;

        WITH complete_out_of_range_pets AS MATERIALIZED (
            SELECT
                pet.id AS pet_id,
                pet.user_id AS owner_user_id_snapshot,
                pet.aptitude AS aptitude_snapshot,
                round(
                    (
                        aptitude.minimum_total_growth
                        + aptitude.maximum_total_growth
                    ) / 2,
                    2
                ) AS midpoint_total_growth
            FROM public.character_pets pet
            INNER JOIN public.pet_aptitude_templates aptitude
                ON aptitude.aptitude = pet.aptitude
            INNER JOIN public.character_pet_stat_values stat
                ON stat.pet_id = pet.id
            GROUP BY
                pet.id,
                pet.user_id,
                pet.aptitude,
                aptitude.minimum_total_growth,
                aptitude.maximum_total_growth
            HAVING count(*) = 6
               AND count(DISTINCT stat.stat_code) = 6
               AND (
                    sum(stat.base_growth_rate)
                        < aptitude.minimum_total_growth
                    OR sum(stat.base_growth_rate)
                        > aptitude.maximum_total_growth
               )
        ),
        midpoint_microunits AS (
            SELECT
                pet_id,
                owner_user_id_snapshot,
                aptitude_snapshot,
                (midpoint_total_growth * 1000000)::bigint
                    AS total_microunits
            FROM complete_out_of_range_pets
        ),
        deterministic_distribution AS (
            SELECT
                midpoint.pet_id,
                midpoint.owner_user_id_snapshot,
                midpoint.aptitude_snapshot,
                stat.stat_code::smallint AS stat_code,
                (
                    midpoint.total_microunits / 6
                    + CASE
                        WHEN stat.stat_code <=
                            mod(midpoint.total_microunits, 6)
                        THEN 1
                        ELSE 0
                    END
                )::numeric / 1000000 AS base_growth_rate
            FROM midpoint_microunits midpoint
            CROSS JOIN generate_series(1, 6)
                AS stat(stat_code)
        ),
        archived_before_images AS (
            INSERT INTO public.pet_growth_reconciliation_archive (
                migration_id,
                pet_id_snapshot,
                owner_user_id_snapshot,
                aptitude_snapshot,
                stat_code,
                old_base_growth_rate,
                old_revision,
                archived_at
            )
            SELECT
                '20260728_018_pet_growth_policy_v2',
                existing.pet_id,
                distribution.owner_user_id_snapshot,
                distribution.aptitude_snapshot,
                existing.stat_code,
                existing.base_growth_rate,
                existing.revision,
                clock_timestamp()
            FROM public.character_pet_stat_values existing
            INNER JOIN deterministic_distribution distribution
                ON distribution.pet_id = existing.pet_id
               AND distribution.stat_code = existing.stat_code
            RETURNING pet_id_snapshot, stat_code
        )
        UPDATE public.character_pet_stat_values existing
        SET base_growth_rate =
                distribution.base_growth_rate,
            revision = existing.revision + 1
        FROM deterministic_distribution distribution
        INNER JOIN archived_before_images archived
            ON archived.pet_id_snapshot = distribution.pet_id
           AND archived.stat_code = distribution.stat_code
        WHERE existing.pet_id = distribution.pet_id
          AND existing.stat_code = distribution.stat_code;

        DO $validate_pet_growth_policy_v2$
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
                    aptitude.minimum_total_growth,
                    aptitude.maximum_total_growth
                HAVING count(stat.stat_code) <> 6
                    OR count(DISTINCT stat.stat_code) <> 6
                    OR count(stat.stat_code) FILTER (
                        WHERE stat.base_growth_rate > 0
                    ) <> 6
                    OR coalesce(sum(stat.base_growth_rate), 0)
                        < aptitude.minimum_total_growth
                    OR coalesce(sum(stat.base_growth_rate), 0)
                        > aptitude.maximum_total_growth
            ) THEN
                RAISE EXCEPTION
                    'Pet growth v2 reconciliation left invalid pet state';
            END IF;
        END
        $validate_pet_growth_policy_v2$;
        """);
}
