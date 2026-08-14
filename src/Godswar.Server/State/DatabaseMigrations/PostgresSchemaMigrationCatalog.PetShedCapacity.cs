namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetShedCapacity() =>
        new(
            "20260810_065_pet_shed_capacity",
            "Persist independently unlocked character pet-shed cells",
            """
            ALTER TABLE public.character_base
                ADD COLUMN pet_shed_capacity smallint,
                ADD COLUMN pet_shed_revision bigint NOT NULL DEFAULT 0;

            WITH existing_pet_counts AS (
                SELECT user_id, count(*)::integer AS pet_count
                FROM public.character_pets
                GROUP BY user_id
            )
            UPDATE public.character_base character
            SET pet_shed_capacity = CASE
                    WHEN COALESCE(counts.pet_count, 0) <= 2 THEN 2
                    WHEN counts.pet_count <= 4 THEN 4
                    ELSE 8
                END
            FROM existing_pet_counts counts
            WHERE counts.user_id = character.id;

            UPDATE public.character_base
            SET pet_shed_capacity = 2
            WHERE pet_shed_capacity IS NULL;

            ALTER TABLE public.character_base
                ALTER COLUMN pet_shed_capacity SET DEFAULT 2,
                ALTER COLUMN pet_shed_capacity SET NOT NULL,
                ADD CONSTRAINT ck_character_base_pet_shed_capacity
                    CHECK (pet_shed_capacity BETWEEN 2 AND 8),
                ADD CONSTRAINT ck_character_base_pet_shed_revision
                    CHECK (pet_shed_revision >= 0);

            DO $pet_shed_existing_count_guard$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_base character
                    JOIN LATERAL (
                        SELECT count(*)::integer AS pet_count
                        FROM public.character_pets pet
                        WHERE pet.user_id = character.id
                    ) counts ON true
                    WHERE counts.pet_count > character.pet_shed_capacity
                ) THEN
                    RAISE EXCEPTION
                        'Existing pet rows exceed the migrated pet-shed capacity.';
                END IF;
            END
            $pet_shed_existing_count_guard$;
            """);
}
