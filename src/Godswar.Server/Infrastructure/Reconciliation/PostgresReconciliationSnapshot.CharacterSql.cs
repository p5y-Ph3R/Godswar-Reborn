namespace Godswar.Server.Infrastructure.Reconciliation;

internal sealed partial class PostgresReconciliationSnapshot
{
    private const string CharacterPageSql =
        """
        WITH keys AS MATERIALIZED (
            SELECT character_id::bigint AS character_id
            FROM public.character_economy_baseline
            WHERE character_id > @after_key
            UNION
            SELECT id::bigint
            FROM public.character_base
            WHERE id > @after_key
            ORDER BY character_id
            LIMIT @limit
        ),
        wallet_ledger AS (
            SELECT
                keys.character_id,
                max(ledger.wallet_revision) AS maximum_revision,
                count(DISTINCT ledger.wallet_revision)
                    AS distinct_revisions,
                COALESCE(sum(ledger.delta) FILTER (
                    WHERE ledger.currency_code = 'silver'
                ), 0)::bigint AS silver_delta,
                COALESCE(sum(ledger.delta) FILTER (
                    WHERE ledger.currency_code = 'gold'
                ), 0)::bigint AS gold_delta
            FROM keys
            LEFT JOIN public.character_currency_ledger ledger
                ON ledger.character_id = keys.character_id
            GROUP BY keys.character_id
        ),
        inventory_ledger AS (
            SELECT
                keys.character_id,
                max(ledger.inventory_revision) AS maximum_revision,
                count(DISTINCT ledger.inventory_revision)
                    AS distinct_revisions
            FROM keys
            LEFT JOIN public.character_inventory_ledger ledger
                ON ledger.character_id = keys.character_id
            GROUP BY keys.character_id
        ),
        inventory_snapshot AS (
            SELECT
                keys.character_id,
                count(snapshot.item_instance_id)::integer
                    AS snapshot_item_count
            FROM keys
            LEFT JOIN public.character_inventory_baseline_items snapshot
                ON snapshot.character_id = keys.character_id
            GROUP BY keys.character_id
        ),
        inventory_difference AS (
            SELECT
                keys.character_id,
                difference.expected_item_count,
                difference.current_item_count,
                difference.mismatched_item_count
            FROM keys
            CROSS JOIN LATERAL (
                WITH item_history AS (
                    SELECT
                        baseline.item_instance_id,
                        0::bigint AS inventory_revision,
                        baseline.item_state
                    FROM public.character_inventory_baseline_items baseline
                    WHERE baseline.character_id =
                        keys.character_id
                    UNION ALL
                    SELECT
                        ledger.item_instance_id,
                        ledger.inventory_revision,
                        ledger.after_state AS item_state
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id =
                        keys.character_id
                ),
                latest_item_state AS (
                    SELECT DISTINCT ON (item_instance_id)
                        item_instance_id,
                        inventory_revision,
                        item_state
                    FROM item_history
                    ORDER BY
                        item_instance_id,
                        inventory_revision DESC
                ),
                item_keys AS (
                    SELECT item_instance_id
                    FROM latest_item_state
                    UNION
                    SELECT item.id
                    FROM public.character_items item
                    WHERE item.user_id = keys.character_id
                )
                SELECT
                    count(*) FILTER (
                        WHERE latest.item_state IS NOT NULL
                    )::integer AS expected_item_count,
                    count(*) FILTER (
                        WHERE current_item.id IS NOT NULL
                    )::integer AS current_item_count,
                    count(*) FILTER (
                        WHERE latest.item_state IS DISTINCT FROM
                            CASE
                                WHEN current_item.id IS NULL
                                    THEN NULL::jsonb
                                ELSE to_jsonb(current_item)
                            END
                    )::integer AS mismatched_item_count
                FROM item_keys item_key
                LEFT JOIN latest_item_state latest
                    ON latest.item_instance_id =
                        item_key.item_instance_id
                LEFT JOIN public.character_items current_item
                    ON current_item.user_id = keys.character_id
                   AND current_item.id = item_key.item_instance_id
            ) difference
        ),
        progression AS (
            SELECT
                settlement.character_id::bigint AS character_id,
                max(settlement.progression_revision)
                    AS maximum_revision,
                count(DISTINCT settlement.progression_revision)
                    AS distinct_revisions,
                bool_or(
                    inbox.id IS NULL
                    OR audit.id IS NULL
                    OR event.event_id IS NULL
                    OR inbox.audit_id <>
                        settlement.audit_id
                    OR inbox.command_family <>
                        'monster_reward_settlement'
                    OR event.event_id <>
                        settlement.outbox_event_id
                    OR event.command_inbox_id <>
                        settlement.command_inbox_id
                    OR event.aggregate_type <>
                        'character_progression'
                    OR event.aggregate_key <>
                        'character:' ||
                            settlement.character_id::text ||
                            ':progression'
                    OR event.aggregate_version <>
                        settlement.progression_revision
                    OR event.event_type <>
                        'progression.monster_reward_settled'
                ) AS evidence_gap
            FROM public.monster_death_reward_settlements settlement
            INNER JOIN keys
                ON keys.character_id = settlement.character_id
            LEFT JOIN public.command_inbox inbox
                ON inbox.id = settlement.command_inbox_id
            LEFT JOIN public.command_audit audit
                ON audit.id = settlement.audit_id
            LEFT JOIN public.outbox_events event
                ON event.event_id = settlement.outbox_event_id
               AND event.consumer_key =
                   'progression_reward_projection_v1'
            GROUP BY settlement.character_id
        ),
        pet_events AS (
            SELECT
                keys.character_id,
                max(event.aggregate_version) AS maximum_revision,
                count(DISTINCT event.aggregate_version)
                    AS distinct_revisions
            FROM keys
            LEFT JOIN public.outbox_events event
                ON event.consumer_key = 'pet_durable_v1'
               AND event.aggregate_type =
                   'character_pet_value'
               AND event.aggregate_key =
                   'character:' || keys.character_id::text
            GROUP BY keys.character_id
        ),
        pet_presence AS (
            SELECT
                pet.user_id::bigint AS character_id,
                count(*) FILTER (WHERE pet.is_carried)
                    AS carried_count,
                count(*) FILTER (WHERE pet.is_summoned)
                    AS summoned_count,
                count(*) FILTER (
                    WHERE pet.contributes_to_character
                ) AS contributing_count,
                bool_or(
                    (pet.is_summoned AND NOT pet.is_carried)
                    OR (
                        pet.contributes_to_character
                        AND (
                            NOT pet.is_summoned
                            OR NOT pet.has_owner_merge_talent
                        )
                    )
                ) AS invalid_state
            FROM public.character_pets pet
            INNER JOIN keys
                ON keys.character_id = pet.user_id
            GROUP BY pet.user_id
        ),
        purge_proof AS (
            SELECT
                keys.character_id,
                bool_or(
                    audit.outcome_code = 'committed'
                    AND inbox.id IS NOT NULL
                    AND inbox.result_code = 'committed'
                    AND inbox.principal_type =
                        audit.principal_type
                    AND inbox.principal_key =
                        audit.principal_key
                    AND inbox.operation_id = audit.operation_id
                    AND inbox.request_hash = audit.request_hash
                    AND event.id IS NOT NULL
                    AND event.consumer_key =
                        'character_lifecycle_v1'
                    AND event.event_type = 'character.purged'
                    AND event.aggregate_type =
                        audit.aggregate_type
                    AND event.aggregate_key =
                        audit.aggregate_key
                    AND event.command_inbox_id = inbox.id
                ) AS proven
            FROM keys
            INNER JOIN public.character_economy_baseline
                purge_baseline
                ON purge_baseline.character_id =
                    keys.character_id
            INNER JOIN public.command_audit audit
                ON audit.principal_type = 'account'
               AND audit.principal_key =
                   purge_baseline.account_id::text
               AND audit.aggregate_type =
                   'account_character_slot'
               AND audit.aggregate_key =
                   purge_baseline.account_id::text || ':0'
               AND audit.command_family = 'character_purge'
               AND audit.detail_payload ->> 'characterId' =
                   keys.character_id::text
            LEFT JOIN public.command_inbox inbox
                ON inbox.audit_id = audit.id
               AND inbox.command_family = audit.command_family
               AND inbox.aggregate_type = audit.aggregate_type
               AND inbox.aggregate_key = audit.aggregate_key
            LEFT JOIN public.outbox_events event
                ON event.command_inbox_id = inbox.id
            GROUP BY keys.character_id
        )
        SELECT
            keys.character_id,
            baseline.character_id IS NULL,
            character_row.id IS NULL
                AND NOT COALESCE(purge_proof.proven, false),
            baseline.character_id IS NOT NULL
                AND character_row.id IS NOT NULL
                AND baseline.account_id
                    IS DISTINCT FROM character_row.account_id,
            baseline.character_id IS NOT NULL
                AND character_row.id IS NOT NULL
                AND wallet_ledger.distinct_revisions <>
                    COALESCE(wallet_ledger.maximum_revision, 0),
            baseline.character_id IS NOT NULL
                AND character_row.id IS NOT NULL
                AND character_row.wallet_revision <>
                    COALESCE(wallet_ledger.maximum_revision, 0),
            baseline.character_id IS NOT NULL
                AND character_row.id IS NOT NULL
                AND (
                    character_row."Money"::bigint <>
                        baseline.silver +
                            wallet_ledger.silver_delta
                    OR character_row."Stone"::bigint <>
                        baseline.gold +
                            wallet_ledger.gold_delta
                ),
            baseline.character_id IS NULL,
            character_row.id IS NULL
                AND NOT COALESCE(purge_proof.proven, false),
            baseline.character_id IS NOT NULL
                AND character_row.id IS NOT NULL
                AND baseline.account_id
                    IS DISTINCT FROM character_row.account_id,
            baseline.character_id IS NOT NULL
                AND character_row.id IS NOT NULL
                AND baseline.item_count <>
                    inventory_snapshot.snapshot_item_count,
            baseline.character_id IS NOT NULL
                AND character_row.id IS NOT NULL
                AND inventory_ledger.distinct_revisions <>
                    COALESCE(
                        inventory_ledger.maximum_revision,
                        0
                    ),
            baseline.character_id IS NOT NULL
                AND character_row.id IS NOT NULL
                AND character_row.inventory_revision <>
                    COALESCE(
                        inventory_ledger.maximum_revision,
                        0
                    ),
            baseline.character_id IS NOT NULL
                AND character_row.id IS NOT NULL
                AND (
                    inventory_difference.mismatched_item_count <> 0
                    OR inventory_difference.expected_item_count <>
                        inventory_difference.current_item_count
                ),
            EXISTS (
                SELECT 1
                FROM public.character_items duplicate_item
                WHERE duplicate_item.user_id = keys.character_id
                GROUP BY
                    duplicate_item.item_location,
                    duplicate_item.slot_index
                HAVING count(*) > 1
            ),
            EXISTS (
                SELECT 1
                FROM public.character_items orphan_item
                LEFT JOIN public.item_templates template
                    ON template.id = orphan_item.prop_id
                WHERE orphan_item.user_id = keys.character_id
                  AND template.id IS NULL
            ),
            NOT COALESCE(purge_proof.proven, false)
                AND (
                    COALESCE(
                        character_row.progression_reward_revision,
                        0
                    ) <> COALESCE(
                        progression.maximum_revision,
                        0
                    )
                    OR COALESCE(
                        progression.distinct_revisions,
                        0
                    ) <> COALESCE(
                        progression.maximum_revision,
                        0
                    )
                ),
            NOT COALESCE(purge_proof.proven, false)
                AND COALESCE(progression.evidence_gap, false),
            COALESCE(pet_presence.carried_count, 0) > 1
                OR COALESCE(pet_presence.summoned_count, 0) > 1
                OR COALESCE(
                    pet_presence.contributing_count,
                    0
                ) > 1
                OR COALESCE(pet_presence.invalid_state, false),
            NOT COALESCE(purge_proof.proven, false)
                AND (
                    COALESCE(stream.current_version, 0)
                        <> COALESCE(
                            pet_events.maximum_revision,
                            0
                        )
                    OR COALESCE(
                        pet_events.distinct_revisions,
                        0
                    ) <> COALESCE(
                        pet_events.maximum_revision,
                        0
                    )
                ),
            character_row.id IS NULL
                AND NOT COALESCE(purge_proof.proven, false)
        FROM keys
        LEFT JOIN public.character_economy_baseline baseline
            ON baseline.character_id = keys.character_id
        LEFT JOIN public.character_base character_row
            ON character_row.id = keys.character_id
        INNER JOIN wallet_ledger
            ON wallet_ledger.character_id = keys.character_id
        INNER JOIN inventory_ledger
            ON inventory_ledger.character_id = keys.character_id
        INNER JOIN inventory_snapshot
            ON inventory_snapshot.character_id = keys.character_id
        INNER JOIN inventory_difference
            ON inventory_difference.character_id = keys.character_id
        LEFT JOIN progression
            ON progression.character_id = keys.character_id
        LEFT JOIN public.pet_durable_stream_versions stream
            ON stream.character_id = keys.character_id
        LEFT JOIN pet_events
            ON pet_events.character_id = keys.character_id
        LEFT JOIN pet_presence
            ON pet_presence.character_id = keys.character_id
        LEFT JOIN purge_proof
            ON purge_proof.character_id = keys.character_id
        ORDER BY keys.character_id;
        """;
}
