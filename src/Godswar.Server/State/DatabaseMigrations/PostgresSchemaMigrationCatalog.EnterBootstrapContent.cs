namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateEnterBootstrapContentRelease() => new(
            "20260801_037_enter_bootstrap_content_release",
            "Create immutable, versioned post-enter bootstrap content and one publication pointer",
            """
            CREATE TABLE public.enter_bootstrap_revisions (
                revision varchar(64) PRIMARY KEY,
                packet_count integer NOT NULL,
                total_bytes integer NOT NULL,
                source varchar(96) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT ck_enter_bootstrap_revisions_revision
                    CHECK (revision ~ '^[0-9A-F]{64}$'),
                CONSTRAINT ck_enter_bootstrap_revisions_packet_count
                    CHECK (packet_count BETWEEN 0 AND 256),
                CONSTRAINT ck_enter_bootstrap_revisions_total_bytes
                    CHECK (total_bytes BETWEEN 0 AND 262144),
                CONSTRAINT ck_enter_bootstrap_revisions_source
                    CHECK (btrim(source) <> '')
            );

            CREATE TABLE public.enter_bootstrap_packets (
                revision varchar(64) NOT NULL,
                sequence smallint NOT NULL,
                opcode integer NOT NULL,
                clear_bytes bytea NOT NULL,
                CONSTRAINT pk_enter_bootstrap_packets
                    PRIMARY KEY (revision, sequence),
                CONSTRAINT fk_enter_bootstrap_packets_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.enter_bootstrap_revisions (revision)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_enter_bootstrap_packets_sequence
                    CHECK (sequence BETWEEN 0 AND 255),
                CONSTRAINT ck_enter_bootstrap_packets_opcode
                    CHECK (opcode BETWEEN 0 AND 65535),
                CONSTRAINT ck_enter_bootstrap_packets_clear_bytes
                    CHECK (octet_length(clear_bytes) BETWEEN 4 AND 65535)
            );

            CREATE TABLE public.enter_bootstrap_publication (
                family varchar(32) PRIMARY KEY,
                revision varchar(64) NOT NULL,
                published_at timestamptz NOT NULL DEFAULT now(),
                publisher varchar(64) NOT NULL,
                CONSTRAINT ck_enter_bootstrap_publication_family
                    CHECK (family = 'enter-bootstrap'),
                CONSTRAINT ck_enter_bootstrap_publication_publisher
                    CHECK (btrim(publisher) <> ''),
                CONSTRAINT fk_enter_bootstrap_publication_revision
                    FOREIGN KEY (revision)
                    REFERENCES public.enter_bootstrap_revisions (revision)
                    ON DELETE RESTRICT
            );

            CREATE OR REPLACE FUNCTION public.reject_immutable_enter_bootstrap_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $reject_immutable_enter_bootstrap_mutation$
            BEGIN
                RAISE EXCEPTION
                    'Enter-bootstrap revisions and packets are immutable; publish a new revision instead.';
            END;
            $reject_immutable_enter_bootstrap_mutation$;

            CREATE TRIGGER trg_enter_bootstrap_revisions_immutable
            BEFORE UPDATE OR DELETE ON public.enter_bootstrap_revisions
            FOR EACH ROW
            EXECUTE FUNCTION public.reject_immutable_enter_bootstrap_mutation();

            CREATE TRIGGER trg_enter_bootstrap_packets_immutable
            BEFORE UPDATE OR DELETE ON public.enter_bootstrap_packets
            FOR EACH ROW
            EXECUTE FUNCTION public.reject_immutable_enter_bootstrap_mutation();

            CREATE OR REPLACE FUNCTION public.guard_enter_bootstrap_packet_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_enter_bootstrap_packet_insert$
            DECLARE
                declared_packet_count integer;
                declared_total_bytes integer;
                stored_packet_count integer;
                stored_total_bytes integer;
            BEGIN
                SELECT packet_count, total_bytes
                INTO STRICT declared_packet_count, declared_total_bytes
                FROM public.enter_bootstrap_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;

                SELECT COUNT(*)::integer,
                       COALESCE(SUM(octet_length(clear_bytes)), 0)::integer
                INTO stored_packet_count, stored_total_bytes
                FROM public.enter_bootstrap_packets
                WHERE revision = NEW.revision;

                IF stored_packet_count >= declared_packet_count OR
                   stored_total_bytes + octet_length(NEW.clear_bytes) >
                       declared_total_bytes THEN
                    RAISE EXCEPTION
                        'Enter-bootstrap revision % exceeds its declared bounds.',
                        NEW.revision;
                END IF;

                RETURN NEW;
            END;
            $guard_enter_bootstrap_packet_insert$;

            CREATE TRIGGER trg_enter_bootstrap_packets_bounded_insert
            BEFORE INSERT ON public.enter_bootstrap_packets
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_enter_bootstrap_packet_insert();

            CREATE OR REPLACE FUNCTION public.validate_enter_bootstrap_publication()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $validate_enter_bootstrap_publication$
            DECLARE
                declared_packet_count integer;
                declared_total_bytes integer;
                stored_packet_count integer;
                stored_total_bytes integer;
            BEGIN
                SELECT packet_count, total_bytes
                INTO STRICT declared_packet_count, declared_total_bytes
                FROM public.enter_bootstrap_revisions
                WHERE revision = NEW.revision
                FOR UPDATE;

                SELECT COUNT(*)::integer,
                       COALESCE(SUM(octet_length(clear_bytes)), 0)::integer
                INTO stored_packet_count, stored_total_bytes
                FROM public.enter_bootstrap_packets
                WHERE revision = NEW.revision;

                IF stored_packet_count <> declared_packet_count OR
                   stored_total_bytes <> declared_total_bytes THEN
                    RAISE EXCEPTION
                        'Enter-bootstrap revision % does not match its declared packet and byte counts.',
                        NEW.revision;
                END IF;

                RETURN NEW;
            END;
            $validate_enter_bootstrap_publication$;

            CREATE TRIGGER trg_enter_bootstrap_publication_complete
            BEFORE INSERT OR UPDATE ON public.enter_bootstrap_publication
            FOR EACH ROW
            EXECUTE FUNCTION public.validate_enter_bootstrap_publication();

            CREATE OR REPLACE FUNCTION public.reject_enter_bootstrap_publication_delete()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $reject_enter_bootstrap_publication_delete$
            BEGIN
                RAISE EXCEPTION
                    'The enter-bootstrap publication pointer cannot be deleted; publish or roll back to another revision.';
            END;
            $reject_enter_bootstrap_publication_delete$;

            CREATE TRIGGER trg_enter_bootstrap_publication_no_delete
            BEFORE DELETE ON public.enter_bootstrap_publication
            FOR EACH ROW
            EXECUTE FUNCTION public.reject_enter_bootstrap_publication_delete();
            """);
}
