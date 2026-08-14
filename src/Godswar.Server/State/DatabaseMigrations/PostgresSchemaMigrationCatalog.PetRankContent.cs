namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetRankContentRelease() => new(
        "20260812_081_pet_rank_content",
        "Publish hatch and Merge rank policy with durable hatch evidence",
        """
        ALTER TABLE public.pet_content_revisions
            ADD COLUMN hatch_rank_step_count integer NOT NULL DEFAULT 0,
            ADD COLUMN merge_rank_lookup_count integer NOT NULL DEFAULT 0,
            ADD COLUMN merge_rank_species_factor_count integer NOT NULL DEFAULT 0,
            ADD COLUMN merge_rank_spirit_step_count integer NOT NULL DEFAULT 0,
            ADD CONSTRAINT ck_pet_content_revisions_hatch_rank_count
                CHECK (hatch_rank_step_count >= 0),
            ADD CONSTRAINT ck_pet_content_revisions_merge_rank_lookup_count
                CHECK (merge_rank_lookup_count >= 0),
            ADD CONSTRAINT ck_pet_content_revisions_merge_rank_factor_count
                CHECK (merge_rank_species_factor_count >= 0),
            ADD CONSTRAINT ck_pet_content_revisions_merge_rank_spirit_count
                CHECK (merge_rank_spirit_step_count >= 0);

        ALTER TABLE public.pet_content_settings
            ADD COLUMN maximum_rank numeric(8, 2) NOT NULL DEFAULT 655.35,
            ADD CONSTRAINT ck_pet_content_settings_maximum_rank
                CHECK (maximum_rank > 0 AND maximum_rank <= 655.35);

        DO $rank_wire_preflight$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM public.character_pets
                WHERE rank > 655.35 OR
                      rank * 100 <> trunc(rank * 100)
            ) THEN
                RAISE EXCEPTION
                    'character_pets contains rank outside native UInt16 hundredths';
            END IF;
        END
        $rank_wire_preflight$;

        ALTER TABLE public.character_pets
            ADD CONSTRAINT ck_character_pets_rank_wire_range CHECK (
                rank <= 655.35 AND rank * 100 = trunc(rank * 100));

        CREATE TABLE public.pet_content_hatch_rank_steps (
            revision varchar(64) NOT NULL,
            aptitude smallint NOT NULL,
            outcome_order smallint NOT NULL,
            rank numeric(8, 2) NOT NULL,
            weight smallint NOT NULL,
            CONSTRAINT pk_pet_content_hatch_rank_steps
                PRIMARY KEY (revision, aptitude, outcome_order),
            CONSTRAINT fk_pet_content_hatch_rank_revision
                FOREIGN KEY (revision) REFERENCES public.pet_content_revisions
                    (revision) ON DELETE RESTRICT,
            CONSTRAINT fk_pet_content_hatch_rank_aptitude
                FOREIGN KEY (revision, aptitude)
                REFERENCES public.pet_content_aptitude_definitions
                    (revision, aptitude) ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_hatch_rank_order
                CHECK (outcome_order BETWEEN 0 AND 2),
            CONSTRAINT ck_pet_content_hatch_rank_value
                CHECK (rank >= 0 AND rank <= 655.35),
            CONSTRAINT ck_pet_content_hatch_rank_weight
                CHECK (weight > 0 AND weight <= 100)
        );

        CREATE TABLE public.pet_content_merge_rank_lookup (
            revision varchar(64) NOT NULL,
            minimum_rank_difference integer NOT NULL,
            base_increase integer NOT NULL,
            CONSTRAINT pk_pet_content_merge_rank_lookup
                PRIMARY KEY (revision, minimum_rank_difference),
            CONSTRAINT fk_pet_content_merge_rank_lookup_revision
                FOREIGN KEY (revision) REFERENCES public.pet_content_revisions
                    (revision) ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_merge_rank_difference
                CHECK (minimum_rank_difference BETWEEN -65535 AND 65535),
            CONSTRAINT ck_pet_content_merge_rank_base
                CHECK (base_increase BETWEEN 1 AND 65535)
        );

        CREATE TABLE public.pet_content_merge_rank_species_factors (
            revision varchar(64) NOT NULL,
            species_id smallint NOT NULL,
            factor numeric(4, 2) NOT NULL,
            CONSTRAINT pk_pet_content_merge_rank_species_factors
                PRIMARY KEY (revision, species_id),
            CONSTRAINT fk_pet_content_merge_rank_factor_revision
                FOREIGN KEY (revision) REFERENCES public.pet_content_revisions
                    (revision) ON DELETE RESTRICT,
            CONSTRAINT fk_pet_content_merge_rank_factor_species
                FOREIGN KEY (revision, species_id)
                REFERENCES public.pet_content_species_definitions
                    (revision, species_id) ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_merge_rank_factor
                CHECK (factor > 0 AND factor <= 10)
        );

        CREATE TABLE public.pet_content_merge_rank_spirit_steps (
            revision varchar(64) NOT NULL,
            spirit_count smallint NOT NULL,
            minimum_percent smallint NOT NULL,
            maximum_percent smallint NOT NULL,
            CONSTRAINT pk_pet_content_merge_rank_spirit_steps
                PRIMARY KEY (revision, spirit_count),
            CONSTRAINT fk_pet_content_merge_rank_spirit_revision
                FOREIGN KEY (revision) REFERENCES public.pet_content_revisions
                    (revision) ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_merge_rank_spirit_count
                CHECK (spirit_count BETWEEN 1 AND 5),
            CONSTRAINT ck_pet_content_merge_rank_spirit_range CHECK (
                minimum_percent BETWEEN 1 AND 100 AND
                maximum_percent BETWEEN minimum_percent AND 100)
        );

        ALTER TABLE public.character_pets
            ADD COLUMN birth_rank numeric(18, 6) NULL,
            ADD COLUMN hatch_rank_roll smallint NULL,
            ADD COLUMN hatch_rank_outcome_order smallint NULL,
            ADD COLUMN hatch_rank_content_revision varchar(64) NULL,
            ADD CONSTRAINT ck_character_pets_birth_rank
                CHECK (birth_rank >= 0 AND birth_rank <= 655.35),
            ADD CONSTRAINT ck_character_pets_hatch_rank_roll
                CHECK (hatch_rank_roll BETWEEN 0 AND 99),
            ADD CONSTRAINT ck_character_pets_hatch_rank_outcome
                CHECK (hatch_rank_outcome_order BETWEEN 0 AND 2),
            ADD CONSTRAINT ck_character_pets_hatch_rank_evidence CHECK (
                (birth_rank IS NULL AND hatch_rank_roll IS NULL AND
                 hatch_rank_outcome_order IS NULL AND
                 hatch_rank_content_revision IS NULL) OR
                (birth_rank IS NOT NULL AND hatch_rank_roll IS NOT NULL AND
                 hatch_rank_outcome_order IS NOT NULL AND
                 hatch_rank_content_revision IS NOT NULL)),
            ADD CONSTRAINT fk_character_pets_hatch_rank_revision
                FOREIGN KEY (hatch_rank_content_revision)
                REFERENCES public.pet_content_revisions (revision)
                ON DELETE RESTRICT;

        CREATE OR REPLACE FUNCTION public.reject_pet_content_mutation()
        RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
            IF TG_TABLE_NAME = 'pet_content_revisions'
               AND TG_OP = 'UPDATE'
               AND OLD.sealed_at IS NULL AND NEW.sealed_at IS NOT NULL
               AND NEW.revision = OLD.revision
               AND NEW.species_count = OLD.species_count
               AND NEW.aptitude_count = OLD.aptitude_count
               AND NEW.native_profile_count = OLD.native_profile_count
               AND NEW.experience_step_count = OLD.experience_step_count
               AND NEW.rebirth_step_count = OLD.rebirth_step_count
               AND NEW.merge_savvy_step_count = OLD.merge_savvy_step_count
               AND NEW.hatch_rank_step_count = OLD.hatch_rank_step_count
               AND NEW.merge_rank_lookup_count = OLD.merge_rank_lookup_count
               AND NEW.merge_rank_species_factor_count =
                   OLD.merge_rank_species_factor_count
               AND NEW.merge_rank_spirit_step_count =
                   OLD.merge_rank_spirit_step_count
               AND NEW.source = OLD.source
               AND NEW.created_at = OLD.created_at THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION 'published pet-content revisions are immutable';
        END
        $body$;

        CREATE TRIGGER trg_pet_content_hatch_rank_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_hatch_rank_steps
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();
        CREATE TRIGGER trg_pet_content_merge_rank_lookup_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_merge_rank_lookup
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();
        CREATE TRIGGER trg_pet_content_merge_rank_factor_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_merge_rank_species_factors
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();
        CREATE TRIGGER trg_pet_content_merge_rank_spirit_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_merge_rank_spirit_steps
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();

        CREATE OR REPLACE FUNCTION public.guard_pet_content_insert()
        RETURNS trigger LANGUAGE plpgsql AS $body$
        DECLARE
            expected public.pet_content_revisions%ROWTYPE;
            declared_count integer;
            existing_count bigint;
        BEGIN
            SELECT * INTO expected FROM public.pet_content_revisions
            WHERE revision = NEW.revision FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'unknown pet-content revision %', NEW.revision;
            END IF;
            IF expected.sealed_at IS NOT NULL THEN
                RAISE EXCEPTION 'pet-content revision % is already sealed',
                    NEW.revision;
            END IF;
            declared_count := CASE TG_TABLE_NAME
                WHEN 'pet_content_settings' THEN 1
                WHEN 'pet_content_species_definitions' THEN expected.species_count
                WHEN 'pet_content_aptitude_definitions' THEN expected.aptitude_count
                WHEN 'pet_content_native_profiles' THEN expected.native_profile_count
                WHEN 'pet_content_experience_steps' THEN expected.experience_step_count
                WHEN 'pet_content_rebirth_steps' THEN expected.rebirth_step_count
                WHEN 'pet_content_merge_savvy_steps' THEN expected.merge_savvy_step_count
                WHEN 'pet_content_hatch_rank_steps' THEN expected.hatch_rank_step_count
                WHEN 'pet_content_merge_rank_lookup' THEN expected.merge_rank_lookup_count
                WHEN 'pet_content_merge_rank_species_factors'
                    THEN expected.merge_rank_species_factor_count
                WHEN 'pet_content_merge_rank_spirit_steps'
                    THEN expected.merge_rank_spirit_step_count
                ELSE NULL
            END;
            IF declared_count IS NULL THEN
                RAISE EXCEPTION 'unsupported pet-content table %', TG_TABLE_NAME;
            END IF;
            EXECUTE format(
                'SELECT count(*) FROM public.%I WHERE revision = $1',
                TG_TABLE_NAME) INTO existing_count USING NEW.revision;
            IF existing_count >= declared_count THEN
                RAISE EXCEPTION
                    'pet-content table % exceeds declared count % for revision %',
                    TG_TABLE_NAME, declared_count, NEW.revision;
            END IF;
            RETURN NEW;
        END
        $body$;

        CREATE TRIGGER trg_pet_content_hatch_rank_insert_guard
        BEFORE INSERT ON public.pet_content_hatch_rank_steps
        FOR EACH ROW EXECUTE FUNCTION public.guard_pet_content_insert();
        CREATE TRIGGER trg_pet_content_merge_rank_lookup_insert_guard
        BEFORE INSERT ON public.pet_content_merge_rank_lookup
        FOR EACH ROW EXECUTE FUNCTION public.guard_pet_content_insert();
        CREATE TRIGGER trg_pet_content_merge_rank_factor_insert_guard
        BEFORE INSERT ON public.pet_content_merge_rank_species_factors
        FOR EACH ROW EXECUTE FUNCTION public.guard_pet_content_insert();
        CREATE TRIGGER trg_pet_content_merge_rank_spirit_insert_guard
        BEFORE INSERT ON public.pet_content_merge_rank_spirit_steps
        FOR EACH ROW EXECUTE FUNCTION public.guard_pet_content_insert();

        CREATE OR REPLACE FUNCTION public.validate_pet_content_publication()
        RETURNS trigger LANGUAGE plpgsql AS $body$
        DECLARE expected public.pet_content_revisions%ROWTYPE;
        BEGIN
            SELECT * INTO expected FROM public.pet_content_revisions
            WHERE revision = NEW.revision FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'unknown pet-content revision %', NEW.revision;
            END IF;
            IF (SELECT count(*) FROM public.pet_content_settings
                WHERE revision = NEW.revision) <> 1 OR
               (SELECT count(*) FROM public.pet_content_species_definitions
                WHERE revision = NEW.revision) <> expected.species_count OR
               (SELECT count(*) FROM public.pet_content_aptitude_definitions
                WHERE revision = NEW.revision) <> expected.aptitude_count OR
               (SELECT count(*) FROM public.pet_content_native_profiles
                WHERE revision = NEW.revision) <> expected.native_profile_count OR
               (SELECT count(*) FROM public.pet_content_experience_steps
                WHERE revision = NEW.revision) <> expected.experience_step_count OR
               (SELECT count(*) FROM public.pet_content_rebirth_steps
                WHERE revision = NEW.revision) <> expected.rebirth_step_count OR
               (SELECT count(*) FROM public.pet_content_merge_savvy_steps
                WHERE revision = NEW.revision) <> expected.merge_savvy_step_count OR
               (SELECT count(*) FROM public.pet_content_hatch_rank_steps
                WHERE revision = NEW.revision) <> expected.hatch_rank_step_count OR
               (SELECT count(*) FROM public.pet_content_merge_rank_lookup
                WHERE revision = NEW.revision) <> expected.merge_rank_lookup_count OR
               (SELECT count(*) FROM public.pet_content_merge_rank_species_factors
                WHERE revision = NEW.revision) <>
                    expected.merge_rank_species_factor_count OR
               (SELECT count(*) FROM public.pet_content_merge_rank_spirit_steps
                WHERE revision = NEW.revision) <>
                    expected.merge_rank_spirit_step_count THEN
                RAISE EXCEPTION 'pet-content revision % is incomplete', NEW.revision;
            END IF;
            UPDATE public.pet_content_revisions SET sealed_at = now()
            WHERE revision = NEW.revision AND sealed_at IS NULL;
            RETURN NEW;
        END
        $body$;
        """);
}
