namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string PetOwnerMergeContentGuardSql =
        """
        CREATE OR REPLACE FUNCTION public.reject_pet_owner_merge_content_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_pet_owner_merge_content_mutation$
        BEGIN
            IF TG_TABLE_NAME = 'pet_owner_merge_content_revisions'
               AND TG_OP = 'UPDATE'
               AND OLD.sealed_at IS NULL
               AND NEW.sealed_at IS NOT NULL
               AND NEW.revision = OLD.revision
               AND NEW.policy_version = OLD.policy_version
               AND NEW.effect_base_count = OLD.effect_base_count
               AND NEW.band_count = OLD.band_count
               AND NEW.rate_count = OLD.rate_count
               AND NEW.source = OLD.source
               AND NEW.created_at = OLD.created_at THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION
                'published pet owner-Merge content revisions are immutable';
        END
        $reject_pet_owner_merge_content_mutation$;

        CREATE TRIGGER trg_pet_owner_merge_effect_types_immutable
        BEFORE UPDATE OR DELETE ON public.pet_owner_merge_effect_types
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_pet_owner_merge_content_mutation();

        CREATE TRIGGER trg_pet_owner_merge_savvy_types_immutable
        BEFORE UPDATE OR DELETE ON public.pet_owner_merge_savvy_types
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_pet_owner_merge_content_mutation();

        CREATE TRIGGER trg_pet_owner_merge_revisions_immutable
        BEFORE UPDATE OR DELETE
        ON public.pet_owner_merge_content_revisions
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_pet_owner_merge_content_mutation();

        CREATE TRIGGER trg_pet_owner_merge_effect_bases_immutable
        BEFORE UPDATE OR DELETE ON public.pet_owner_merge_effect_bases
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_pet_owner_merge_content_mutation();

        CREATE TRIGGER trg_pet_owner_merge_savvy_bands_immutable
        BEFORE UPDATE OR DELETE ON public.pet_owner_merge_savvy_bands
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_pet_owner_merge_content_mutation();

        CREATE TRIGGER trg_pet_owner_merge_rates_immutable
        BEFORE UPDATE OR DELETE ON public.pet_owner_merge_rates
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_pet_owner_merge_content_mutation();

        CREATE OR REPLACE FUNCTION public.guard_pet_owner_merge_content_insert()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_pet_owner_merge_content_insert$
        DECLARE
            expected public.pet_owner_merge_content_revisions%ROWTYPE;
            declared_count integer;
            existing_count bigint;
        BEGIN
            SELECT * INTO expected
            FROM public.pet_owner_merge_content_revisions
            WHERE revision = NEW.revision
            FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION
                    'unknown pet owner-Merge content revision %', NEW.revision;
            END IF;
            IF expected.sealed_at IS NOT NULL THEN
                RAISE EXCEPTION
                    'pet owner-Merge content revision % is already sealed',
                    NEW.revision;
            END IF;

            declared_count := CASE TG_TABLE_NAME
                WHEN 'pet_owner_merge_effect_bases'
                    THEN expected.effect_base_count
                WHEN 'pet_owner_merge_savvy_bands'
                    THEN expected.band_count
                WHEN 'pet_owner_merge_rates'
                    THEN expected.rate_count
                ELSE NULL
            END;
            IF declared_count IS NULL THEN
                RAISE EXCEPTION
                    'unsupported pet owner-Merge content table %',
                    TG_TABLE_NAME;
            END IF;

            EXECUTE format(
                'SELECT count(*) FROM public.%I WHERE revision = $1',
                TG_TABLE_NAME)
            INTO existing_count
            USING NEW.revision;
            IF existing_count >= declared_count THEN
                RAISE EXCEPTION
                    'pet owner-Merge table % exceeds declared count % for revision %',
                    TG_TABLE_NAME,
                    declared_count,
                    NEW.revision;
            END IF;
            RETURN NEW;
        END
        $guard_pet_owner_merge_content_insert$;

        CREATE TRIGGER trg_pet_owner_merge_effect_bases_insert_guard
        BEFORE INSERT ON public.pet_owner_merge_effect_bases
        FOR EACH ROW EXECUTE FUNCTION
            public.guard_pet_owner_merge_content_insert();

        CREATE TRIGGER trg_pet_owner_merge_savvy_bands_insert_guard
        BEFORE INSERT ON public.pet_owner_merge_savvy_bands
        FOR EACH ROW EXECUTE FUNCTION
            public.guard_pet_owner_merge_content_insert();

        CREATE TRIGGER trg_pet_owner_merge_rates_insert_guard
        BEFORE INSERT ON public.pet_owner_merge_rates
        FOR EACH ROW EXECUTE FUNCTION
            public.guard_pet_owner_merge_content_insert();

        CREATE OR REPLACE FUNCTION public.validate_pet_owner_merge_publication()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $validate_pet_owner_merge_publication$
        DECLARE
            expected public.pet_owner_merge_content_revisions%ROWTYPE;
        BEGIN
            SELECT * INTO expected
            FROM public.pet_owner_merge_content_revisions
            WHERE revision = NEW.revision
            FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION
                    'unknown pet owner-Merge content revision %', NEW.revision;
            END IF;

            IF (SELECT count(*) FROM public.pet_owner_merge_effect_bases
                WHERE revision = NEW.revision) <>
                    expected.effect_base_count OR
               (SELECT count(*) FROM public.pet_owner_merge_savvy_bands
                WHERE revision = NEW.revision) <>
                    expected.band_count OR
               (SELECT count(*) FROM public.pet_owner_merge_rates
                WHERE revision = NEW.revision) <>
                    expected.rate_count THEN
                RAISE EXCEPTION
                    'pet owner-Merge content revision % is incomplete',
                    NEW.revision;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM (
                    SELECT band_index,
                           minimum_savvy,
                           maximum_savvy,
                           lag(maximum_savvy) OVER (
                               ORDER BY band_index
                           ) AS previous_maximum,
                           row_number() OVER (
                               ORDER BY band_index
                           ) AS expected_index,
                           count(*) OVER () AS total_bands
                    FROM public.pet_owner_merge_savvy_bands
                    WHERE revision = NEW.revision
                ) band
                WHERE band.band_index <> band.expected_index
                   OR (
                       band.band_index = 1 AND
                       band.minimum_savvy <> 0
                   )
                   OR (
                       band.band_index > 1 AND
                       band.minimum_savvy IS DISTINCT FROM
                           band.previous_maximum
                   )
                   OR (
                       band.band_index < band.total_bands AND
                       band.maximum_savvy IS NULL
                   )
                   OR (
                       band.band_index = band.total_bands AND
                       band.maximum_savvy IS NOT NULL
                   )
            ) THEN
                RAISE EXCEPTION
                    'pet owner-Merge content revision % has non-contiguous bands',
                    NEW.revision;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM public.pet_owner_merge_rates rate
                WHERE rate.revision = NEW.revision
                GROUP BY rate.source_savvy, rate.effect_code
                HAVING count(*) <> expected.band_count
            ) OR (
                SELECT count(*)
                FROM (
                    SELECT DISTINCT source_savvy, effect_code
                    FROM public.pet_owner_merge_rates
                    WHERE revision = NEW.revision
                ) mapping
            ) <> 19 THEN
                RAISE EXCEPTION
                    'pet owner-Merge content revision % has incomplete typed rates',
                    NEW.revision;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM (
                    SELECT source_savvy,
                           effect_code,
                           band_index,
                           rate_per_savvy,
                           lag(rate_per_savvy) OVER (
                               PARTITION BY source_savvy, effect_code
                               ORDER BY band_index
                           ) AS previous_rate
                    FROM public.pet_owner_merge_rates
                    WHERE revision = NEW.revision
                ) rate
                WHERE rate.previous_rate IS NOT NULL
                  AND rate.rate_per_savvy > rate.previous_rate
            ) THEN
                RAISE EXCEPTION
                    'pet owner-Merge content revision % has an increasing marginal rate',
                    NEW.revision;
            END IF;

            UPDATE public.pet_owner_merge_content_revisions
            SET sealed_at = now()
            WHERE revision = NEW.revision
              AND sealed_at IS NULL;
            RETURN NEW;
        END
        $validate_pet_owner_merge_publication$;

        CREATE TRIGGER trg_pet_owner_merge_publication_complete
        BEFORE INSERT OR UPDATE
        ON public.pet_owner_merge_content_publication
        FOR EACH ROW EXECUTE FUNCTION
            public.validate_pet_owner_merge_publication();

        CREATE OR REPLACE FUNCTION
            public.reject_pet_owner_merge_publication_delete()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_pet_owner_merge_publication_delete$
        BEGIN
            RAISE EXCEPTION
                'the official pet owner-Merge publication cannot be deleted';
        END
        $reject_pet_owner_merge_publication_delete$;

        CREATE TRIGGER trg_pet_owner_merge_publication_no_delete
        BEFORE DELETE ON public.pet_owner_merge_content_publication
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_pet_owner_merge_publication_delete();
        """;
}
