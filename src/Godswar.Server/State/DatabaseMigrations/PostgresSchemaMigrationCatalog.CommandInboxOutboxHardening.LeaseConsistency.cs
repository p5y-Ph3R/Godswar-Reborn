namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string CommandInboxOutboxLeaseConsistencySql =
        """
        DO $validate_outbox_lease_consistency$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM public.outbox_events AS outbox_event
                LEFT JOIN public.outbox_consumer_positions AS position
                  ON position.inflight_event_id = outbox_event.id
                 AND position.consumer_key = outbox_event.consumer_key
                 AND position.aggregate_type = outbox_event.aggregate_type
                 AND position.aggregate_key = outbox_event.aggregate_key
                 AND position.inflight_version =
                        outbox_event.aggregate_version
                 AND position.ordering_policy =
                        outbox_event.ordering_policy
                 AND position.lease_owner = outbox_event.lease_owner
                 AND position.lease_token = outbox_event.lease_token
                 AND position.lease_expires_at =
                        outbox_event.lease_expires_at
                WHERE outbox_event.lease_token IS NOT NULL
                  AND position.inflight_event_id IS NULL
            ) OR EXISTS (
                SELECT 1
                FROM public.outbox_consumer_positions AS position
                LEFT JOIN public.outbox_events AS outbox_event
                  ON outbox_event.id = position.inflight_event_id
                 AND outbox_event.consumer_key = position.consumer_key
                 AND outbox_event.aggregate_type = position.aggregate_type
                 AND outbox_event.aggregate_key = position.aggregate_key
                 AND outbox_event.aggregate_version =
                        position.inflight_version
                 AND outbox_event.ordering_policy =
                        position.ordering_policy
                 AND outbox_event.lease_owner = position.lease_owner
                 AND outbox_event.lease_token = position.lease_token
                 AND outbox_event.lease_expires_at =
                        position.lease_expires_at
                 AND outbox_event.delivered_at IS NULL
                 AND outbox_event.poisoned_at IS NULL
                WHERE position.inflight_event_id IS NOT NULL
                  AND outbox_event.id IS NULL
            ) OR EXISTS (
                SELECT 1
                FROM public.outbox_events AS outbox_event
                LEFT JOIN public.outbox_consumer_positions AS position
                  ON position.consumer_key = outbox_event.consumer_key
                 AND position.aggregate_type = outbox_event.aggregate_type
                 AND position.aggregate_key = outbox_event.aggregate_key
                 AND position.ordering_policy =
                        outbox_event.ordering_policy
                 AND position.current_version >=
                        outbox_event.aggregate_version
                WHERE outbox_event.delivered_at IS NOT NULL
                  AND position.consumer_key IS NULL
            ) OR EXISTS (
                SELECT 1
                FROM public.outbox_consumer_positions AS position
                WHERE position.current_version > 0
                  AND NOT EXISTS (
                      SELECT 1
                      FROM public.outbox_events AS outbox_event
                      WHERE outbox_event.consumer_key =
                                position.consumer_key
                        AND outbox_event.aggregate_type =
                                position.aggregate_type
                        AND outbox_event.aggregate_key =
                                position.aggregate_key
                        AND outbox_event.ordering_policy =
                                position.ordering_policy
                        AND outbox_event.aggregate_version =
                                position.current_version
                        AND outbox_event.delivered_at IS NOT NULL
                        AND outbox_event.poisoned_at IS NULL
                  )
            ) OR EXISTS (
                SELECT 1
                FROM public.outbox_events AS outbox_event
                WHERE outbox_event.lease_token IS NOT NULL
                  AND outbox_event.attempt_count = 0
            ) THEN
                RAISE EXCEPTION
                    'Existing outbox lease or checkpoint state is inconsistent.';
            END IF;
        END;
        $validate_outbox_lease_consistency$;

        CREATE OR REPLACE FUNCTION
            public.guard_outbox_lease_consistency()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_outbox_lease_consistency$
        BEGIN
            IF TG_TABLE_NAME = 'outbox_events' THEN
                IF EXISTS (
                    SELECT 1
                    FROM public.outbox_events AS outbox_event
                    WHERE outbox_event.id = NEW.id
                      AND (
                          (
                              outbox_event.lease_token IS NOT NULL
                              AND NOT EXISTS (
                                  SELECT 1
                                  FROM public.outbox_consumer_positions
                                      AS position
                                  WHERE position.inflight_event_id =
                                            outbox_event.id
                                    AND position.consumer_key =
                                            outbox_event.consumer_key
                                    AND position.aggregate_type =
                                            outbox_event.aggregate_type
                                    AND position.aggregate_key =
                                            outbox_event.aggregate_key
                                    AND position.inflight_version =
                                            outbox_event.aggregate_version
                                    AND position.ordering_policy =
                                            outbox_event.ordering_policy
                                    AND position.lease_owner =
                                            outbox_event.lease_owner
                                    AND position.lease_token =
                                            outbox_event.lease_token
                                    AND position.lease_expires_at =
                                            outbox_event.lease_expires_at
                              )
                          )
                          OR (
                              outbox_event.lease_token IS NULL
                              AND EXISTS (
                                  SELECT 1
                                  FROM public.outbox_consumer_positions
                                      AS position
                                  WHERE position.inflight_event_id =
                                            outbox_event.id
                                    AND position.consumer_key =
                                            outbox_event.consumer_key
                                    AND position.aggregate_type =
                                            outbox_event.aggregate_type
                                    AND position.aggregate_key =
                                            outbox_event.aggregate_key
                              )
                          )
                          OR (
                              outbox_event.delivered_at IS NOT NULL
                              AND NOT EXISTS (
                                  SELECT 1
                                  FROM public.outbox_consumer_positions
                                      AS position
                                  WHERE position.consumer_key =
                                            outbox_event.consumer_key
                                    AND position.aggregate_type =
                                            outbox_event.aggregate_type
                                    AND position.aggregate_key =
                                            outbox_event.aggregate_key
                                    AND position.ordering_policy =
                                            outbox_event.ordering_policy
                                    AND position.inflight_event_id IS NULL
                                    AND position.current_version >=
                                            outbox_event.aggregate_version
                              )
                          )
                      )
                ) THEN
                    RAISE EXCEPTION
                        'Outbox event and position final state must match.';
                END IF;
            ELSE
                IF TG_OP = 'UPDATE'
                   AND NEW.current_version > OLD.current_version
                   AND (
                       OLD.inflight_event_id IS NULL
                       OR NOT EXISTS (
                           SELECT 1
                           FROM public.outbox_events AS outbox_event
                           WHERE outbox_event.id =
                                    OLD.inflight_event_id
                             AND outbox_event.delivered_at IS NOT NULL
                             AND outbox_event.poisoned_at IS NULL
                       )
                   ) THEN
                    RAISE EXCEPTION
                        'An outbox checkpoint requires a delivered inflight event.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.outbox_consumer_positions AS position
                    WHERE position.consumer_key = NEW.consumer_key
                      AND position.aggregate_type = NEW.aggregate_type
                      AND position.aggregate_key = NEW.aggregate_key
                      AND (
                          (
                              position.inflight_event_id IS NOT NULL
                              AND NOT EXISTS (
                                  SELECT 1
                                  FROM public.outbox_events AS outbox_event
                                  WHERE outbox_event.id =
                                            position.inflight_event_id
                                    AND outbox_event.consumer_key =
                                            position.consumer_key
                                    AND outbox_event.aggregate_type =
                                            position.aggregate_type
                                    AND outbox_event.aggregate_key =
                                            position.aggregate_key
                                    AND outbox_event.aggregate_version =
                                            position.inflight_version
                                    AND outbox_event.ordering_policy =
                                            position.ordering_policy
                                    AND outbox_event.lease_owner =
                                            position.lease_owner
                                    AND outbox_event.lease_token =
                                            position.lease_token
                                    AND outbox_event.lease_expires_at =
                                            position.lease_expires_at
                                    AND outbox_event.delivered_at IS NULL
                                    AND outbox_event.poisoned_at IS NULL
                              )
                          )
                          OR (
                              position.inflight_event_id IS NULL
                              AND EXISTS (
                                  SELECT 1
                                  FROM public.outbox_events AS outbox_event
                                  WHERE outbox_event.consumer_key =
                                            position.consumer_key
                                    AND outbox_event.aggregate_type =
                                            position.aggregate_type
                                    AND outbox_event.aggregate_key =
                                            position.aggregate_key
                                    AND outbox_event.lease_token IS NOT NULL
                              )
                          )
                      )
                ) THEN
                    RAISE EXCEPTION
                        'Outbox position and event leases must match.';
                END IF;
            END IF;

            RETURN NULL;
        END;
        $guard_outbox_lease_consistency$;

        CREATE CONSTRAINT TRIGGER
            trg_outbox_events_lease_consistency
        AFTER INSERT OR UPDATE ON public.outbox_events
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_outbox_lease_consistency();

        CREATE CONSTRAINT TRIGGER
            trg_outbox_positions_lease_consistency
        AFTER INSERT OR UPDATE ON public.outbox_consumer_positions
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION public.guard_outbox_lease_consistency();
        """;
}
