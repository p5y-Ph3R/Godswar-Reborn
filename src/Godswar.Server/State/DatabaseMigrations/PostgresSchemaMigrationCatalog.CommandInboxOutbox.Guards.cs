namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string CommandInboxOutboxGuardsSql =
        """
        CREATE OR REPLACE FUNCTION
            public.reject_command_audit_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_command_audit_mutation$
        BEGIN
            RAISE EXCEPTION
                'Command audit rows are permanently retained and immutable.';
        END;
        $reject_command_audit_mutation$;

        CREATE TRIGGER trg_command_audit_immutable
        BEFORE UPDATE OR DELETE ON public.command_audit
        FOR EACH ROW
        EXECUTE FUNCTION public.reject_command_audit_mutation();

        CREATE OR REPLACE FUNCTION
            public.guard_command_inbox_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_command_inbox_mutation$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION
                    'Command inbox rows are permanently retained.';
            END IF;

            IF (
                NEW.id,
                NEW.principal_type,
                NEW.principal_key,
                NEW.aggregate_type,
                NEW.aggregate_key,
                NEW.command_family,
                NEW.operation_id,
                NEW.request_hash,
                NEW.result_contract_version,
                NEW.result_code,
                NEW.result_payload,
                NEW.result_hash,
                NEW.audit_id,
                NEW.retention_policy,
                NEW.completed_at
            ) IS DISTINCT FROM (
                OLD.id,
                OLD.principal_type,
                OLD.principal_key,
                OLD.aggregate_type,
                OLD.aggregate_key,
                OLD.command_family,
                OLD.operation_id,
                OLD.request_hash,
                OLD.result_contract_version,
                OLD.result_code,
                OLD.result_payload,
                OLD.result_hash,
                OLD.audit_id,
                OLD.retention_policy,
                OLD.completed_at
            ) THEN
                RAISE EXCEPTION
                    'Command inbox identity and result are immutable.';
            END IF;

            IF NEW.duplicate_count < OLD.duplicate_count
               OR NEW.request_conflict_count
                    < OLD.request_conflict_count THEN
                RAISE EXCEPTION
                    'Command inbox counters cannot decrease.';
            END IF;

            RETURN NEW;
        END;
        $guard_command_inbox_mutation$;

        CREATE TRIGGER trg_command_inbox_guard
        BEFORE UPDATE OR DELETE ON public.command_inbox
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_command_inbox_mutation();

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

            IF NEW.attempt_count < OLD.attempt_count THEN
                RAISE EXCEPTION
                    'Outbox attempt count cannot decrease.';
            END IF;

            RETURN NEW;
        END;
        $guard_outbox_event_mutation$;

        CREATE TRIGGER trg_outbox_events_guard
        BEFORE UPDATE OR DELETE ON public.outbox_events
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_outbox_event_mutation();

        CREATE OR REPLACE FUNCTION
            public.guard_outbox_consumer_position()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_outbox_consumer_position$
        BEGIN
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

            RETURN NEW;
        END;
        $guard_outbox_consumer_position$;

        CREATE TRIGGER trg_outbox_consumer_positions_guard
        BEFORE UPDATE ON public.outbox_consumer_positions
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_outbox_consumer_position();
        """;
}
