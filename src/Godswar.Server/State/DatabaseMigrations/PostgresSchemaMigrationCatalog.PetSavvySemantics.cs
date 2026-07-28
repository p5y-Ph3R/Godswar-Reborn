namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetSavvySemanticsCorrection() => new(
            "20260729_020_pet_savvy_semantics",
            "Move aptitude rarity allocation to added savvy and derive basic savvy from growth",
            """
            CREATE TABLE public.pet_savvy_semantics_reconciliation_archive (
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
                old_stat_revision bigint NOT NULL,
                old_pet_revision bigint NOT NULL,
                old_initial_savvy_baseline_total integer,
                old_initial_savvy_policy_version varchar(32),
                archived_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT ux_pet_savvy_semantics_archive
                    UNIQUE (migration_id, pet_id_snapshot, stat_code)
            );

            ALTER TABLE public.pet_aptitude_templates
                ADD COLUMN minimum_added_savvy integer,
                ADD COLUMN maximum_added_savvy integer,
                ADD COLUMN added_savvy_policy_version varchar(32);

            DO $install_pet_added_savvy_policy$
            DECLARE
                updated_aptitudes integer;
            BEGIN
                UPDATE public.pet_aptitude_templates aptitude
                SET minimum_added_savvy = policy.minimum_added_savvy,
                    maximum_added_savvy = policy.maximum_added_savvy,
                    added_savvy_policy_version = 'project-v2'
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
                    minimum_added_savvy,
                    maximum_added_savvy)
                WHERE aptitude.aptitude = policy.aptitude;

                GET DIAGNOSTICS updated_aptitudes = ROW_COUNT;
                IF updated_aptitudes <> 16 THEN
                    RAISE EXCEPTION
                        'Expected 16 pet aptitude rows, updated %',
                        updated_aptitudes;
                END IF;
            END
            $install_pet_added_savvy_policy$;

            ALTER TABLE public.pet_aptitude_templates
                ALTER COLUMN minimum_added_savvy SET NOT NULL,
                ALTER COLUMN maximum_added_savvy SET NOT NULL,
                ALTER COLUMN added_savvy_policy_version SET NOT NULL,
                ADD CONSTRAINT ck_pet_aptitude_added_savvy_bracket
                    CHECK (
                        minimum_added_savvy > 0
                        AND maximum_added_savvy >= minimum_added_savvy
                    ) NOT VALID,
                ADD CONSTRAINT ck_pet_aptitude_added_savvy_policy
                    CHECK (btrim(added_savvy_policy_version) <> '')
                    NOT VALID;

            ALTER TABLE public.pet_aptitude_templates
                VALIDATE CONSTRAINT
                    ck_pet_aptitude_added_savvy_bracket,
                VALIDATE CONSTRAINT
                    ck_pet_aptitude_added_savvy_policy;

            ALTER TABLE public.character_pets
                ADD COLUMN rarity_added_savvy_baseline_total integer,
                ADD COLUMN rarity_added_savvy_policy_version varchar(32),
                ADD COLUMN initial_savvy_source_version varchar(32);

            ALTER TABLE public.character_pets
                ADD CONSTRAINT ck_character_pets_rarity_savvy_baseline
                    CHECK (
                        rarity_added_savvy_baseline_total IS NULL
                        OR rarity_added_savvy_baseline_total > 0
                    ) NOT VALID,
                ADD CONSTRAINT ck_character_pets_savvy_provenance
                    CHECK (
                        (
                            rarity_added_savvy_baseline_total IS NULL
                            AND rarity_added_savvy_policy_version IS NULL
                            AND initial_savvy_source_version IS NULL
                        )
                        OR (
                            rarity_added_savvy_baseline_total IS NOT NULL
                            AND btrim(
                                rarity_added_savvy_policy_version
                            ) <> ''
                            AND btrim(initial_savvy_source_version) <> ''
                        )
                    ) NOT VALID;

            ALTER TABLE public.character_pets
                VALIDATE CONSTRAINT
                    ck_character_pets_rarity_savvy_baseline,
                VALIDATE CONSTRAINT
                    ck_character_pets_savvy_provenance;

            ALTER TABLE public.character_pet_stat_values
                ADD COLUMN birth_initial_savvy numeric(18, 6),
                ADD COLUMN rarity_added_savvy numeric(18, 6),
                ADD CONSTRAINT ck_pet_stat_birth_initial_savvy
                    CHECK (
                        birth_initial_savvy IS NULL
                        OR birth_initial_savvy > 0
                    ) NOT VALID,
                ADD CONSTRAINT ck_pet_stat_rarity_added_savvy
                    CHECK (
                        rarity_added_savvy IS NULL
                        OR rarity_added_savvy > 0
                    ) NOT VALID,
                ADD CONSTRAINT ck_pet_stat_savvy_baseline_pair
                    CHECK (
                        (
                            birth_initial_savvy IS NULL
                            AND rarity_added_savvy IS NULL
                        )
                        OR (
                            birth_initial_savvy IS NOT NULL
                            AND rarity_added_savvy IS NOT NULL
                        )
                    ) NOT VALID;

            ALTER TABLE public.character_pet_stat_values
                VALIDATE CONSTRAINT ck_pet_stat_birth_initial_savvy,
                VALIDATE CONSTRAINT ck_pet_stat_rarity_added_savvy,
                VALIDATE CONSTRAINT ck_pet_stat_savvy_baseline_pair;

            DO $guard_pet_savvy_semantics_reconciliation$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_pets pet
                    LEFT JOIN public.character_pet_stat_values stat
                        ON stat.pet_id = pet.id
                    WHERE pet.initial_savvy_policy_version = 'project-v1'
                    GROUP BY
                        pet.id,
                        pet.initial_savvy_baseline_total
                    HAVING
                        pet.initial_savvy_baseline_total IS NULL
                        OR count(stat.stat_code) <> 6
                        OR count(DISTINCT stat.stat_code) <> 6
                        OR count(*) FILTER (
                            WHERE stat.added_savvy <> 0
                        ) <> 0
                        OR count(*) FILTER (
                            WHERE stat.base_growth_rate <= 0
                        ) <> 0
                        OR COALESCE(sum(stat.initial_savvy), 0)
                            <> pet.initial_savvy_baseline_total
                ) THEN
                    RAISE EXCEPTION
                        'A project-v1 pet has progressed or incomplete savvy data; manual reconciliation is required';
                END IF;
            END
            $guard_pet_savvy_semantics_reconciliation$;

            WITH eligible_pets AS MATERIALIZED (
                SELECT
                    pet.id AS pet_id,
                    pet.user_id AS owner_user_id,
                    pet.aptitude,
                    pet.revision AS old_pet_revision,
                    pet.initial_savvy_baseline_total AS total_savvy,
                    pet.initial_savvy_policy_version AS old_policy_version
                FROM public.character_pets pet
                WHERE pet.initial_savvy_policy_version = 'project-v1'
                  AND pet.initial_savvy_baseline_total IS NOT NULL
            ),
            weighted_stats AS (
                SELECT
                    target.*,
                    stat.stat_code,
                    stat.initial_savvy AS old_initial_savvy,
                    stat.added_savvy AS old_added_savvy,
                    stat.base_growth_rate,
                    stat.growth_acceleration,
                    stat.revision AS old_stat_revision,
                    (
                        ARRAY[80, 88, 96, 104, 112, 120]
                    )[
                        row_number() OVER (
                            PARTITION BY target.pet_id
                            ORDER BY md5(
                                '20260729_020_pet_savvy_semantics:'
                                || target.pet_id::text
                                || ':'
                                || stat.stat_code::text
                            )
                        )
                    ]::bigint AS weight
                FROM eligible_pets target
                INNER JOIN public.character_pet_stat_values stat
                    ON stat.pet_id = target.pet_id
            ),
            floor_allocations AS (
                SELECT
                    weighted.*,
                    (
                        weighted.total_savvy::bigint
                        * 100
                        * weighted.weight
                    ) / 600 AS floor_units,
                    (
                        weighted.total_savvy::bigint
                        * 100
                        * weighted.weight
                    ) % 600 AS remainder
                FROM weighted_stats weighted
            ),
            ranked_allocations AS (
                SELECT
                    allocation.*,
                    allocation.total_savvy::bigint * 100
                        - sum(allocation.floor_units) OVER (
                            PARTITION BY allocation.pet_id
                        ) AS unallocated_units,
                    row_number() OVER (
                        PARTITION BY allocation.pet_id
                        ORDER BY
                            allocation.remainder DESC,
                            allocation.stat_code
                    ) AS remainder_rank
                FROM floor_allocations allocation
            ),
            final_allocations AS (
                SELECT
                    ranked.*,
                    ranked.floor_units
                        + CASE
                            WHEN ranked.remainder_rank
                                <= ranked.unallocated_units
                            THEN 1
                            ELSE 0
                        END AS allocated_units
                FROM ranked_allocations ranked
            ),
            archived_before_images AS (
                INSERT INTO
                    public.pet_savvy_semantics_reconciliation_archive (
                    migration_id,
                    pet_id_snapshot,
                    owner_user_id_snapshot,
                    aptitude_snapshot,
                    stat_code,
                    old_initial_savvy,
                    old_added_savvy,
                    old_base_growth_rate,
                    old_growth_acceleration,
                    old_stat_revision,
                    old_pet_revision,
                    old_initial_savvy_baseline_total,
                    old_initial_savvy_policy_version,
                    archived_at
                )
                SELECT
                    '20260729_020_pet_savvy_semantics',
                    allocation.pet_id,
                    allocation.owner_user_id,
                    allocation.aptitude,
                    allocation.stat_code,
                    allocation.old_initial_savvy,
                    allocation.old_added_savvy,
                    allocation.base_growth_rate,
                    allocation.growth_acceleration,
                    allocation.old_stat_revision,
                    allocation.old_pet_revision,
                    allocation.total_savvy,
                    allocation.old_policy_version,
                    clock_timestamp()
                FROM final_allocations allocation
                RETURNING pet_id_snapshot, stat_code
            ),
            updated_stats AS (
                UPDATE public.character_pet_stat_values stat
                SET birth_initial_savvy =
                        allocation.base_growth_rate,
                    rarity_added_savvy =
                        allocation.allocated_units::numeric / 100,
                    initial_savvy =
                        allocation.base_growth_rate,
                    added_savvy =
                        allocation.allocated_units::numeric / 100,
                    revision = stat.revision + 1
                FROM final_allocations allocation
                INNER JOIN archived_before_images archived
                    ON archived.pet_id_snapshot = allocation.pet_id
                   AND archived.stat_code = allocation.stat_code
                WHERE stat.pet_id = allocation.pet_id
                  AND stat.stat_code = allocation.stat_code
                RETURNING stat.pet_id, stat.stat_code
            ),
            completely_updated_pets AS (
                SELECT pet_id
                FROM updated_stats
                GROUP BY pet_id
                HAVING count(*) = 6
                   AND count(DISTINCT stat_code) = 6
            )
            UPDATE public.character_pets pet
            SET rarity_added_savvy_baseline_total =
                    pet.initial_savvy_baseline_total,
                rarity_added_savvy_policy_version = 'project-v2',
                initial_savvy_source_version = 'growth-x1-v1',
                initial_savvy_baseline_total = NULL,
                initial_savvy_policy_version = NULL,
                revision = pet.revision + 1,
                updated_at = clock_timestamp()
            FROM completely_updated_pets updated
            WHERE pet.id = updated.pet_id;
            """ + "\n" + PetSavvySemanticsItemAndValidationSql);
}
