namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetMergeBalanceContentRelease() => new(
        "20260811_074_pet_merge_balance_content",
        "Publish immutable aptitude and spirit-count pet merge savvy ranges",
        """
        ALTER TABLE public.pet_content_revisions
            ADD COLUMN merge_savvy_step_count integer NOT NULL DEFAULT 0,
            ADD CONSTRAINT ck_pet_content_revisions_merge_savvy_step_count
                CHECK (merge_savvy_step_count >= 0);

        CREATE TABLE public.pet_content_merge_savvy_steps (
            revision varchar(64) NOT NULL,
            aptitude smallint NOT NULL,
            spirit_count smallint NOT NULL,
            minimum_increase_per_stat numeric(18, 6) NOT NULL,
            maximum_increase_per_stat numeric(18, 6) NOT NULL,
            CONSTRAINT pk_pet_content_merge_savvy_steps
                PRIMARY KEY (revision, aptitude, spirit_count),
            CONSTRAINT fk_pet_content_merge_savvy_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT fk_pet_content_merge_savvy_aptitude
                FOREIGN KEY (revision, aptitude)
                REFERENCES public.pet_content_aptitude_definitions
                    (revision, aptitude)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_merge_savvy_aptitude
                CHECK (aptitude BETWEEN 1 AND 255),
            CONSTRAINT ck_pet_content_merge_savvy_spirits
                CHECK (spirit_count BETWEEN 0 AND 5),
            CONSTRAINT ck_pet_content_merge_savvy_range CHECK (
                minimum_increase_per_stat >= 0.01 AND
                maximum_increase_per_stat >= minimum_increase_per_stat
            )
        );

        CREATE OR REPLACE FUNCTION public.reject_pet_content_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_pet_content_mutation$
        BEGIN
            IF TG_TABLE_NAME = 'pet_content_revisions'
               AND TG_OP = 'UPDATE'
               AND OLD.sealed_at IS NULL
               AND NEW.sealed_at IS NOT NULL
               AND NEW.revision = OLD.revision
               AND NEW.species_count = OLD.species_count
               AND NEW.aptitude_count = OLD.aptitude_count
               AND NEW.native_profile_count = OLD.native_profile_count
               AND NEW.experience_step_count = OLD.experience_step_count
               AND NEW.rebirth_step_count = OLD.rebirth_step_count
               AND NEW.merge_savvy_step_count = OLD.merge_savvy_step_count
               AND NEW.source = OLD.source
               AND NEW.created_at = OLD.created_at THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION 'published pet-content revisions are immutable';
        END
        $reject_pet_content_mutation$;

        CREATE TRIGGER trg_pet_content_merge_savvy_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_merge_savvy_steps
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();

        CREATE OR REPLACE FUNCTION public.guard_pet_content_insert()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_pet_content_insert$
        DECLARE
            expected public.pet_content_revisions%ROWTYPE;
            declared_count integer;
            existing_count bigint;
        BEGIN
            SELECT *
              INTO expected
            FROM public.pet_content_revisions
            WHERE revision = NEW.revision
            FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'unknown pet-content revision %', NEW.revision;
            END IF;
            IF expected.sealed_at IS NOT NULL THEN
                RAISE EXCEPTION 'pet-content revision % is already sealed',
                    NEW.revision;
            END IF;

            declared_count := CASE TG_TABLE_NAME
                WHEN 'pet_content_settings' THEN 1
                WHEN 'pet_content_species_definitions'
                    THEN expected.species_count
                WHEN 'pet_content_aptitude_definitions'
                    THEN expected.aptitude_count
                WHEN 'pet_content_native_profiles'
                    THEN expected.native_profile_count
                WHEN 'pet_content_experience_steps'
                    THEN expected.experience_step_count
                WHEN 'pet_content_rebirth_steps'
                    THEN expected.rebirth_step_count
                WHEN 'pet_content_merge_savvy_steps'
                    THEN expected.merge_savvy_step_count
                ELSE NULL
            END;
            IF declared_count IS NULL THEN
                RAISE EXCEPTION 'unsupported pet-content table %',
                    TG_TABLE_NAME;
            END IF;

            EXECUTE format(
                'SELECT count(*) FROM public.%I WHERE revision = $1',
                TG_TABLE_NAME)
            INTO existing_count
            USING NEW.revision;
            IF existing_count >= declared_count THEN
                RAISE EXCEPTION
                    'pet-content table % exceeds declared count % for revision %',
                    TG_TABLE_NAME,
                    declared_count,
                    NEW.revision;
            END IF;
            RETURN NEW;
        END
        $guard_pet_content_insert$;

        CREATE TRIGGER trg_pet_content_merge_savvy_insert_guard
        BEFORE INSERT ON public.pet_content_merge_savvy_steps
        FOR EACH ROW EXECUTE FUNCTION public.guard_pet_content_insert();

        CREATE OR REPLACE FUNCTION public.validate_pet_content_publication()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $validate_pet_content_publication$
        DECLARE
            expected public.pet_content_revisions%ROWTYPE;
        BEGIN
            SELECT * INTO expected
            FROM public.pet_content_revisions
            WHERE revision = NEW.revision
            FOR UPDATE;
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
                WHERE revision = NEW.revision) <>
                    expected.merge_savvy_step_count THEN
                RAISE EXCEPTION
                    'pet-content revision % is incomplete', NEW.revision;
            END IF;
            UPDATE public.pet_content_revisions
            SET sealed_at = now()
            WHERE revision = NEW.revision
              AND sealed_at IS NULL;
            RETURN NEW;
        END
        $validate_pet_content_publication$;
        """);
}
