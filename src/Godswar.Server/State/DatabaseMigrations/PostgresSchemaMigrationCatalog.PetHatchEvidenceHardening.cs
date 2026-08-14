namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetHatchEvidenceHardening() => new(
        "20260812_082_pet_hatch_evidence_hardening",
        "Bind immutable hatch-rank evidence to published pet content",
        """
        DO $pet_hatch_evidence_preflight$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM public.character_pets pet
                LEFT JOIN public.pet_content_revisions content_revision
                  ON content_revision.revision =
                     pet.hatch_rank_content_revision
                 AND content_revision.sealed_at IS NOT NULL
                LEFT JOIN public.pet_content_hatch_rank_steps chosen
                  ON chosen.revision = pet.hatch_rank_content_revision
                 AND chosen.aptitude = pet.aptitude
                 AND chosen.outcome_order =
                     pet.hatch_rank_outcome_order
                 AND chosen.rank = pet.birth_rank
                WHERE pet.birth_rank IS NOT NULL
                  AND (
                      pet.birth_rank * 100 <>
                          trunc(pet.birth_rank * 100) OR
                      content_revision.revision IS NULL OR
                      chosen.revision IS NULL OR
                      pet.hatch_rank_roll < COALESCE((
                          SELECT sum(prior.weight)
                          FROM public.pet_content_hatch_rank_steps prior
                          WHERE prior.revision =
                                    pet.hatch_rank_content_revision
                            AND prior.aptitude = pet.aptitude
                            AND prior.outcome_order <
                                pet.hatch_rank_outcome_order
                      ), 0) OR
                      pet.hatch_rank_roll >= COALESCE((
                          SELECT sum(prior.weight)
                          FROM public.pet_content_hatch_rank_steps prior
                          WHERE prior.revision =
                                    pet.hatch_rank_content_revision
                            AND prior.aptitude = pet.aptitude
                            AND prior.outcome_order <
                                pet.hatch_rank_outcome_order
                      ), 0) + chosen.weight
                  )
            ) THEN
                RAISE EXCEPTION
                    'character_pets contains inconsistent hatch-rank evidence';
            END IF;
        END
        $pet_hatch_evidence_preflight$;

        ALTER TABLE public.pet_content_hatch_rank_steps
            ADD CONSTRAINT ux_pet_content_hatch_rank_evidence
            UNIQUE (revision, aptitude, outcome_order, rank);

        ALTER TABLE public.character_pets
            ADD CONSTRAINT ck_character_pets_birth_rank_hundredths
            CHECK (
                birth_rank IS NULL OR
                birth_rank * 100 = trunc(birth_rank * 100)
            ),
            ADD CONSTRAINT fk_character_pets_hatch_rank_evidence
            FOREIGN KEY (
                hatch_rank_content_revision,
                aptitude,
                hatch_rank_outcome_order,
                birth_rank
            ) REFERENCES public.pet_content_hatch_rank_steps (
                revision,
                aptitude,
                outcome_order,
                rank
            ) ON DELETE RESTRICT;

        CREATE OR REPLACE FUNCTION public.guard_pet_hatch_rank_evidence()
        RETURNS trigger LANGUAGE plpgsql AS $body$
        DECLARE
            lower_roll integer;
            selected_weight integer;
        BEGIN
            IF TG_OP = 'UPDATE' THEN
                IF (OLD.birth_rank IS NOT NULL AND
                    NEW.aptitude IS DISTINCT FROM OLD.aptitude) OR ROW(
                    NEW.birth_rank,
                    NEW.hatch_rank_roll,
                    NEW.hatch_rank_outcome_order,
                    NEW.hatch_rank_content_revision
                ) IS DISTINCT FROM ROW(
                    OLD.birth_rank,
                    OLD.hatch_rank_roll,
                    OLD.hatch_rank_outcome_order,
                    OLD.hatch_rank_content_revision
                ) THEN
                    RAISE EXCEPTION
                        'pet hatch-rank evidence is immutable';
                END IF;
                RETURN NEW;
            END IF;

            IF NEW.birth_rank IS NULL THEN
                RAISE EXCEPTION
                    'new pets require complete hatch-rank evidence';
            END IF;

            SELECT step.weight
            INTO selected_weight
            FROM public.pet_content_hatch_rank_steps step
            INNER JOIN public.pet_content_revisions content_revision
              ON content_revision.revision = step.revision
             AND content_revision.sealed_at IS NOT NULL
            WHERE step.revision = NEW.hatch_rank_content_revision
              AND step.aptitude = NEW.aptitude
              AND step.outcome_order = NEW.hatch_rank_outcome_order
              AND step.rank = NEW.birth_rank;

            SELECT COALESCE(sum(step.weight), 0)
            INTO lower_roll
            FROM public.pet_content_hatch_rank_steps step
            WHERE step.revision = NEW.hatch_rank_content_revision
              AND step.aptitude = NEW.aptitude
              AND step.outcome_order < NEW.hatch_rank_outcome_order;

            IF selected_weight IS NULL OR
               NEW.hatch_rank_roll < lower_roll OR
               NEW.hatch_rank_roll >= lower_roll + selected_weight THEN
                RAISE EXCEPTION
                    'pet hatch-rank evidence does not match published content';
            END IF;

            RETURN NEW;
        END
        $body$;

        CREATE TRIGGER trg_character_pets_hatch_rank_evidence_guard
        BEFORE INSERT OR UPDATE ON public.character_pets
        FOR EACH ROW EXECUTE FUNCTION
            public.guard_pet_hatch_rank_evidence();
        """);
}
