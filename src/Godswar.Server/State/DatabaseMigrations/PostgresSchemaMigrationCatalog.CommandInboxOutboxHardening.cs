namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateCommandInboxOutboxHardening() => new(
            "20260729_026_command_inbox_outbox_hardening",
            "Harden outbox identities, state transitions, leases, and checkpoints",
            """
            ALTER TABLE public.command_audit
                ADD CONSTRAINT ck_command_audit_aggregate_key_no_control
                    CHECK (aggregate_key !~ '[[:cntrl:]]');

            ALTER TABLE public.command_inbox
                ADD CONSTRAINT ck_command_inbox_aggregate_key_no_control
                    CHECK (aggregate_key !~ '[[:cntrl:]]');

            ALTER TABLE public.outbox_events
                ADD CONSTRAINT ck_outbox_events_event_id_not_empty
                    CHECK (
                        event_id <>
                            '00000000-0000-0000-0000-000000000000'::uuid
                    ),
                ADD CONSTRAINT ck_outbox_events_aggregate_key_no_control
                    CHECK (aggregate_key !~ '[[:cntrl:]]');

            ALTER TABLE public.outbox_consumer_positions
                ADD CONSTRAINT
                    ck_outbox_positions_aggregate_key_no_control
                    CHECK (aggregate_key !~ '[[:cntrl:]]');

            CREATE OR REPLACE FUNCTION
                public.guard_outbox_event_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_outbox_event_mutation$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION
                        'Outbox events cannot be deleted.';
                END IF;

                IF TG_OP = 'INSERT' THEN
                    IF NEW.attempt_count <> 0
                       OR NEW.lease_owner IS NOT NULL
                       OR NEW.lease_token IS NOT NULL
                       OR NEW.lease_expires_at IS NOT NULL
                       OR NEW.delivered_at IS NOT NULL
                       OR NEW.poisoned_at IS NOT NULL
                       OR NEW.poison_reason IS NOT NULL THEN
                        RAISE EXCEPTION
                            'New outbox events must start pending and unleased.';
                    END IF;

                    RETURN NEW;
                END IF;

                IF (
                    NEW.id,
                    NEW.event_id,
                    NEW.command_inbox_id,
                    NEW.consumer_key,
                    NEW.aggregate_type,
                    NEW.aggregate_key,
                    NEW.aggregate_version,
                    NEW.event_type,
                    NEW.contract_version,
                    NEW.ordering_policy,
                    NEW.payload,
                    NEW.max_attempts,
                    NEW.created_at
                ) IS DISTINCT FROM (
                    OLD.id,
                    OLD.event_id,
                    OLD.command_inbox_id,
                    OLD.consumer_key,
                    OLD.aggregate_type,
                    OLD.aggregate_key,
                    OLD.aggregate_version,
                    OLD.event_type,
                    OLD.contract_version,
                    OLD.ordering_policy,
                    OLD.payload,
                    OLD.max_attempts,
                    OLD.created_at
                ) THEN
                    RAISE EXCEPTION
                        'Outbox event identity and payload are immutable.';
                END IF;

                IF OLD.delivered_at IS NOT NULL
                   OR OLD.poisoned_at IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Terminal outbox events are immutable.';
                END IF;

                IF NEW.attempt_count < OLD.attempt_count THEN
                    RAISE EXCEPTION
                        'Outbox attempt count cannot decrease.';
                END IF;

                IF NEW.attempt_count <> OLD.attempt_count
                   AND (
                       NEW.attempt_count <> OLD.attempt_count + 1
                       OR OLD.lease_token IS NOT NULL
                       OR NEW.lease_token IS NULL
                   ) THEN
                    RAISE EXCEPTION
                        'Outbox attempts advance once when a lease is acquired.';
                END IF;

                IF OLD.lease_token IS NULL
                   AND NEW.lease_token IS NOT NULL
                   AND NEW.attempt_count <> OLD.attempt_count + 1 THEN
                    RAISE EXCEPTION
                        'An outbox lease must consume exactly one attempt.';
                END IF;

                IF OLD.lease_token IS NOT NULL
                   AND NEW.lease_token IS NOT NULL
                   AND (
                       NEW.lease_owner,
                       NEW.lease_token,
                       NEW.lease_expires_at
                   ) IS DISTINCT FROM (
                       OLD.lease_owner,
                       OLD.lease_token,
                       OLD.lease_expires_at
                   ) THEN
                    RAISE EXCEPTION
                        'An active outbox lease cannot be retargeted.';
                END IF;

                RETURN NEW;
            END;
            $guard_outbox_event_mutation$;

            DROP TRIGGER trg_outbox_events_guard
                ON public.outbox_events;

            CREATE TRIGGER trg_outbox_events_guard
            BEFORE INSERT OR UPDATE OR DELETE ON public.outbox_events
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_outbox_event_mutation();

            CREATE OR REPLACE FUNCTION
                public.guard_outbox_consumer_position()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_outbox_consumer_position$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION
                        'Outbox consumer positions cannot be deleted.';
                END IF;

                IF TG_OP = 'INSERT' THEN
                    IF NEW.current_version <> 0
                       OR NEW.inflight_event_id IS NOT NULL
                       OR NEW.inflight_version IS NOT NULL
                       OR NEW.lease_owner IS NOT NULL
                       OR NEW.lease_token IS NOT NULL
                       OR NEW.lease_expires_at IS NOT NULL THEN
                        RAISE EXCEPTION
                            'New outbox consumer positions must start idle at zero.';
                    END IF;

                    RETURN NEW;
                END IF;

                IF (
                    NEW.consumer_key,
                    NEW.aggregate_type,
                    NEW.aggregate_key,
                    NEW.ordering_policy
                ) IS DISTINCT FROM (
                    OLD.consumer_key,
                    OLD.aggregate_type,
                    OLD.aggregate_key,
                    OLD.ordering_policy
                ) THEN
                    RAISE EXCEPTION
                        'Outbox consumer position identity is immutable.';
                END IF;

                IF NEW.current_version < OLD.current_version THEN
                    RAISE EXCEPTION
                        'Outbox consumer position cannot move backwards.';
                END IF;

                IF OLD.inflight_event_id IS NULL
                   AND NEW.current_version <> OLD.current_version THEN
                    RAISE EXCEPTION
                        'An idle outbox consumer position cannot advance.';
                END IF;

                IF OLD.inflight_event_id IS NOT NULL
                   AND NEW.inflight_event_id IS NULL
                   AND NEW.current_version NOT IN (
                       OLD.current_version,
                       OLD.inflight_version
                   ) THEN
                    RAISE EXCEPTION
                        'An outbox checkpoint must use its inflight version.';
                END IF;

                IF OLD.inflight_event_id IS NOT NULL
                   AND NEW.inflight_event_id IS NOT NULL
                   AND (
                       NEW.current_version,
                       NEW.inflight_event_id,
                       NEW.inflight_version,
                       NEW.lease_owner,
                       NEW.lease_token,
                       NEW.lease_expires_at
                   ) IS DISTINCT FROM (
                       OLD.current_version,
                       OLD.inflight_event_id,
                       OLD.inflight_version,
                       OLD.lease_owner,
                       OLD.lease_token,
                       OLD.lease_expires_at
                   ) THEN
                    RAISE EXCEPTION
                        'An active outbox position lease cannot be retargeted.';
                END IF;

                RETURN NEW;
            END;
            $guard_outbox_consumer_position$;

            DROP TRIGGER trg_outbox_consumer_positions_guard
                ON public.outbox_consumer_positions;

            CREATE TRIGGER trg_outbox_consumer_positions_guard
            BEFORE INSERT OR UPDATE OR DELETE
                ON public.outbox_consumer_positions
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_outbox_consumer_position();
            """ + "\n\n" + CommandInboxOutboxLeaseConsistencySql);
}
