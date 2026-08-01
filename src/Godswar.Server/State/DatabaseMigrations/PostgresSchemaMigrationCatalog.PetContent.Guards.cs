namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string PetContentGuardSql =
        """
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
               AND NEW.source = OLD.source
               AND NEW.created_at = OLD.created_at THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION 'published pet-content revisions are immutable';
        END
        $reject_pet_content_mutation$;

        CREATE TRIGGER trg_pet_content_revisions_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_revisions
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();

        CREATE TRIGGER trg_pet_content_settings_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_settings
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();

        CREATE TRIGGER trg_pet_content_species_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_species_definitions
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();

        CREATE TRIGGER trg_pet_content_aptitudes_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_aptitude_definitions
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();

        CREATE TRIGGER trg_pet_content_profiles_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_native_profiles
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();

        CREATE TRIGGER trg_pet_content_experience_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_experience_steps
        FOR EACH ROW EXECUTE FUNCTION public.reject_pet_content_mutation();

        CREATE TRIGGER trg_pet_content_rebirth_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_rebirth_steps
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

        CREATE TRIGGER trg_pet_content_settings_insert_guard
        BEFORE INSERT ON public.pet_content_settings
        FOR EACH ROW EXECUTE FUNCTION public.guard_pet_content_insert();

        CREATE TRIGGER trg_pet_content_species_insert_guard
        BEFORE INSERT ON public.pet_content_species_definitions
        FOR EACH ROW EXECUTE FUNCTION public.guard_pet_content_insert();

        CREATE TRIGGER trg_pet_content_aptitudes_insert_guard
        BEFORE INSERT ON public.pet_content_aptitude_definitions
        FOR EACH ROW EXECUTE FUNCTION public.guard_pet_content_insert();

        CREATE TRIGGER trg_pet_content_profiles_insert_guard
        BEFORE INSERT ON public.pet_content_native_profiles
        FOR EACH ROW EXECUTE FUNCTION public.guard_pet_content_insert();

        CREATE TRIGGER trg_pet_content_experience_insert_guard
        BEFORE INSERT ON public.pet_content_experience_steps
        FOR EACH ROW EXECUTE FUNCTION public.guard_pet_content_insert();

        CREATE TRIGGER trg_pet_content_rebirth_insert_guard
        BEFORE INSERT ON public.pet_content_rebirth_steps
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
                WHERE revision = NEW.revision) <> expected.rebirth_step_count THEN
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

        CREATE TRIGGER trg_pet_content_publication_complete
        BEFORE INSERT OR UPDATE ON public.pet_content_publication
        FOR EACH ROW EXECUTE FUNCTION public.validate_pet_content_publication();

        CREATE OR REPLACE FUNCTION public.reject_pet_content_publication_delete()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_pet_content_publication_delete$
        BEGIN
            RAISE EXCEPTION 'the official pet-content publication cannot be deleted';
        END
        $reject_pet_content_publication_delete$;

        CREATE TRIGGER trg_pet_content_publication_no_delete
        BEFORE DELETE ON public.pet_content_publication
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_pet_content_publication_delete();
        """;
}
