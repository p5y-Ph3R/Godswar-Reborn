namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateMedusaPetCaptureRarity() => new(
        "20260827_115_medusa_pet_capture_rarity",
        "Store Medusa Rock Elf capture rarity weights",
        """
        CREATE TABLE IF NOT EXISTS public.medusa_pet_capture_rarity_weights (
            difficulty smallint NOT NULL
                CHECK (difficulty IN (2, 3)),
            egg_item_id integer NOT NULL
                REFERENCES public.item_templates(id) ON DELETE RESTRICT,
            aptitude smallint NOT NULL
                REFERENCES public.pet_aptitude_templates(aptitude)
                ON DELETE RESTRICT,
            weight_basis_points integer NOT NULL
                CHECK (weight_basis_points BETWEEN 1 AND 10000),
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            PRIMARY KEY (difficulty, egg_item_id, aptitude),
            CONSTRAINT ck_medusa_capture_native_aptitude CHECK (
                aptitude IN (1, 2, 3, 4, 5, 7, 8, 9, 10, 12, 14))
        );

        CREATE OR REPLACE FUNCTION
            public.enforce_medusa_capture_rarity_total()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
            current_difficulty smallint;
            current_egg_item_id integer;
            old_total bigint;
            current_total bigint;
        BEGIN
            IF TG_OP = 'DELETE' THEN
                current_difficulty := OLD.difficulty;
                current_egg_item_id := OLD.egg_item_id;
            ELSE
                current_difficulty := NEW.difficulty;
                current_egg_item_id := NEW.egg_item_id;
            END IF;

            SELECT COALESCE(sum(weight_basis_points), 0)
            INTO current_total
            FROM public.medusa_pet_capture_rarity_weights
            WHERE difficulty = current_difficulty
              AND egg_item_id = current_egg_item_id;

            IF current_total <> 10000 THEN
                RAISE EXCEPTION
                    'Medusa capture weights for difficulty % and egg % total %, expected 10000',
                    current_difficulty,
                    current_egg_item_id,
                    current_total;
            END IF;

            IF TG_OP = 'UPDATE' AND
               (OLD.difficulty, OLD.egg_item_id) IS DISTINCT FROM
               (NEW.difficulty, NEW.egg_item_id) THEN
                SELECT COALESCE(sum(weight_basis_points), 0)
                INTO old_total
                FROM public.medusa_pet_capture_rarity_weights
                WHERE difficulty = OLD.difficulty
                  AND egg_item_id = OLD.egg_item_id;

                IF old_total <> 10000 THEN
                    RAISE EXCEPTION
                        'Medusa capture weights for difficulty % and egg % total %, expected 10000',
                        OLD.difficulty,
                        OLD.egg_item_id,
                        old_total;
                END IF;
            END IF;

            RETURN NULL;
        END
        $function$;

        DROP TRIGGER IF EXISTS
            trg_medusa_capture_rarity_total
            ON public.medusa_pet_capture_rarity_weights;
        CREATE CONSTRAINT TRIGGER trg_medusa_capture_rarity_total
            AFTER INSERT OR UPDATE OR DELETE
            ON public.medusa_pet_capture_rarity_weights
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            EXECUTE FUNCTION public.enforce_medusa_capture_rarity_total();

        INSERT INTO public.medusa_pet_capture_rarity_weights (
            difficulty,
            egg_item_id,
            aptitude,
            weight_basis_points)
        VALUES
            (2, 10150,  1, 2000),
            (2, 10150,  2, 1800),
            (2, 10150,  3, 1600),
            (2, 10150,  4, 1400),
            (2, 10150,  5, 1100),
            (2, 10150,  7,  800),
            (2, 10150,  8,  500),
            (2, 10150,  9,  400),
            (2, 10150, 10,  200),
            (2, 10150, 12,  150),
            (2, 10150, 14,   50),
            (3, 10150,  1,  400),
            (3, 10150,  2,  600),
            (3, 10150,  3,  800),
            (3, 10150,  4, 1100),
            (3, 10150,  5, 1300),
            (3, 10150,  7, 1400),
            (3, 10150,  8, 1400),
            (3, 10150,  9, 1200),
            (3, 10150, 10,  900),
            (3, 10150, 12,  600),
            (3, 10150, 14,  300)
        ON CONFLICT (difficulty, egg_item_id, aptitude) DO UPDATE
        SET weight_basis_points = EXCLUDED.weight_basis_points,
            updated_at = clock_timestamp();
        """);
}
