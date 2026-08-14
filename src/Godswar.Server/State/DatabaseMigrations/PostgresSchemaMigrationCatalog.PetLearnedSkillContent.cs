namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetLearnedSkillContentRelease() => new(
        "20260812_083_pet_learned_skill_content",
        "Publish normalized learned pet-skill rank curves",
        """
        CREATE TABLE public.pet_skill_content_revisions (
            revision varchar(64) PRIMARY KEY,
            curve_count integer NOT NULL CHECK (curve_count > 0),
            step_count integer NOT NULL CHECK (step_count > 0),
            source varchar(96) NOT NULL CHECK (btrim(source) <> ''),
            source_sha256 varchar(64) NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            sealed_at timestamptz,
            CONSTRAINT ck_pet_skill_content_revision_digest CHECK (
                revision ~ '^[0-9A-F]{64}$' AND
                source_sha256 ~ '^[0-9A-F]{64}$')
        );

        CREATE TABLE public.pet_skill_curve_definitions (
            revision varchar(64) NOT NULL REFERENCES
                public.pet_skill_content_revisions(revision)
                ON DELETE RESTRICT,
            family_type integer NOT NULL CHECK (family_type >= 0),
            priority smallint NOT NULL CHECK (priority > 0),
            genre integer NOT NULL CHECK (genre >= 0),
            effect integer NOT NULL CHECK (effect >= 0),
            opaque_add integer NOT NULL CHECK (opaque_add >= 0),
            opaque_flag integer NOT NULL CHECK (opaque_flag >= 0),
            required_agility numeric(18, 6) NOT NULL CHECK
                (required_agility >= 0),
            required_strength numeric(18, 6) NOT NULL CHECK
                (required_strength >= 0),
            required_accuracy numeric(18, 6) NOT NULL CHECK
                (required_accuracy >= 0),
            required_technique numeric(18, 6) NOT NULL CHECK
                (required_technique >= 0),
            required_wisdom numeric(18, 6) NOT NULL CHECK
                (required_wisdom >= 0),
            required_luck numeric(18, 6) NOT NULL CHECK
                (required_luck >= 0),
            first_runtime_skill_id integer NOT NULL CHECK
                (first_runtime_skill_id > 0),
            PRIMARY KEY (revision, family_type, priority),
            UNIQUE (revision, first_runtime_skill_id),
            CONSTRAINT ck_pet_skill_curve_single_trait CHECK (
                (CASE WHEN required_agility > 0 THEN 1 ELSE 0 END) +
                (CASE WHEN required_strength > 0 THEN 1 ELSE 0 END) +
                (CASE WHEN required_accuracy > 0 THEN 1 ELSE 0 END) +
                (CASE WHEN required_technique > 0 THEN 1 ELSE 0 END) +
                (CASE WHEN required_wisdom > 0 THEN 1 ELSE 0 END) +
                (CASE WHEN required_luck > 0 THEN 1 ELSE 0 END) <= 1)
        );

        CREATE TABLE public.pet_skill_curve_steps (
            revision varchar(64) NOT NULL,
            family_type integer NOT NULL,
            priority smallint NOT NULL,
            step_order smallint NOT NULL CHECK
                (step_order BETWEEN 0 AND 31),
            runtime_skill_id integer NOT NULL CHECK
                (runtime_skill_id > 0),
            minimum_pet_rank smallint NOT NULL CHECK
                (minimum_pet_rank BETWEEN 0 AND 655),
            absolute_value numeric(18, 6) NOT NULL CHECK
                (absolute_value > 0),
            PRIMARY KEY (revision, family_type, priority, step_order),
            UNIQUE (revision, runtime_skill_id),
            FOREIGN KEY (revision, family_type, priority) REFERENCES
                public.pet_skill_curve_definitions(
                    revision, family_type, priority)
                ON DELETE RESTRICT
        );

        CREATE TABLE public.pet_skill_content_publication (
            singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
            revision varchar(64) NOT NULL REFERENCES
                public.pet_skill_content_revisions(revision)
                ON DELETE RESTRICT,
            published_at timestamptz NOT NULL DEFAULT now()
        );

        CREATE OR REPLACE FUNCTION public.guard_pet_skill_content_rows()
        RETURNS trigger LANGUAGE plpgsql AS $body$
        DECLARE
            expected_count integer;
            existing_count integer;
            is_sealed boolean;
        BEGIN
            SELECT
                CASE TG_TABLE_NAME
                    WHEN 'pet_skill_curve_definitions'
                        THEN revision.curve_count
                    ELSE revision.step_count
                END,
                revision.sealed_at IS NOT NULL
            INTO expected_count, is_sealed
            FROM public.pet_skill_content_revisions revision
            WHERE revision.revision = NEW.revision
            FOR UPDATE;
            IF expected_count IS NULL OR is_sealed THEN
                RAISE EXCEPTION
                    'pet-skill content revision is absent or sealed';
            END IF;
            EXECUTE format(
                'SELECT count(*) FROM public.%I WHERE revision = $1',
                TG_TABLE_NAME)
            INTO existing_count
            USING NEW.revision;
            IF existing_count >= expected_count THEN
                RAISE EXCEPTION
                    'pet-skill content exceeds its declared row count';
            END IF;
            RETURN NEW;
        END
        $body$;

        CREATE OR REPLACE FUNCTION public.reject_pet_skill_content_mutation()
        RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
            RAISE EXCEPTION 'pet-skill content rows are immutable';
        END
        $body$;

        CREATE TRIGGER trg_pet_skill_curves_insert_guard
        BEFORE INSERT ON public.pet_skill_curve_definitions
        FOR EACH ROW EXECUTE FUNCTION
            public.guard_pet_skill_content_rows();
        CREATE TRIGGER trg_pet_skill_steps_insert_guard
        BEFORE INSERT ON public.pet_skill_curve_steps
        FOR EACH ROW EXECUTE FUNCTION
            public.guard_pet_skill_content_rows();
        CREATE TRIGGER trg_pet_skill_curves_immutable
        BEFORE UPDATE OR DELETE ON public.pet_skill_curve_definitions
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_pet_skill_content_mutation();
        CREATE TRIGGER trg_pet_skill_steps_immutable
        BEFORE UPDATE OR DELETE ON public.pet_skill_curve_steps
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_pet_skill_content_mutation();

        CREATE OR REPLACE FUNCTION public.guard_pet_skill_content_revision()
        RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
            IF OLD.sealed_at IS NULL AND NEW.sealed_at IS NOT NULL AND
               NEW.revision = OLD.revision AND
               NEW.curve_count = OLD.curve_count AND
               NEW.step_count = OLD.step_count AND
               NEW.source = OLD.source AND
               NEW.source_sha256 = OLD.source_sha256 AND
               NEW.created_at = OLD.created_at AND
               (SELECT count(*) FROM public.pet_skill_curve_definitions
                WHERE revision = NEW.revision) = NEW.curve_count AND
               (SELECT count(*) FROM public.pet_skill_curve_steps
                WHERE revision = NEW.revision) = NEW.step_count THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION 'pet-skill content revisions are immutable';
        END
        $body$;

        CREATE TRIGGER trg_pet_skill_revision_guard
        BEFORE UPDATE OR DELETE ON public.pet_skill_content_revisions
        FOR EACH ROW EXECUTE FUNCTION
            public.guard_pet_skill_content_revision();

        CREATE OR REPLACE FUNCTION public.guard_pet_skill_publication()
        RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION
                    'pet-skill content publication cannot be deleted';
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM public.pet_skill_content_revisions revision
                WHERE revision.revision = NEW.revision
                  AND revision.sealed_at IS NOT NULL
                  AND (SELECT count(*)
                       FROM public.pet_skill_curve_definitions curve
                       WHERE curve.revision = NEW.revision) =
                      revision.curve_count
                  AND (SELECT count(*)
                       FROM public.pet_skill_curve_steps step
                       WHERE step.revision = NEW.revision) =
                      revision.step_count
            ) THEN
                RAISE EXCEPTION
                    'pet-skill content publication is incomplete';
            END IF;
            RETURN NEW;
        END
        $body$;

        CREATE TRIGGER trg_pet_skill_publication_guard
        BEFORE INSERT OR UPDATE OR DELETE
        ON public.pet_skill_content_publication
        FOR EACH ROW EXECUTE FUNCTION
            public.guard_pet_skill_publication();
        """);
}
