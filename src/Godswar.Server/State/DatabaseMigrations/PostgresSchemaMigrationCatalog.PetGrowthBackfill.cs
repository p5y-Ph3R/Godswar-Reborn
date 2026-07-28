namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetGrowthMidpointBackfill() => new(
            "20260728_017_pet_growth_midpoint_backfill",
            "Backfill missing legacy pet growth at each aptitude midpoint",
            """
            WITH eligible_pets AS MATERIALIZED (
                SELECT
                    pet.id AS pet_id,
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
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM public.character_pet_stat_values existing
                    WHERE existing.pet_id = pet.id
                      AND existing.base_growth_rate <> 0
                )
            ),
            midpoint_hundredths AS (
                SELECT
                    pet_id,
                    (midpoint_total_growth * 100)::bigint
                        AS total_hundredths
                FROM eligible_pets
            ),
            deterministic_distribution AS (
                SELECT
                    midpoint.pet_id,
                    stat.stat_code::smallint AS stat_code,
                    (
                        midpoint.total_hundredths / 6
                        + CASE
                            WHEN stat.stat_code <=
                                mod(midpoint.total_hundredths, 6)
                            THEN 1
                            ELSE 0
                        END
                    )::numeric / 100 AS base_growth_rate
                FROM midpoint_hundredths midpoint
                CROSS JOIN generate_series(1, 6)
                    AS stat(stat_code)
            )
            INSERT INTO public.character_pet_stat_values (
                pet_id,
                stat_code,
                base_growth_rate
            )
            SELECT
                pet_id,
                stat_code,
                base_growth_rate
            FROM deterministic_distribution
            ON CONFLICT (pet_id, stat_code) DO UPDATE
            SET base_growth_rate = EXCLUDED.base_growth_rate,
                revision =
                    character_pet_stat_values.revision + 1
            WHERE character_pet_stat_values.base_growth_rate = 0;

            ALTER TABLE public.character_pet_stat_values
                ALTER COLUMN base_growth_rate DROP DEFAULT;
            """);
}
