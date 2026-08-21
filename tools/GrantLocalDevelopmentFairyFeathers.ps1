[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$AccountId = 13,

    [ValidatePattern('^[A-Za-z0-9_]{1,32}$')]
    [string]$CharacterName = 'test2',

    [ValidateRange(1, 9504)]
    [int]$TargetQuantity = 200,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$PostgresContainer = 'godswar-dev-postgres',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$ServerContainer = 'godswar-dev-tempest-openworld-01',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = 'godswar',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$DatabaseUser = 'godswar'
)

# Local-development fixture only. It reaches an exact Fairy's Feather total
# without clearing, moving, replacing, or trimming another bag item. Account
# and character row locks serialize this operation against login/checkpoint
# acquisition while the isolated development server remains available.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required but was not found on PATH.'
}

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentStack.Common.psm1'
) -Force

$server = Assert-DevelopmentContainer $ServerContainer 'server'
$serverEnvironment = @($server.Config.Env)
if ($serverEnvironment -cnotcontains
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
        "Ensure exactly $TargetQuantity Fairy's Feathers in the kit bag")) {
    return
}

$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE fairy_feather_grant_context (
    account_id integer NOT NULL,
    character_id integer NOT NULL,
    character_name text NOT NULL,
    publication_revision text NOT NULL,
    old_inventory_revision bigint NOT NULL,
    new_inventory_revision bigint,
    previous_total integer NOT NULL,
    current_total integer,
    changed boolean NOT NULL DEFAULT false
);
CREATE TEMP TABLE fairy_feather_grant_mutations (
    ordinal bigint GENERATED ALWAYS AS IDENTITY,
    action text NOT NULL,
    slot_index smallint NOT NULL,
    old_item jsonb,
    old_stack smallint NOT NULL,
    new_stack smallint NOT NULL
);

DO `$grant`$
DECLARE
    v_character_id integer;
    v_inventory_revision bigint;
    v_publication_revision text;
    v_previous_total integer;
    v_remaining integer;
    v_capacity integer;
    v_added integer;
    v_open_slot smallint;
    v_new_stack smallint;
    v_item record;
BEGIN
    SELECT character_row.id,
           character_row.inventory_revision
    INTO v_character_id,
         v_inventory_revision
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
     AND definition.id = 11000
    JOIN public.item_templates mutable
      ON mutable.id = definition.id
    WHERE publication.family = 'items'
      AND definition.name_key = 'Pet11000'
      AND definition.display_name = 'Fairy''s Feather'
      AND definition.kind = 'consume item'
      AND COALESCE((definition.stats ->> 'Overlap')::integer, 0) = 99;
    IF v_publication_revision IS NULL THEN
        RAISE EXCEPTION
            'Published Fairy''s Feather item 11000 is missing or invalid';
    END IF;

    SELECT COALESCE(sum(item.stack), 0)::integer
    INTO v_previous_total
    FROM public.character_items item
    WHERE item.user_id = v_character_id
      AND item.item_location = 1
      AND item.prop_id = 11000;
    IF v_previous_total > $TargetQuantity THEN
        RAISE EXCEPTION
            'Refusing to trim Fairy''s Feathers from % to target %',
            v_previous_total, $TargetQuantity;
    END IF;

    INSERT INTO fairy_feather_grant_context (
        account_id, character_id, character_name,
        publication_revision, old_inventory_revision, previous_total)
    VALUES (
        $AccountId, v_character_id, '$safeName',
        v_publication_revision, v_inventory_revision, v_previous_total);

    v_remaining := $TargetQuantity - v_previous_total;
    FOR v_item IN
        SELECT item.id, item.slot_index, item.stack,
               to_jsonb(item) AS old_item
        FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.prop_id = 11000
          AND item.stack < 99
        ORDER BY item.slot_index
    LOOP
        EXIT WHEN v_remaining = 0;
        v_capacity := 99 - v_item.stack;
        v_added := LEAST(v_remaining, v_capacity);

        INSERT INTO fairy_feather_grant_mutations (
            action, slot_index, old_item, old_stack, new_stack)
        VALUES (
            'update', v_item.slot_index, v_item.old_item,
            v_item.stack, v_item.stack + v_added);

        UPDATE public.character_items
        SET stack = v_item.stack + v_added,
            updated_at = transaction_timestamp()
        WHERE id = v_item.id;
        v_remaining := v_remaining - v_added;
    END LOOP;

    WHILE v_remaining > 0 LOOP
        SELECT candidate.slot_index::smallint
        INTO v_open_slot
        FROM generate_series(0, 95) candidate(slot_index)
        WHERE NOT EXISTS (
            SELECT 1
            FROM public.character_items occupied
            WHERE occupied.user_id = v_character_id
              AND occupied.item_location = 1
              AND occupied.slot_index = candidate.slot_index)
        ORDER BY candidate.slot_index
        LIMIT 1;
        IF v_open_slot IS NULL THEN
            RAISE EXCEPTION
                'Character % has insufficient authoritative kit-bag slots',
                v_character_id;
        END IF;

        v_new_stack := LEAST(v_remaining, 99)::smallint;
        INSERT INTO public.character_items (
            user_id, item_location, slot_index, prop_id,
            item_quality, item_grade, bound, stack,
            item_exp, holy_suit_code)
        VALUES (
            v_character_id, 1, v_open_slot, 11000,
            1, 1, 0, v_new_stack,
            0, 0);
        INSERT INTO fairy_feather_grant_mutations (
            action, slot_index, old_item, old_stack, new_stack)
        VALUES ('add', v_open_slot, NULL, 0, v_new_stack);
        v_remaining := v_remaining - v_new_stack;
    END LOOP;

    IF EXISTS (SELECT 1 FROM fairy_feather_grant_mutations) THEN
        UPDATE public.character_base character_row
        SET inventory_revision = character_row.inventory_revision + 1
        WHERE character_row.id = v_character_id
          AND character_row.inventory_revision = v_inventory_revision
        RETURNING character_row.inventory_revision
        INTO v_inventory_revision;
        IF NOT FOUND THEN
            RAISE EXCEPTION
                'Character % inventory revision changed during grant',
                v_character_id;
        END IF;
        UPDATE fairy_feather_grant_context
        SET new_inventory_revision = v_inventory_revision,
            changed = true;
    ELSE
        UPDATE fairy_feather_grant_context
        SET new_inventory_revision = old_inventory_revision;
    END IF;

    UPDATE fairy_feather_grant_context context
    SET current_total = (
        SELECT COALESCE(sum(item.stack), 0)::integer
        FROM public.character_items item
        WHERE item.user_id = context.character_id
          AND item.item_location = 1
          AND item.prop_id = 11000);
    IF NOT EXISTS (
        SELECT 1
        FROM fairy_feather_grant_context
        WHERE current_total = $TargetQuantity
          AND new_inventory_revision = old_inventory_revision +
              CASE WHEN changed THEN 1 ELSE 0 END)
    THEN
        RAISE EXCEPTION
            'Fairy''s Feather grant failed post-write validation';
    END IF;
END
`$grant`$;

INSERT INTO public.character_item_audit (
    source, action, user_id, item_location, slot_index,
    prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'localdev-fairy-feather-fixture',
       mutation.action,
       context.character_id,
       1,
       mutation.slot_index,
       11000,
       1,
       1,
       0,
       mutation.old_item
FROM fairy_feather_grant_mutations mutation
CROSS JOIN fairy_feather_grant_context context
ORDER BY mutation.ordinal;

COMMIT;

SELECT 'FAIRY_FEATHER_GRANT_RESULT|' || jsonb_build_object(
    'accountId', account_id,
    'characterId', character_id,
    'characterName', character_name,
    'itemId', 11000,
    'itemName', 'Fairy''s Feather',
    'publishedRevision', publication_revision,
    'previousQuantity', previous_total,
    'currentQuantity', current_total,
    'inventoryRevisionBefore', old_inventory_revision,
    'inventoryRevisionAfter', new_inventory_revision,
    'changed', changed,
    'mutations', (SELECT count(*) FROM fairy_feather_grant_mutations),
    'stacks', COALESCE((
        SELECT jsonb_agg(jsonb_build_object(
            'slot', mutation.slot_index,
            'quantity', mutation.new_stack)
            ORDER BY mutation.slot_index)
        FROM fairy_feather_grant_mutations mutation
    ), '[]'::jsonb))::text
FROM fairy_feather_grant_context;
"@

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
$exitCode = $LASTEXITCODE
$lines = @($output | ForEach-Object { $_.ToString() })
if ($exitCode -ne 0) {
    throw (
        "Fairy's Feather grant failed and rolled back:`n" +
        ($lines -join "`n"))
}
$prefix = 'FAIRY_FEATHER_GRANT_RESULT|'
$result = $lines | Where-Object {
    $_.StartsWith($prefix)
} | Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($result)) {
    throw "The Fairy's Feather grant returned no verification receipt."
}
$result.Substring($prefix.Length) | ConvertFrom-Json
