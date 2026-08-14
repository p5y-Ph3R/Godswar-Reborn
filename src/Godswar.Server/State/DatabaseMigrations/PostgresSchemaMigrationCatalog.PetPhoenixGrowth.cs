namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetPhoenixGrowthActivation() => new(
            "20260811_071_pet_phoenix_growth_activation",
            "Keep unrevealed pet Growth Weak until a Phoenix Feather reset",
            """
            ALTER TABLE public.character_pets
                ADD COLUMN growth_activation_policy_version varchar(48)
                    NOT NULL DEFAULT 'weak-until-phoenix-v1';

            ALTER TABLE public.character_pets
                ADD CONSTRAINT ck_character_pets_growth_activation_policy
                CHECK (btrim(growth_activation_policy_version) <> '');

            CREATE TABLE public.pet_phoenix_growth_activation_archive (
                migration_id varchar(96) NOT NULL,
                pet_id bigint NOT NULL,
                stat_code smallint NOT NULL,
                old_added_savvy numeric(18, 6) NOT NULL,
                old_base_growth_rate numeric(18, 6) NOT NULL,
                old_stat_revision bigint NOT NULL,
                old_pet_revision bigint NOT NULL,
                old_growth_revealed boolean NOT NULL,
                archived_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
                PRIMARY KEY (migration_id, pet_id, stat_code),
                CONSTRAINT ck_pet_phoenix_growth_archive_stat
                    CHECK (stat_code BETWEEN 1 AND 6),
                CONSTRAINT ck_pet_phoenix_growth_archive_revision
                    CHECK (old_stat_revision >= 0 AND old_pet_revision >= 0)
            );

            DO $preflight_pet_phoenix_growth_activation$
            DECLARE
                invalid_pet_id bigint;
            BEGIN
                SELECT pet.id
                INTO invalid_pet_id
                FROM public.character_pets pet
                LEFT JOIN public.character_pet_stat_values stat
                    ON stat.pet_id = pet.id
                WHERE NOT pet.growth_revealed
                GROUP BY pet.id, pet.initial_savvy_source_version
                HAVING count(stat.stat_code) <> 6
                    OR count(DISTINCT stat.stat_code) <> 6
                    OR pet.initial_savvy_source_version IS DISTINCT FROM
                        'savvy-plus-growth-v2'
                    OR count(*) FILTER (
                        WHERE stat.stat_code NOT BETWEEN 1 AND 6
                           OR stat.base_growth_rate <= 0
                           OR stat.added_savvy < stat.base_growth_rate
                           OR stat.revision < 0
                    ) > 0
                ORDER BY pet.id
                LIMIT 1;

                IF invalid_pet_id IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Pet % cannot enter weak-until-Phoenix Growth policy',
                        invalid_pet_id;
                END IF;
            END
            $preflight_pet_phoenix_growth_activation$;

            WITH candidates AS (
                SELECT pet.id
                FROM public.character_pets pet
                JOIN public.character_pet_stat_values stat
                  ON stat.pet_id = pet.id
                WHERE NOT pet.growth_revealed
                GROUP BY pet.id
                HAVING sum(stat.base_growth_rate) NOT BETWEEN 0.01 AND 0.10
            )
            INSERT INTO public.pet_phoenix_growth_activation_archive (
                migration_id,
                pet_id,
                stat_code,
                old_added_savvy,
                old_base_growth_rate,
                old_stat_revision,
                old_pet_revision,
                old_growth_revealed
            )
            SELECT
                '20260811_071_pet_phoenix_growth_activation',
                pet.id,
                stat.stat_code,
                stat.added_savvy,
                stat.base_growth_rate,
                stat.revision,
                pet.revision,
                pet.growth_revealed
            FROM public.character_pets pet
            JOIN candidates ON candidates.id = pet.id
            JOIN public.character_pet_stat_values stat
              ON stat.pet_id = pet.id
            WHERE NOT pet.growth_revealed
            ON CONFLICT (migration_id, pet_id, stat_code) DO NOTHING;

            WITH target(stat_code, growth_rate) AS (
                VALUES
                    (1::smallint, 0.010000::numeric(18, 6)),
                    (2::smallint, 0.010000::numeric(18, 6)),
                    (3::smallint, 0.010000::numeric(18, 6)),
                    (4::smallint, 0.010000::numeric(18, 6)),
                    (5::smallint, 0.010000::numeric(18, 6)),
                    (6::smallint, 0.010000::numeric(18, 6))
            )
            UPDATE public.character_pet_stat_values stat
            SET added_savvy =
                    target.growth_rate +
                    (archive.old_added_savvy -
                     archive.old_base_growth_rate),
                base_growth_rate = target.growth_rate,
                revision = archive.old_stat_revision + 1
            FROM public.pet_phoenix_growth_activation_archive archive
            JOIN target ON target.stat_code = archive.stat_code
            WHERE archive.migration_id =
                    '20260811_071_pet_phoenix_growth_activation'
              AND stat.pet_id = archive.pet_id
              AND stat.stat_code = archive.stat_code
              AND stat.added_savvy = archive.old_added_savvy
              AND stat.base_growth_rate = archive.old_base_growth_rate
              AND stat.revision = archive.old_stat_revision;

            WITH affected AS (
                SELECT pet_id, min(old_pet_revision) AS old_pet_revision
                FROM public.pet_phoenix_growth_activation_archive
                WHERE migration_id =
                    '20260811_071_pet_phoenix_growth_activation'
                GROUP BY pet_id
            )
            UPDATE public.character_pets pet
            SET revision = affected.old_pet_revision + 1,
                growth_activation_policy_version =
                    'weak-until-phoenix-v1',
                updated_at = transaction_timestamp()
            FROM affected
            WHERE pet.id = affected.pet_id
              AND NOT pet.growth_revealed
              AND pet.revision = affected.old_pet_revision;

            DO $validate_pet_phoenix_growth_activation$
            DECLARE
                invalid_pet_id bigint;
            BEGIN
                WITH target(stat_code, growth_rate) AS (
                    VALUES
                        (1::smallint, 0.010000::numeric(18, 6)),
                        (2::smallint, 0.010000::numeric(18, 6)),
                        (3::smallint, 0.010000::numeric(18, 6)),
                        (4::smallint, 0.010000::numeric(18, 6)),
                        (5::smallint, 0.010000::numeric(18, 6)),
                        (6::smallint, 0.010000::numeric(18, 6))
                )
                SELECT archive.pet_id
                INTO invalid_pet_id
                FROM public.pet_phoenix_growth_activation_archive archive
                JOIN target ON target.stat_code = archive.stat_code
                JOIN public.character_pets pet ON pet.id = archive.pet_id
                JOIN public.character_pet_stat_values stat
                  ON stat.pet_id = archive.pet_id
                 AND stat.stat_code = archive.stat_code
                WHERE archive.migration_id =
                        '20260811_071_pet_phoenix_growth_activation'
                  AND (
                    archive.old_growth_revealed
                    OR pet.growth_revealed
                    OR pet.growth_activation_policy_version <>
                        'weak-until-phoenix-v1'
                    OR pet.revision <> archive.old_pet_revision + 1
                    OR stat.revision <> archive.old_stat_revision + 1
                    OR stat.base_growth_rate <> target.growth_rate
                    OR stat.added_savvy <>
                        target.growth_rate +
                        (archive.old_added_savvy -
                         archive.old_base_growth_rate)
                  )
                ORDER BY archive.pet_id
                LIMIT 1;

                IF invalid_pet_id IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Pet % failed weak-until-Phoenix reconciliation',
                        invalid_pet_id;
                END IF;

                SELECT pet.id
                INTO invalid_pet_id
                FROM public.character_pets pet
                JOIN public.character_pet_stat_values stat
                  ON stat.pet_id = pet.id
                WHERE NOT pet.growth_revealed
                GROUP BY pet.id
                HAVING count(*) <> 6
                    OR sum(stat.base_growth_rate) NOT BETWEEN 0.01 AND 0.10
                ORDER BY pet.id
                LIMIT 1;

                IF invalid_pet_id IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Pet % has invalid unrevealed Weak Growth total',
                        invalid_pet_id;
                END IF;
            END
            $validate_pet_phoenix_growth_activation$;
            """);
}
