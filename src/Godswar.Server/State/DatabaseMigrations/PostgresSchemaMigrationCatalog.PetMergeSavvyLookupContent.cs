namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetMergeSavvyLookupContentRelease() => new(
        "20260812_084_pet_merge_savvy_lookup_content",
        "Publish the immutable native pet merge-savvy lookup",
        """
        ALTER TABLE public.pet_content_revisions
            ADD COLUMN merge_savvy_lookup_count integer NOT NULL DEFAULT 0,
            ADD CONSTRAINT ck_pet_content_revisions_merge_savvy_lookup_count
                CHECK (merge_savvy_lookup_count >= 0);

        CREATE TABLE public.pet_content_merge_savvy_lookup (
            revision varchar(64) NOT NULL,
            minimum_savvy_difference integer NOT NULL,
            base_increase integer NOT NULL,
            CONSTRAINT pk_pet_content_merge_savvy_lookup
                PRIMARY KEY (revision, minimum_savvy_difference),
            CONSTRAINT fk_pet_content_merge_savvy_lookup_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_merge_savvy_lookup_difference
                CHECK (minimum_savvy_difference BETWEEN -65535 AND 65535),
            CONSTRAINT ck_pet_content_merge_savvy_lookup_base
                CHECK (base_increase BETWEEN 1 AND 65535)
        );

        ALTER TABLE public.pet_content_merge_rank_spirit_steps
            DROP CONSTRAINT ck_pet_content_merge_rank_spirit_count,
            DROP CONSTRAINT ck_pet_content_merge_rank_spirit_range,
            ADD CONSTRAINT ck_pet_content_merge_rank_spirit_count
                CHECK (spirit_count BETWEEN 0 AND 5),
            ADD CONSTRAINT ck_pet_content_merge_rank_spirit_range CHECK (
                ((spirit_count = 0 AND minimum_percent = 0) OR
                 (spirit_count > 0 AND
                  minimum_percent BETWEEN 1 AND 100)) AND
                maximum_percent BETWEEN minimum_percent AND 100);

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
               AND NEW.merge_savvy_lookup_count =
                   OLD.merge_savvy_lookup_count
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

        CREATE TRIGGER trg_pet_content_merge_savvy_lookup_immutable
        BEFORE UPDATE OR DELETE ON public.pet_content_merge_savvy_lookup
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
                WHEN 'pet_content_merge_savvy_lookup'
                    THEN expected.merge_savvy_lookup_count
                WHEN 'pet_content_hatch_rank_steps'
                    THEN expected.hatch_rank_step_count
                WHEN 'pet_content_merge_rank_lookup'
                    THEN expected.merge_rank_lookup_count
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

        CREATE TRIGGER trg_pet_content_merge_savvy_lookup_insert_guard
        BEFORE INSERT ON public.pet_content_merge_savvy_lookup
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
                WHERE revision = NEW.revision) <>
                    expected.merge_savvy_step_count OR
               (SELECT count(*) FROM public.pet_content_merge_savvy_lookup
                WHERE revision = NEW.revision) <>
                    expected.merge_savvy_lookup_count OR
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
                RAISE EXCEPTION 'pet-content revision % is incomplete',
                    NEW.revision;
            END IF;
            UPDATE public.pet_content_revisions SET sealed_at = now()
            WHERE revision = NEW.revision AND sealed_at IS NULL;
            RETURN NEW;
        END
        $body$;
        """);
}
