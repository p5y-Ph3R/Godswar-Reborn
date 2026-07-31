function Get-B19ReconciliationState {
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$PostgresUser,
        [Parameter(Mandatory)][string]$Database
    )

    $value = Invoke-B19Psql `
        $ContainerName $Password $PostgresUser $Database @'
WITH purge_proof AS (
    SELECT
        baseline.character_id,
        bool_or(
            audit.outcome_code = 'committed'
            AND inbox.id IS NOT NULL
            AND inbox.result_code = 'committed'
            AND inbox.principal_type = audit.principal_type
            AND inbox.principal_key = audit.principal_key
            AND inbox.operation_id = audit.operation_id
            AND inbox.request_hash = audit.request_hash
            AND event.id IS NOT NULL
            AND event.consumer_key = 'character_lifecycle_v1'
            AND event.event_type = 'character.purged'
            AND event.aggregate_type = audit.aggregate_type
            AND event.aggregate_key = audit.aggregate_key
            AND event.command_inbox_id = inbox.id
        ) AS proven
    FROM public.character_economy_baseline baseline
    INNER JOIN public.command_audit audit
        ON audit.principal_type = 'account'
       AND audit.principal_key = baseline.account_id::text
       AND audit.aggregate_type = 'account_character_slot'
       AND audit.aggregate_key = baseline.account_id::text || ':0'
       AND audit.command_family = 'character_purge'
       AND audit.detail_payload ->> 'characterId' =
            baseline.character_id::text
    LEFT JOIN public.command_inbox inbox
        ON inbox.audit_id = audit.id
       AND inbox.command_family = audit.command_family
       AND inbox.aggregate_type = audit.aggregate_type
       AND inbox.aggregate_key = audit.aggregate_key
    LEFT JOIN public.outbox_events event
        ON event.command_inbox_id = inbox.id
    GROUP BY baseline.character_id
),
wallet_state AS (
    SELECT
        wallet.*,
        (
            wallet.baseline_present
            AND NOT wallet.character_present
            AND COALESCE(proof.proven, false)
        ) AS proven_purge
    FROM public.character_wallet_reconciliation wallet
    LEFT JOIN purge_proof proof
        ON proof.character_id = wallet.character_id
),
inventory_state AS (
    SELECT
        inventory.*,
        (
            inventory.baseline_present
            AND NOT inventory.character_present
            AND COALESCE(proof.proven, false)
        ) AS proven_purge
    FROM public.character_inventory_reconciliation inventory
    LEFT JOIN purge_proof proof
        ON proof.character_id = inventory.character_id
)
SELECT
    (SELECT count(*) FROM wallet_state)::text
    || '|' ||
    (SELECT count(*) FROM wallet_state
      WHERE is_reconciled IS DISTINCT FROM true
        AND NOT proven_purge)::text
    || '|' ||
    (SELECT count(*) FROM inventory_state)::text
    || '|' ||
    (SELECT count(*) FROM inventory_state
      WHERE is_reconciled IS DISTINCT FROM true
        AND NOT proven_purge)::text
    || '|' ||
    (SELECT count(*) FROM wallet_state
      WHERE proven_purge)::text
    || '|' ||
    (SELECT count(*) FROM inventory_state
      WHERE proven_purge)::text;
'@
    $parts = $value.Split('|')
    if ($parts.Length -ne 6) {
        throw "Could not parse reconciliation state for '$Database'."
    }

    [ordered]@{
        walletRows = [long]$parts[0]
        walletUnexplainedMismatches = [long]$parts[1]
        inventoryRows = [long]$parts[2]
        inventoryUnexplainedMismatches = [long]$parts[3]
        walletProvenPurgeRows = [long]$parts[4]
        inventoryProvenPurgeRows = [long]$parts[5]
    }
}

function Assert-B19Reconciled {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$State
    )

    if ($State.walletUnexplainedMismatches -ne 0 -or
        $State.inventoryUnexplainedMismatches -ne 0) {
        throw 'The B19 fixture retained an unexplained mismatch.'
    }

    if ($State.walletProvenPurgeRows -ne 1 -or
        $State.inventoryProvenPurgeRows -ne 1) {
        throw (
            'The B19 fixture must retain one symmetrically proven purge ' +
            'row in both immutable economy views.')
    }
}
