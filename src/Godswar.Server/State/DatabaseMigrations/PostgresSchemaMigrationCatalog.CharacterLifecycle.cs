namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateCharacterLifecycleFoundation() => new(
            "20260730_031_character_lifecycle_foundation",
            "Add recoverable single-slot character lifecycle state",
            """
            DO $character_slot_preflight$
            BEGIN
                IF EXISTS (
                    SELECT existing.account_id
                    FROM public.character_base existing
                    GROUP BY existing.account_id
                    HAVING count(*) > 1
                ) THEN
                    RAISE EXCEPTION
                        'Cannot enable SingleCharacterV1: an account owns multiple active characters.'
                        USING ERRCODE = '23505';
                END IF;
            END
            $character_slot_preflight$;

            ALTER TABLE public.accounts
                ADD COLUMN character_lifecycle_version bigint
                    NOT NULL DEFAULT 0,
                ADD CONSTRAINT ck_accounts_character_lifecycle_version
                    CHECK (character_lifecycle_version >= 0);

            UPDATE public.accounts account_row
            SET character_lifecycle_version = 1
            WHERE EXISTS (
                SELECT 1
                FROM public.character_base character_row
                WHERE character_row.account_id = account_row.id
            );

            ALTER TABLE public.character_base
                ADD COLUMN character_slot smallint NOT NULL DEFAULT 0,
                ADD COLUMN lifecycle_state varchar(16)
                    NOT NULL DEFAULT 'active',
                ADD COLUMN lifecycle_version bigint NOT NULL DEFAULT 1,
                ADD COLUMN deleted_at timestamptz,
                ADD COLUMN restore_until timestamptz,
                ADD COLUMN purge_after timestamptz,
                ADD CONSTRAINT ck_character_base_character_slot
                    CHECK (character_slot = 0),
                ADD CONSTRAINT ck_character_base_lifecycle_state
                    CHECK (lifecycle_state IN ('active', 'deleted')),
                ADD CONSTRAINT ck_character_base_lifecycle_version
                    CHECK (lifecycle_version >= 1),
                ADD CONSTRAINT ck_character_base_lifecycle_timestamps
                    CHECK (
                        (
                            lifecycle_state = 'active'
                            AND deleted_at IS NULL
                            AND restore_until IS NULL
                            AND purge_after IS NULL
                        )
                        OR (
                            lifecycle_state = 'deleted'
                            AND deleted_at IS NOT NULL
                            AND restore_until IS NOT NULL
                            AND purge_after IS NOT NULL
                            AND deleted_at >= "Register_time"
                            AND deleted_at < restore_until
                            AND restore_until <= purge_after
                        )
                    ),
                ADD CONSTRAINT ck_character_base_deleted_owner
                    CHECK (
                        lifecycle_state <> 'deleted'
                        OR checkpoint_owner_id IS NULL
                    );

            CREATE UNIQUE INDEX ux_character_base_active_account_slot
                ON public.character_base (account_id, character_slot)
                WHERE lifecycle_state = 'active';

            CREATE INDEX ix_character_base_deleted_account_slot
                ON public.character_base (
                    account_id,
                    character_slot,
                    deleted_at DESC,
                    id
                )
                WHERE lifecycle_state = 'deleted';

            CREATE INDEX ix_character_base_purge_due
                ON public.character_base (purge_after, id)
                WHERE lifecycle_state = 'deleted';

            -- Strict streams normally begin at zero. Character lifecycle is
            -- the narrow exception: an account may already own an active
            -- character before this event stream first observes it. Permit
            -- exactly one idle baseline at the immediately preceding
            -- account lifecycle version, before any stream event exists.
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
                    IF NEW.inflight_event_id IS NOT NULL
                       OR NEW.inflight_version IS NOT NULL
                       OR NEW.lease_owner IS NOT NULL
                       OR NEW.lease_token IS NOT NULL
                       OR NEW.lease_expires_at IS NOT NULL THEN
                        RAISE EXCEPTION
                            'New outbox consumer positions must start idle.';
                    END IF;

                    IF NEW.current_version <> 0
                       AND NOT (
                           NEW.current_version > 0
                           AND NEW.consumer_key =
                               'character_lifecycle_v1'
                           AND NEW.aggregate_type =
                               'account_character_slot'
                           AND NEW.ordering_policy = 'strict'
                           AND EXISTS (
                               SELECT 1
                               FROM public.accounts account_row
                               WHERE NEW.aggregate_key =
                                   account_row.id::text || ':0'
                                 AND account_row
                                     .character_lifecycle_version =
                                     NEW.current_version + 1
                           )
                           AND NOT EXISTS (
                               SELECT 1
                               FROM public.outbox_events event_row
                               WHERE event_row.consumer_key =
                                   NEW.consumer_key
                                 AND event_row.aggregate_type =
                                   NEW.aggregate_type
                                 AND event_row.aggregate_key =
                                   NEW.aggregate_key
                           )
                       ) THEN
                        RAISE EXCEPTION
                            'New outbox consumer positions must start idle at zero or an authorized lifecycle baseline.';
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
            """);
}
