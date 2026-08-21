namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateTempestRealmAuthority() => new(
            "20260731_035_tempest_realm_authority",
            "Make the legacy Tempest server row authoritative as realm one",
            """
            DO $tempest_realm_preflight$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM public.server realm
                    WHERE realm.id = 1
                      AND realm.name = 'Tempest'
                      AND realm.identifier =
                          'KAL3jcIzqGgKvOf1dbYZKC8cS'
                ) THEN
                    RAISE EXCEPTION
                        'Tempest realm identity is missing or conflicts with realm id 1.'
                        USING ERRCODE = '23514';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.character_base character_row
                    WHERE character_row.server_id IS NOT NULL
                      AND character_row.server_id <> 1
                ) THEN
                    RAISE EXCEPTION
                        'Non-Tempest character realms require the realm-scoped lifecycle contract first.'
                        USING ERRCODE = '23514';
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint constraint_row
                    JOIN pg_attribute source_column
                      ON source_column.attrelid =
                            constraint_row.conrelid
                     AND source_column.attnum =
                            ANY(constraint_row.conkey)
                    JOIN pg_attribute target_column
                      ON target_column.attrelid =
                            constraint_row.confrelid
                     AND target_column.attnum =
                            ANY(constraint_row.confkey)
                    WHERE constraint_row.conrelid =
                            'public.character_base'::regclass
                      AND constraint_row.confrelid =
                            'public.server'::regclass
                      AND constraint_row.contype = 'f'
                      AND constraint_row.convalidated
                      AND source_column.attname = 'server_id'
                      AND target_column.attname = 'id'
                ) THEN
                    RAISE EXCEPTION
                        'character_base.server_id must retain its validated realm foreign key.'
                        USING ERRCODE = '23503';
                END IF;
            END
            $tempest_realm_preflight$;

            UPDATE public.character_base
            SET server_id = 1
            WHERE server_id IS NULL;

            ALTER TABLE public.character_base
                ALTER COLUMN server_id SET DEFAULT 1,
                ALTER COLUMN server_id SET NOT NULL,
                ADD CONSTRAINT ck_character_base_tempest_realm
                    CHECK (server_id = 1);

            CREATE INDEX IF NOT EXISTS ix_character_base_server
                ON public.character_base (server_id);

            COMMENT ON TABLE public.server IS
                'Legacy-named authoritative logical realm catalog.';
            COMMENT ON COLUMN public.character_base.server_id IS
                'Authoritative home realm id; references public.server.';
            """);

    private static PostgresSchemaMigration
        CreateMultiRealmCharacterAuthority() => new(
            "20260820_094_multi_realm_character_authority",
            "Enable realm-scoped character slots for Tempest and Dwargon",
            """
            DO $multi_realm_preflight$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM public.server realm
                    WHERE realm.id = 1
                      AND realm.name = 'Tempest'
                      AND realm.identifier =
                          'KAL3jcIzqGgKvOf1dbYZKC8cS'
                ) THEN
                    RAISE EXCEPTION
                        'Tempest realm identity is missing or conflicts with realm id 1.'
                        USING ERRCODE = '23514';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.server realm
                    WHERE realm.id = 2
                      AND (
                          realm.name <> 'Dwargon'
                          OR realm.identifier <>
                              'DWG3jcIzqGgKvOf1dbYZKC8cS'
                      )
                ) THEN
                    RAISE EXCEPTION
                        'Dwargon realm identity conflicts with realm id 2.'
                        USING ERRCODE = '23514';
                END IF;
            END
            $multi_realm_preflight$;

            ALTER TABLE public.server
                ADD COLUMN enabled boolean NOT NULL DEFAULT false,
                ADD COLUMN display_order integer NOT NULL DEFAULT 0,
                ADD COLUMN game_port integer NOT NULL DEFAULT 7000,
                ADD COLUMN recommended boolean NOT NULL DEFAULT false,
                ADD CONSTRAINT ck_server_display_order CHECK (
                    display_order >= 0
                ),
                ADD CONSTRAINT ck_server_game_port CHECK (
                    game_port BETWEEN 1 AND 65535
                );

            UPDATE public.server
            SET enabled = true,
                display_order = 1,
                game_port = 7000,
                recommended = true
            WHERE id = 1;

            INSERT INTO public.server (
                id,
                name,
                identifier,
                ip_address,
                server_limit,
                enabled,
                display_order,
                game_port,
                recommended
            )
            VALUES (
                2,
                'Dwargon',
                'DWG3jcIzqGgKvOf1dbYZKC8cS',
                '0.0.0.0',
                250,
                false,
                2,
                7000,
                false
            )
            ON CONFLICT (id) DO NOTHING;

            SELECT setval(
                pg_get_serial_sequence('public.server', 'id'),
                (SELECT max(id) FROM public.server),
                true
            );

            CREATE TABLE public.account_realm (
                account_id integer NOT NULL
                    REFERENCES public.accounts(id) ON DELETE CASCADE,
                realm_id integer NOT NULL
                    REFERENCES public.server(id),
                character_lifecycle_version bigint NOT NULL DEFAULT 0,
                character_slot_limit smallint NOT NULL DEFAULT 1,
                created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
                PRIMARY KEY (account_id, realm_id),
                CONSTRAINT ck_account_realm_lifecycle_version CHECK (
                    character_lifecycle_version >= 0
                ),
                CONSTRAINT ck_account_realm_character_slot_limit CHECK (
                    character_slot_limit BETWEEN 1 AND 16
                )
            );

            INSERT INTO public.account_realm (
                account_id,
                realm_id,
                character_lifecycle_version
            )
            SELECT
                account_row.id,
                1,
                account_row.character_lifecycle_version
            FROM public.accounts account_row;

            ALTER TABLE public.character_base
                DROP CONSTRAINT ck_character_base_tempest_realm,
                ALTER COLUMN server_id DROP DEFAULT;

            DROP INDEX public.ux_character_base_active_account_slot;
            CREATE UNIQUE INDEX ux_character_base_active_account_realm_slot
                ON public.character_base (
                    account_id,
                    server_id,
                    character_slot
                )
                WHERE lifecycle_state = 'active';

            DROP INDEX public.ix_character_base_deleted_account_slot;
            CREATE INDEX ix_character_base_deleted_account_realm_slot
                ON public.character_base (
                    account_id,
                    server_id,
                    character_slot,
                    deleted_at DESC,
                    id
                )
                WHERE lifecycle_state = 'deleted';

            CREATE INDEX ix_character_base_account_realm
                ON public.character_base (account_id, server_id);

            COMMENT ON TABLE public.account_realm IS
                'Per-account, per-realm character lifecycle authority.';
            COMMENT ON COLUMN public.accounts.character_lifecycle_version IS
                'Legacy Tempest lifecycle mirror; account_realm is authoritative.';
            COMMENT ON COLUMN public.character_base.server_id IS
                'Required authoritative home realm; callers must supply it explicitly.';

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
                           AND NEW.consumer_key IN (
                               'character_lifecycle_v1',
                               'character_lifecycle_v2'
                           )
                           AND NEW.aggregate_type IN (
                               'account_character_slot',
                               'account_realm_character_slot'
                           )
                           AND NEW.ordering_policy = 'strict'
                           AND EXISTS (
                               SELECT 1
                               FROM public.account_realm membership
                               WHERE membership
                                   .character_lifecycle_version =
                                       NEW.current_version + 1
                                 AND NEW.consumer_key = CASE
                                     WHEN membership.realm_id = 1
                                         THEN 'character_lifecycle_v1'
                                     ELSE 'character_lifecycle_v2'
                                 END
                                 AND NEW.aggregate_type = CASE
                                     WHEN membership.realm_id = 1
                                         THEN 'account_character_slot'
                                     ELSE 'account_realm_character_slot'
                                 END
                                 AND NEW.aggregate_key = CASE
                                     WHEN membership.realm_id = 1 THEN
                                         membership.account_id::text || ':0'
                                     ELSE
                                         membership.account_id::text || ':' ||
                                         membership.realm_id::text || ':0'
                                 END
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
