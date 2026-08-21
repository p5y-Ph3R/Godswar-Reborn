[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$AccountId = 13,

    [ValidatePattern('^[A-Za-z0-9_]{1,32}$')]
    [string]$CharacterName = 'test2',

    [ValidateRange(1, 9504)]
    [int]$QuantityToAdd = 300,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$PostgresContainer = 'godswar-dev-postgres',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$ServerContainer = 'godswar-dev-tempest-openworld-01',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = 'godswar',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$DatabaseUser = 'godswar'
)

# Local-development fixture only. The operation adds exactly the requested
# quantity without clearing, replacing, or moving unrelated bag contents.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required but was not found on PATH.'
}

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentStack.Common.psm1'
) -Force

$server = Assert-DevelopmentContainer $ServerContainer 'server'
if ([bool]$server.State.Running) {
    throw "Refusing the offline grant while '$ServerContainer' is running."
}
if (@($server.Config.Env) -cnotcontains
    'GODSWAR_RUNTIME_PROFILE=LocalDevelopment') {
    throw "'$ServerContainer' is not a LocalDevelopment server."
}

$postgres = Assert-DevelopmentContainer $PostgresContainer 'postgres'
if (-not [bool]$postgres.State.Running) {
    throw "PostgreSQL '$PostgresContainer' is not running."
}
$health = $postgres.State.PSObject.Properties['Health']
if ($null -ne $health -and $null -ne $health.Value -and
    $health.Value.Status -cne 'healthy') {
    throw "PostgreSQL '$PostgresContainer' is not healthy."
}
$dataMounts = @($postgres.Mounts | Where-Object {
    $_.Destination -ceq '/var/lib/postgresql/data'
})
if ($dataMounts.Count -ne 1 -or
    $dataMounts[0].Name -cne 'godswar-dev-postgres-data') {
    throw 'Development PostgreSQL is not using its isolated data volume.'
}

$safeName = $CharacterName.Replace("'", "''")
$target = "account $AccountId / '$CharacterName'"
if (-not $PSCmdlet.ShouldProcess(
        $target,
        "Add exactly $QuantityToAdd Morning Dew 5 items")) {
    return
}

$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE morning_dew_grant_context (
    character_id integer NOT NULL,
    old_inventory_revision bigint NOT NULL,
    new_inventory_revision bigint,
    quantity_before integer NOT NULL,
    quantity_after integer,
    publication_revision text NOT NULL
);
CREATE TEMP TABLE morning_dew_grant_mutations (
    ordinal bigint GENERATED ALWAYS AS IDENTITY,
    action text NOT NULL,
    slot_index smallint NOT NULL,
    old_item jsonb
);

DO `$grant`$
DECLARE
    v_character_id integer;
    v_inventory_revision bigint;
    v_publication_revision text;
    v_quantity_before integer;
    v_capacity integer;
    v_remaining integer := $QuantityToAdd;
    v_add integer;
    v_open_slot smallint;
    v_item record;
BEGIN
    SELECT character_row.id, character_row.inventory_revision
    INTO v_character_id, v_inventory_revision
    FROM public.character_base character_row
    JOIN public.accounts account
      ON account.id = character_row.account_id
    WHERE character_row.account_id = $AccountId
      AND character_row.name = '$safeName'
      AND character_row.lifecycle_state = 'active'
      AND character_row.checkpoint_owner_id IS NULL
      AND account.login_status = 0
    FOR UPDATE OF character_row, account;
    IF NOT FOUND THEN
        RAISE EXCEPTION
            'Character % on account % is missing, online, or checkpoint-owned',
            '$safeName', $AccountId;
    END IF;

    PERFORM 1
    FROM public.character_items item
    WHERE item.user_id = v_character_id
      AND item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95
    ORDER BY item.slot_index
    FOR UPDATE;

    SELECT publication.revision
    INTO v_publication_revision
    FROM public.item_template_content_publication publication
    JOIN public.item_template_content_revisions release
      ON release.revision = publication.revision
     AND release.sealed_at IS NOT NULL
    JOIN public.item_template_content_definitions definition
      ON definition.revision = publication.revision
     AND definition.id = 10134
    JOIN public.item_templates mutable ON mutable.id = definition.id
    WHERE publication.family = 'items'
      AND definition.display_name = 'Morning Dew 5'
      AND definition.kind = 'consume item'
      AND COALESCE((definition.stats ->> 'Overlap')::integer, 0) = 99;
    IF v_publication_revision IS NULL THEN
        RAISE EXCEPTION 'Published Morning Dew 5 item 10134 is invalid';
    END IF;

    IF EXISTS (
        SELECT 1 FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
          AND item.prop_id = 10134
          AND (item.stack < 1 OR item.stack > 99))
    THEN
        RAISE EXCEPTION 'Existing Morning Dew 5 stack violates 1..99 policy';
    END IF;

    SELECT COALESCE(sum(item.stack), 0)::integer
    INTO v_quantity_before
    FROM public.character_items item
    WHERE item.user_id = v_character_id
      AND item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95
      AND item.prop_id = 10134;

    SELECT
        COALESCE(sum(99 - item.stack) FILTER (
            WHERE item.prop_id = 10134 AND item.bound = 0), 0)::integer +
        (SELECT count(*)::integer * 99
         FROM generate_series(0, 95) candidate(slot_index)
         WHERE NOT EXISTS (
             SELECT 1 FROM public.character_items occupied
             WHERE occupied.user_id = v_character_id
               AND occupied.item_location = 1
               AND occupied.slot_index = candidate.slot_index))
    INTO v_capacity
    FROM public.character_items item
    WHERE item.user_id = v_character_id
      AND item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95;
    IF v_capacity < $QuantityToAdd THEN
        RAISE EXCEPTION
            'Grant needs capacity for % items but only % is available',
            $QuantityToAdd, v_capacity;
    END IF;

    INSERT INTO morning_dew_grant_context (
        character_id, old_inventory_revision, quantity_before,
        publication_revision)
    VALUES (
        v_character_id, v_inventory_revision, v_quantity_before,
        v_publication_revision);

    FOR v_item IN
        SELECT item.id, item.slot_index, item.stack, to_jsonb(item) old_item
        FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
          AND item.prop_id = 10134
          AND item.bound = 0
          AND item.stack < 99
        ORDER BY item.slot_index
    LOOP
        EXIT WHEN v_remaining = 0;
        v_add := LEAST(v_remaining, 99 - v_item.stack);
        INSERT INTO morning_dew_grant_mutations (
            action, slot_index, old_item)
        VALUES ('update', v_item.slot_index, v_item.old_item);
        UPDATE public.character_items
        SET stack = v_item.stack + v_add,
            updated_at = transaction_timestamp()
        WHERE id = v_item.id;
        v_remaining := v_remaining - v_add;
    END LOOP;

    WHILE v_remaining > 0 LOOP
        SELECT candidate.slot_index::smallint
        INTO v_open_slot
        FROM generate_series(0, 95) candidate(slot_index)
        WHERE NOT EXISTS (
            SELECT 1 FROM public.character_items occupied
            WHERE occupied.user_id = v_character_id
              AND occupied.item_location = 1
              AND occupied.slot_index = candidate.slot_index)
        ORDER BY candidate.slot_index
        LIMIT 1;
        IF v_open_slot IS NULL THEN
            RAISE EXCEPTION 'No authoritative kit-bag slot remains';
        END IF;
        v_add := LEAST(v_remaining, 99);
        INSERT INTO public.character_items (
            user_id, item_location, slot_index, prop_id,
            item_quality, item_grade, bound, stack,
            item_exp, holy_suit_code)
        VALUES (
            v_character_id, 1, v_open_slot, 10134,
            1, 1, 0, v_add, 0, 0);
        INSERT INTO morning_dew_grant_mutations (
            action, slot_index, old_item)
        VALUES ('add', v_open_slot, NULL);
        v_remaining := v_remaining - v_add;
    END LOOP;

    UPDATE public.character_base character_row
    SET inventory_revision = character_row.inventory_revision + 1
    WHERE character_row.id = v_character_id
      AND character_row.inventory_revision = v_inventory_revision
    RETURNING character_row.inventory_revision
    INTO v_inventory_revision;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Inventory revision did not advance exactly once';
    END IF;

    UPDATE morning_dew_grant_context context
    SET new_inventory_revision = v_inventory_revision,
        quantity_after = (
            SELECT COALESCE(sum(item.stack), 0)::integer
            FROM public.character_items item
            WHERE item.user_id = context.character_id
              AND item.item_location = 1
              AND item.slot_index BETWEEN 0 AND 95
              AND item.prop_id = 10134);
    IF NOT EXISTS (
        SELECT 1 FROM morning_dew_grant_context
        WHERE quantity_after = quantity_before + $QuantityToAdd
          AND new_inventory_revision = old_inventory_revision + 1)
    THEN
        RAISE EXCEPTION 'Morning Dew 5 post-write validation failed';
    END IF;
END
`$grant`$;

INSERT INTO public.character_item_audit (
    source, action, user_id, item_location, slot_index,
    prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'localdev-morning-dew-5-grant', mutation.action,
       context.character_id, 1, mutation.slot_index,
       10134, 1, 1, 0, mutation.old_item
FROM morning_dew_grant_mutations mutation
CROSS JOIN morning_dew_grant_context context
ORDER BY mutation.ordinal;

COMMIT;
SELECT 'MORNING_DEW_GRANT_RESULT|' || jsonb_build_object(
    'accountId', $AccountId,
    'characterId', character_id,
    'characterName', '$safeName',
    'itemId', 10134,
    'itemName', 'Morning Dew 5',
    'quantityAdded', quantity_after - quantity_before,
    'quantityBefore', quantity_before,
    'quantityAfter', quantity_after,
    'inventoryRevisionBefore', old_inventory_revision,
    'inventoryRevisionAfter', new_inventory_revision,
    'mutations', (SELECT count(*) FROM morning_dew_grant_mutations),
    'stacks', (
        SELECT jsonb_agg(jsonb_build_object(
            'slot', item.slot_index, 'stack', item.stack)
            ORDER BY item.slot_index)
        FROM public.character_items item
        WHERE item.user_id = context.character_id
          AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
          AND item.prop_id = 10134))::text
FROM morning_dew_grant_context context;
"@

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
$exitCode = $LASTEXITCODE
$lines = @($output | ForEach-Object { $_.ToString() })
if ($exitCode -ne 0) {
    throw "Morning Dew 5 grant failed and rolled back:`n$($lines -join "`n")"
}
$prefix = 'MORNING_DEW_GRANT_RESULT|'
$result = $lines | Where-Object {
    $_.StartsWith($prefix)
} | Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($result)) {
    throw 'The Morning Dew 5 grant returned no verification receipt.'
}
$result.Substring($prefix.Length) | ConvertFrom-Json
