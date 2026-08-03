namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string CharacterInventoryReconciliationV2Sql =
        """

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
                    WHERE public.canonical_character_item_state_v2(
                            latest_row.item_state
                        ) IS DISTINCT FROM
                        CASE
                            WHEN current_item.id IS NULL
                                THEN NULL::jsonb
                            ELSE public.canonical_character_item_state_v2(
                                to_jsonb(current_item)
                            )
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

        COMMENT ON VIEW public.character_inventory_reconciliation IS
            'Report-only inventory baseline and ledger reconciliation using canonical item-state schema v2.';
        """;
}
