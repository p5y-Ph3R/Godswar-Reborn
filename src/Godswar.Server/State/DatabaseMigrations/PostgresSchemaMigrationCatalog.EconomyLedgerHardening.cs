namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateEconomyLedgerHardening() => new(
            "20260729_028_economy_ledger_hardening",
            "Harden inventory domains and publish report-only reconciliation views",
            """
            ALTER TABLE public.character_items
                ADD CONSTRAINT ck_character_items_location_slot_domain CHECK (
                    (
                        item_location = 0
                        AND slot_index BETWEEN 0 AND 23
                    )
                    OR (
                        item_location = 1
                        AND slot_index BETWEEN 0 AND 32767
                    )
                    OR (
                        item_location = 2
                        AND slot_index BETWEEN -32768 AND -1
                    )
                ),
                ADD CONSTRAINT ck_character_items_stack_positive
                    CHECK (stack > 0),
                ADD CONSTRAINT ck_character_items_exp_nonnegative
                    CHECK (item_exp >= 0),
                ADD CONSTRAINT ck_character_items_quality_domain
                    CHECK (item_quality BETWEEN 0 AND 20),
                ADD CONSTRAINT ck_character_items_grade_domain
                    CHECK (item_grade BETWEEN 0 AND 25),
                ADD CONSTRAINT ck_character_items_bound_domain
                    CHECK (bound BETWEEN 0 AND 1),
                ADD CONSTRAINT ck_character_items_socket_count_domain
                    CHECK (holy_socket_count BETWEEN 0 AND 6);

            CREATE OR REPLACE FUNCTION
                public.reject_character_economy_evidence_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $reject_character_economy_evidence_mutation$
            BEGIN
                RAISE EXCEPTION
                    'Economy evidence table %.% is append-only.',
                    TG_TABLE_SCHEMA,
                    TG_TABLE_NAME
                    USING ERRCODE = '55000';
            END;
            $reject_character_economy_evidence_mutation$;

            CREATE TRIGGER trg_character_economy_baseline_immutable
            BEFORE UPDATE OR DELETE
                ON public.character_economy_baseline
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_character_economy_evidence_mutation();

            CREATE TRIGGER trg_character_economy_baseline_no_truncate
            BEFORE TRUNCATE
                ON public.character_economy_baseline
            FOR EACH STATEMENT
            EXECUTE FUNCTION
                public.reject_character_economy_evidence_mutation();

            CREATE TRIGGER
                trg_character_inventory_baseline_items_immutable
            BEFORE UPDATE OR DELETE
                ON public.character_inventory_baseline_items
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_character_economy_evidence_mutation();

            CREATE TRIGGER
                trg_character_inventory_baseline_items_no_truncate
            BEFORE TRUNCATE
                ON public.character_inventory_baseline_items
            FOR EACH STATEMENT
            EXECUTE FUNCTION
                public.reject_character_economy_evidence_mutation();

            CREATE TRIGGER trg_character_currency_ledger_immutable
            BEFORE UPDATE OR DELETE
                ON public.character_currency_ledger
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_character_economy_evidence_mutation();

            CREATE TRIGGER trg_character_currency_ledger_no_truncate
            BEFORE TRUNCATE
                ON public.character_currency_ledger
            FOR EACH STATEMENT
            EXECUTE FUNCTION
                public.reject_character_economy_evidence_mutation();

            CREATE TRIGGER trg_character_inventory_ledger_immutable
            BEFORE UPDATE OR DELETE
                ON public.character_inventory_ledger
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_character_economy_evidence_mutation();

            CREATE TRIGGER trg_character_inventory_ledger_no_truncate
            BEFORE TRUNCATE
                ON public.character_inventory_ledger
            FOR EACH STATEMENT
            EXECUTE FUNCTION
                public.reject_character_economy_evidence_mutation();

            CREATE OR REPLACE VIEW
                public.character_wallet_reconciliation
            AS
            WITH character_keys AS (
                SELECT character_id
                FROM public.character_economy_baseline
                UNION
                SELECT id
                FROM public.character_base
            ),
            ledger_summary AS (
                SELECT
                    character_id,
                    max(wallet_revision) AS maximum_ledger_revision,
                    count(DISTINCT wallet_revision)::bigint
                        AS distinct_ledger_revisions,
                    count(*)::bigint AS ledger_entry_count,
                    COALESCE(sum(delta) FILTER (
                        WHERE currency_code = 'silver'
                    ), 0)::bigint AS silver_delta,
                    COALESCE(sum(delta) FILTER (
                        WHERE currency_code = 'gold'
                    ), 0)::bigint AS gold_delta
                FROM public.character_currency_ledger
                GROUP BY character_id
            )
            SELECT
                key_row.character_id,
                baseline_row.account_id AS baseline_account_id,
                character_row.account_id AS current_account_id,
                baseline_row.character_id IS NOT NULL AS baseline_present,
                character_row.id IS NOT NULL AS character_present,
                baseline_row.wallet_revision AS baseline_wallet_revision,
                character_row.wallet_revision AS current_wallet_revision,
                COALESCE(
                    ledger_row.maximum_ledger_revision,
                    0
                )::bigint AS maximum_ledger_revision,
                COALESCE(
                    ledger_row.distinct_ledger_revisions,
                    0
                )::bigint AS distinct_ledger_revisions,
                COALESCE(
                    ledger_row.ledger_entry_count,
                    0
                )::bigint AS ledger_entry_count,
                baseline_row.silver AS baseline_silver,
                character_row."Money"::bigint AS current_silver,
                (
                    baseline_row.silver +
                    COALESCE(ledger_row.silver_delta, 0)
                )::bigint AS expected_silver,
                baseline_row.gold AS baseline_gold,
                character_row."Stone"::bigint AS current_gold,
                (
                    baseline_row.gold +
                    COALESCE(ledger_row.gold_delta, 0)
                )::bigint AS expected_gold,
                (
                    baseline_row.account_id IS NOT NULL
                    AND baseline_row.account_id =
                        character_row.account_id
                ) AS identity_matches,
                (
                    COALESCE(
                        ledger_row.distinct_ledger_revisions,
                        0
                    ) =
                    COALESCE(
                        ledger_row.maximum_ledger_revision,
                        0
                    )
                ) AS revision_sequence_contiguous,
                (
                    character_row.wallet_revision =
                    COALESCE(
                        ledger_row.maximum_ledger_revision,
                        0
                    )
                ) AS revision_matches,
                (
                    character_row."Money"::bigint =
                        baseline_row.silver +
                        COALESCE(ledger_row.silver_delta, 0)
                    AND character_row."Stone"::bigint =
                        baseline_row.gold +
                        COALESCE(ledger_row.gold_delta, 0)
                ) AS balances_match,
                (
                    baseline_row.character_id IS NOT NULL
                    AND character_row.id IS NOT NULL
                    AND baseline_row.account_id =
                        character_row.account_id
                    AND COALESCE(
                        ledger_row.distinct_ledger_revisions,
                        0
                    ) =
                        COALESCE(
                            ledger_row.maximum_ledger_revision,
                            0
                        )
                    AND character_row.wallet_revision =
                        COALESCE(
                            ledger_row.maximum_ledger_revision,
                            0
                        )
                    AND character_row."Money"::bigint =
                        baseline_row.silver +
                        COALESCE(ledger_row.silver_delta, 0)
                    AND character_row."Stone"::bigint =
                        baseline_row.gold +
                        COALESCE(ledger_row.gold_delta, 0)
                ) AS is_reconciled
            FROM character_keys key_row
            LEFT JOIN public.character_economy_baseline baseline_row
              ON baseline_row.character_id = key_row.character_id
            LEFT JOIN public.character_base character_row
              ON character_row.id = key_row.character_id
            LEFT JOIN ledger_summary ledger_row
              ON ledger_row.character_id = key_row.character_id;

            CREATE OR REPLACE VIEW
                public.character_inventory_reconciliation
            AS
            WITH item_history AS (
                SELECT
                    character_id,
                    item_instance_id,
                    0::bigint AS inventory_revision,
                    item_state
                FROM public.character_inventory_baseline_items
                UNION ALL
                SELECT
                    character_id,
                    item_instance_id,
                    inventory_revision,
                    after_state AS item_state
                FROM public.character_inventory_ledger
            ),
            latest_item_state AS (
                SELECT DISTINCT ON (
                    character_id,
                    item_instance_id
                )
                    character_id,
                    item_instance_id,
                    inventory_revision,
                    item_state
                FROM item_history
                ORDER BY
                    character_id,
                    item_instance_id,
                    inventory_revision DESC
            ),
            item_keys AS (
                SELECT character_id, item_instance_id
                FROM latest_item_state
                UNION
                SELECT user_id, id
                FROM public.character_items
            ),
            item_differences AS (
                SELECT
                    key_row.character_id,
                    count(*) FILTER (
                        WHERE latest_row.item_state IS NOT NULL
                    )::integer AS expected_item_count,
                    count(*) FILTER (
                        WHERE current_item.id IS NOT NULL
                    )::integer AS current_item_count,
                    count(*) FILTER (
                        WHERE latest_row.item_state IS DISTINCT FROM
                            CASE
                                WHEN current_item.id IS NULL
                                    THEN NULL::jsonb
                                ELSE to_jsonb(current_item)
                            END
                    )::integer AS mismatched_item_count
                FROM item_keys key_row
                LEFT JOIN latest_item_state latest_row
                  ON latest_row.character_id = key_row.character_id
                 AND latest_row.item_instance_id =
                     key_row.item_instance_id
                LEFT JOIN public.character_items current_item
                  ON current_item.user_id = key_row.character_id
                 AND current_item.id = key_row.item_instance_id
                GROUP BY key_row.character_id
            ),
            baseline_snapshot_summary AS (
                SELECT
                    character_id,
                    count(*)::integer AS snapshot_item_count
                FROM public.character_inventory_baseline_items
                GROUP BY character_id
            ),
            ledger_summary AS (
                SELECT
                    character_id,
                    max(inventory_revision)
                        AS maximum_ledger_revision,
                    count(DISTINCT inventory_revision)::bigint
                        AS distinct_ledger_revisions,
                    count(*)::bigint AS ledger_entry_count
                FROM public.character_inventory_ledger
                GROUP BY character_id
            ),
            character_keys AS (
                SELECT character_id
                FROM public.character_economy_baseline
                UNION
                SELECT id
                FROM public.character_base
            )
            SELECT
                key_row.character_id,
                baseline_row.account_id AS baseline_account_id,
                character_row.account_id AS current_account_id,
                baseline_row.character_id IS NOT NULL AS baseline_present,
                character_row.id IS NOT NULL AS character_present,
                baseline_row.inventory_revision
                    AS baseline_inventory_revision,
                character_row.inventory_revision
                    AS current_inventory_revision,
                COALESCE(
                    ledger_row.maximum_ledger_revision,
                    0
                )::bigint AS maximum_ledger_revision,
                COALESCE(
                    ledger_row.distinct_ledger_revisions,
                    0
                )::bigint AS distinct_ledger_revisions,
                COALESCE(
                    ledger_row.ledger_entry_count,
                    0
                )::bigint AS ledger_entry_count,
                baseline_row.item_count AS baseline_item_count,
                COALESCE(
                    snapshot_row.snapshot_item_count,
                    0
                )::integer AS snapshot_item_count,
                COALESCE(
                    difference_row.expected_item_count,
                    0
                )::integer AS expected_item_count,
                COALESCE(
                    difference_row.current_item_count,
                    0
                )::integer AS current_item_count,
                COALESCE(
                    difference_row.mismatched_item_count,
                    0
                )::integer AS mismatched_item_count,
                (
                    baseline_row.account_id IS NOT NULL
                    AND baseline_row.account_id =
                        character_row.account_id
                ) AS identity_matches,
                (
                    baseline_row.item_count =
                    COALESCE(snapshot_row.snapshot_item_count, 0)
                ) AS baseline_snapshot_matches,
                (
                    COALESCE(
                        ledger_row.distinct_ledger_revisions,
                        0
                    ) =
                    COALESCE(
                        ledger_row.maximum_ledger_revision,
                        0
                    )
                ) AS revision_sequence_contiguous,
                (
                    character_row.inventory_revision =
                    COALESCE(
                        ledger_row.maximum_ledger_revision,
                        0
                    )
                ) AS revision_matches,
                (
                    COALESCE(
                        difference_row.mismatched_item_count,
                        0
                    ) = 0
                    AND COALESCE(
                        difference_row.expected_item_count,
                        0
                    ) =
                        COALESCE(
                            difference_row.current_item_count,
                            0
                        )
                ) AS items_match,
                (
                    baseline_row.character_id IS NOT NULL
                    AND character_row.id IS NOT NULL
                    AND baseline_row.account_id =
                        character_row.account_id
                    AND baseline_row.item_count =
                        COALESCE(snapshot_row.snapshot_item_count, 0)
                    AND COALESCE(
                        ledger_row.distinct_ledger_revisions,
                        0
                    ) =
                        COALESCE(
                            ledger_row.maximum_ledger_revision,
                            0
                        )
                    AND character_row.inventory_revision =
                        COALESCE(
                            ledger_row.maximum_ledger_revision,
                            0
                        )
                    AND COALESCE(
                        difference_row.mismatched_item_count,
                        0
                    ) = 0
                    AND COALESCE(
                        difference_row.expected_item_count,
                        0
                    ) =
                        COALESCE(
                            difference_row.current_item_count,
                            0
                        )
                ) AS is_reconciled
            FROM character_keys key_row
            LEFT JOIN public.character_economy_baseline baseline_row
              ON baseline_row.character_id = key_row.character_id
            LEFT JOIN public.character_base character_row
              ON character_row.id = key_row.character_id
            LEFT JOIN baseline_snapshot_summary snapshot_row
              ON snapshot_row.character_id = key_row.character_id
            LEFT JOIN ledger_summary ledger_row
              ON ledger_row.character_id = key_row.character_id
            LEFT JOIN item_differences difference_row
              ON difference_row.character_id = key_row.character_id;

            COMMENT ON VIEW public.character_wallet_reconciliation IS
                'Report-only wallet baseline and ledger reconciliation.';

            COMMENT ON VIEW public.character_inventory_reconciliation IS
                'Report-only inventory baseline and ledger reconciliation.';
            """);
}
