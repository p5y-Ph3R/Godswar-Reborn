namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateEconomyLedgerFoundation() => new(
            "20260729_027_economy_ledger_foundation",
            "Create revisioned wallet and inventory evidence foundations",
            """
            ALTER TABLE public.character_base
                ADD COLUMN wallet_revision bigint NOT NULL DEFAULT 0,
                ADD COLUMN inventory_revision bigint NOT NULL DEFAULT 0,
                ADD CONSTRAINT ck_character_base_wallet_revision
                    CHECK (wallet_revision >= 0),
                ADD CONSTRAINT ck_character_base_inventory_revision
                    CHECK (inventory_revision >= 0),
                ADD CONSTRAINT ck_character_base_money_nonnegative
                    CHECK ("Money" BETWEEN 0 AND 2147483647),
                ADD CONSTRAINT ck_character_base_stone_nonnegative
                    CHECK ("Stone" BETWEEN 0 AND 2147483647);

            CREATE TABLE public.character_economy_baseline (
                character_id integer PRIMARY KEY,
                account_id integer NOT NULL,
                wallet_revision bigint NOT NULL DEFAULT 0,
                inventory_revision bigint NOT NULL DEFAULT 0,
                silver bigint NOT NULL,
                gold bigint NOT NULL,
                item_count integer NOT NULL,
                baseline_source varchar(64) NOT NULL,
                captured_at timestamptz NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT uq_character_economy_baseline_identity
                    UNIQUE (character_id, account_id),
                CONSTRAINT ck_character_economy_baseline_identity CHECK (
                    character_id <> 0
                    AND account_id <> 0
                ),
                CONSTRAINT ck_character_economy_baseline_revisions CHECK (
                    wallet_revision = 0
                    AND inventory_revision = 0
                ),
                CONSTRAINT ck_character_economy_baseline_balances CHECK (
                    silver BETWEEN 0 AND 2147483647
                    AND gold BETWEEN 0 AND 2147483647
                ),
                CONSTRAINT ck_character_economy_baseline_item_count CHECK (
                    item_count BETWEEN 0 AND 1000000
                ),
                CONSTRAINT ck_character_economy_baseline_source CHECK (
                    baseline_source ~ '^[a-z][a-z0-9_.-]{0,63}$'
                )
            );

            CREATE INDEX ix_character_economy_baseline_account
                ON public.character_economy_baseline (
                    account_id,
                    character_id
                );

            CREATE TABLE public.character_inventory_baseline_items (
                character_id integer NOT NULL,
                account_id integer NOT NULL,
                item_instance_id bigint NOT NULL,
                item_location smallint NOT NULL,
                slot_index smallint NOT NULL,
                prop_id integer NOT NULL,
                state_contract_version smallint NOT NULL DEFAULT 1,
                item_state jsonb NOT NULL,
                captured_at timestamptz NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT pk_character_inventory_baseline_items
                    PRIMARY KEY (character_id, item_instance_id),
                CONSTRAINT uq_character_inventory_baseline_item_instance
                    UNIQUE (item_instance_id),
                CONSTRAINT uq_character_inventory_baseline_slot
                    UNIQUE (
                        character_id,
                        item_location,
                        slot_index
                    ),
                CONSTRAINT fk_character_inventory_baseline_identity
                    FOREIGN KEY (character_id, account_id)
                    REFERENCES public.character_economy_baseline (
                        character_id,
                        account_id
                    )
                    ON DELETE RESTRICT,
                CONSTRAINT ck_character_inventory_baseline_identity CHECK (
                    character_id <> 0
                    AND account_id <> 0
                    AND item_instance_id <> 0
                    AND prop_id > 0
                ),
                CONSTRAINT ck_character_inventory_baseline_version CHECK (
                    state_contract_version BETWEEN 1 AND 32767
                ),
                CONSTRAINT ck_character_inventory_baseline_state CHECK (
                    jsonb_typeof(item_state) = 'object'
                    AND octet_length(item_state::text) <= 8192
                    AND item_state @> jsonb_build_object(
                        'id', item_instance_id,
                        'user_id', character_id,
                        'item_location', item_location,
                        'slot_index', slot_index,
                        'prop_id', prop_id
                    )
                )
            );

            CREATE TABLE public.character_currency_ledger (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                command_inbox_id bigint NOT NULL,
                account_id integer NOT NULL,
                character_id integer NOT NULL,
                wallet_revision bigint NOT NULL,
                currency_code varchar(16) NOT NULL,
                delta bigint NOT NULL,
                balance_before bigint NOT NULL,
                balance_after bigint NOT NULL,
                reason_code varchar(64) NOT NULL,
                created_at timestamptz NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT uq_character_currency_ledger_command_currency
                    UNIQUE (command_inbox_id, currency_code),
                CONSTRAINT uq_character_currency_ledger_revision_currency
                    UNIQUE (
                        character_id,
                        wallet_revision,
                        currency_code
                    ),
                CONSTRAINT fk_character_currency_ledger_inbox
                    FOREIGN KEY (command_inbox_id)
                    REFERENCES public.command_inbox (id)
                    ON DELETE RESTRICT,
                CONSTRAINT fk_character_currency_ledger_baseline
                    FOREIGN KEY (character_id, account_id)
                    REFERENCES public.character_economy_baseline (
                        character_id,
                        account_id
                    )
                    ON DELETE RESTRICT,
                CONSTRAINT ck_character_currency_ledger_identity CHECK (
                    account_id > 0
                    AND character_id > 0
                    AND wallet_revision > 0
                ),
                CONSTRAINT ck_character_currency_ledger_currency CHECK (
                    currency_code IN ('silver', 'gold')
                ),
                CONSTRAINT ck_character_currency_ledger_delta CHECK (
                    delta BETWEEN -2147483647 AND 2147483647
                    AND delta <> 0
                ),
                CONSTRAINT ck_character_currency_ledger_balances CHECK (
                    balance_before BETWEEN 0 AND 2147483647
                    AND balance_after BETWEEN 0 AND 2147483647
                    AND balance_after = balance_before + delta
                ),
                CONSTRAINT ck_character_currency_ledger_reason CHECK (
                    reason_code ~ '^[a-z][a-z0-9_.-]{0,63}$'
                )
            );

            CREATE INDEX ix_character_currency_ledger_character_time
                ON public.character_currency_ledger (
                    character_id,
                    created_at DESC,
                    id
                );

            CREATE TABLE public.character_inventory_ledger (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                command_inbox_id bigint NOT NULL,
                account_id integer NOT NULL,
                character_id integer NOT NULL,
                inventory_revision bigint NOT NULL,
                entry_ordinal smallint NOT NULL,
                item_instance_id bigint NOT NULL,
                mutation_kind varchar(16) NOT NULL,
                state_contract_version smallint NOT NULL DEFAULT 1,
                before_state jsonb,
                after_state jsonb,
                reason_code varchar(64) NOT NULL,
                created_at timestamptz NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT uq_character_inventory_ledger_command_ordinal
                    UNIQUE (command_inbox_id, entry_ordinal),
                CONSTRAINT uq_character_inventory_ledger_revision_ordinal
                    UNIQUE (
                        character_id,
                        inventory_revision,
                        entry_ordinal
                    ),
                CONSTRAINT uq_character_inventory_ledger_revision_item
                    UNIQUE (
                        character_id,
                        inventory_revision,
                        item_instance_id
                    ),
                CONSTRAINT fk_character_inventory_ledger_inbox
                    FOREIGN KEY (command_inbox_id)
                    REFERENCES public.command_inbox (id)
                    ON DELETE RESTRICT,
                CONSTRAINT fk_character_inventory_ledger_baseline
                    FOREIGN KEY (character_id, account_id)
                    REFERENCES public.character_economy_baseline (
                        character_id,
                        account_id
                    )
                    ON DELETE RESTRICT,
                CONSTRAINT ck_character_inventory_ledger_identity CHECK (
                    account_id > 0
                    AND character_id > 0
                    AND item_instance_id > 0
                    AND inventory_revision > 0
                ),
                CONSTRAINT ck_character_inventory_ledger_ordinal CHECK (
                    entry_ordinal BETWEEN 0 AND 255
                ),
                CONSTRAINT ck_character_inventory_ledger_mutation CHECK (
                    mutation_kind IN ('add', 'update', 'move', 'delete')
                    AND (
                        (
                            mutation_kind = 'add'
                            AND before_state IS NULL
                            AND after_state IS NOT NULL
                        )
                        OR (
                            mutation_kind IN ('update', 'move')
                            AND before_state IS NOT NULL
                            AND after_state IS NOT NULL
                            AND before_state IS DISTINCT FROM after_state
                        )
                        OR (
                            mutation_kind = 'delete'
                            AND before_state IS NOT NULL
                            AND after_state IS NULL
                        )
                    )
                ),
                CONSTRAINT ck_character_inventory_ledger_version CHECK (
                    state_contract_version BETWEEN 1 AND 32767
                ),
                CONSTRAINT ck_character_inventory_ledger_before_state CHECK (
                    before_state IS NULL
                    OR (
                        jsonb_typeof(before_state) = 'object'
                        AND octet_length(before_state::text) <= 8192
                        AND before_state @> jsonb_build_object(
                            'id', item_instance_id,
                            'user_id', character_id
                        )
                    )
                ),
                CONSTRAINT ck_character_inventory_ledger_after_state CHECK (
                    after_state IS NULL
                    OR (
                        jsonb_typeof(after_state) = 'object'
                        AND octet_length(after_state::text) <= 8192
                        AND after_state @> jsonb_build_object(
                            'id', item_instance_id,
                            'user_id', character_id
                        )
                    )
                ),
                CONSTRAINT ck_character_inventory_ledger_reason CHECK (
                    reason_code ~ '^[a-z][a-z0-9_.-]{0,63}$'
                )
            );

            CREATE INDEX ix_character_inventory_ledger_character_revision
                ON public.character_inventory_ledger (
                    character_id,
                    inventory_revision,
                    id
                );

            INSERT INTO public.character_economy_baseline (
                character_id,
                account_id,
                wallet_revision,
                inventory_revision,
                silver,
                gold,
                item_count,
                baseline_source,
                captured_at
            )
            SELECT
                character_row.id,
                character_row.account_id,
                0,
                0,
                character_row."Money"::bigint,
                character_row."Stone"::bigint,
                count(item_row.id)::integer,
                'migration_027',
                transaction_timestamp()
            FROM public.character_base character_row
            LEFT JOIN public.character_items item_row
              ON item_row.user_id = character_row.id
            GROUP BY
                character_row.id,
                character_row.account_id,
                character_row."Money",
                character_row."Stone";

            INSERT INTO public.character_inventory_baseline_items (
                character_id,
                account_id,
                item_instance_id,
                item_location,
                slot_index,
                prop_id,
                state_contract_version,
                item_state,
                captured_at
            )
            SELECT
                item_row.user_id,
                character_row.account_id,
                item_row.id,
                item_row.item_location,
                item_row.slot_index,
                item_row.prop_id,
                1,
                to_jsonb(item_row),
                baseline_row.captured_at
            FROM public.character_items item_row
            JOIN public.character_base character_row
              ON character_row.id = item_row.user_id
            JOIN public.character_economy_baseline baseline_row
              ON baseline_row.character_id = item_row.user_id;

            DO $verify_character_economy_baseline$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_economy_baseline baseline_row
                    LEFT JOIN (
                        SELECT
                            character_id,
                            count(*)::integer AS snapshot_count
                        FROM public.character_inventory_baseline_items
                        GROUP BY character_id
                    ) snapshot_row
                      ON snapshot_row.character_id =
                         baseline_row.character_id
                    WHERE baseline_row.item_count <>
                        COALESCE(snapshot_row.snapshot_count, 0)
                ) THEN
                    RAISE EXCEPTION
                        'Character inventory baseline snapshot count mismatch.';
                END IF;
            END;
            $verify_character_economy_baseline$;
            """);
}
