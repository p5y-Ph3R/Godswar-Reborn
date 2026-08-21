[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateRange(1, [int]::MaxValue)][int]$AccountId = 13,
    [ValidateNotNullOrEmpty()][string]$CharacterName = 'test2'
)

# Local-development fixture only. It reconciles an offline character to six
# bound, nonstacking Special Pet Sheds without replacing unrelated bag items.
# Every mutation is audited, and inventory_revision advances once only when
# the authoritative inventory actually changes.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($CharacterName.Length -gt 32 -or $CharacterName.Contains([char]0)) {
    throw 'CharacterName must contain 1 to 32 non-NUL characters.'
}
if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required but was not found on PATH.'
}

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentStack.Common.psm1'
) -Force

$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-tempest-openworld-01'
$database = 'godswar'
$databaseUser = 'godswar'
$itemId = 4109
$desiredCount = 6

$postgres = Assert-DevelopmentContainer $postgresContainer 'postgres'
$server = Assert-DevelopmentContainer $serverContainer 'server'
if ([bool]$server.State.Running) {
    throw "Refusing the offline fixture while '$serverContainer' is running."
}
if (-not [bool]$postgres.State.Running) {
    throw "PostgreSQL '$postgresContainer' is not running."
}
$health = $postgres.State.PSObject.Properties['Health']
if ($null -ne $health -and $null -ne $health.Value -and
    $health.Value.Status -cne 'healthy') {
    throw "PostgreSQL '$postgresContainer' is not healthy."
}
$dataMounts = @($postgres.Mounts | Where-Object {
    $_.Destination -ceq '/var/lib/postgresql/data'
})
if ($dataMounts.Count -ne 1 -or
    $dataMounts[0].Name -cne 'godswar-dev-postgres-data') {
    throw 'Development PostgreSQL is not using its isolated data volume.'
}

$target = "account $AccountId / character '$CharacterName'"
if (-not $PSCmdlet.ShouldProcess(
        $target,
        "Reconcile $desiredCount Special Pet Shed items")) { return }
$safeName = $CharacterName.Replace("'", "''")

$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE pet_shed_grant_context (
    account_id integer NOT NULL,
    character_id integer NOT NULL,
    character_name text NOT NULL,
    old_inventory_revision bigint NOT NULL,
    new_inventory_revision bigint,
    publication_revision text NOT NULL,
    copies_before integer NOT NULL,
    empty_slots_before integer NOT NULL
);
CREATE TEMP TABLE pet_shed_grant_mutations (
    ordinal bigint GENERATED ALWAYS AS IDENTITY,
    action text NOT NULL,
    slot_index smallint NOT NULL,
    old_item jsonb
);

DO `$context`$
DECLARE
    v_character_id integer;
    v_inventory_revision bigint;
    v_checkpoint_owner uuid;
    v_login_status smallint;
    v_revision text;
    v_copies integer;
    v_empty_slots integer;
BEGIN
    SELECT character_row.id,
           character_row.inventory_revision,
           character_row.checkpoint_owner_id,
           account.login_status
    INTO v_character_id,
         v_inventory_revision,
         v_checkpoint_owner,
         v_login_status
    FROM public.character_base character_row
    JOIN public.accounts account
      ON account.id = character_row.account_id
    WHERE character_row.account_id = $AccountId
      AND character_row.name = '$safeName'
    FOR UPDATE OF character_row, account;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Character % on account % does not exist',
            '$safeName', $AccountId;
    END IF;
    IF v_checkpoint_owner IS NOT NULL OR v_login_status <> 0 THEN
        RAISE EXCEPTION
            'Character % is not safely offline (owner %, login status %)',
            v_character_id, v_checkpoint_owner, v_login_status;
    END IF;

    PERFORM 1
    FROM public.character_items
    WHERE user_id = v_character_id
      AND item_location = 1
      AND slot_index BETWEEN 0 AND 95
    ORDER BY slot_index
    FOR UPDATE;

    SELECT publication.revision
    INTO v_revision
    FROM public.item_template_content_publication publication
    JOIN public.item_template_content_revisions revision
      ON revision.revision = publication.revision
     AND revision.sealed_at IS NOT NULL
    WHERE publication.family = 'items';
    IF v_revision IS NULL THEN
        RAISE EXCEPTION 'No sealed item publication is active';
    END IF;

    PERFORM 1
    FROM public.item_template_content_definitions definition
    JOIN public.item_templates mutable ON mutable.id = definition.id
    WHERE definition.revision = v_revision
      AND definition.id = $itemId
      AND definition.kind = 'consume item'
      AND definition.name_key = 'AddPetNum'
      AND definition.display_name = 'Special Pet Shed'
      AND definition.equipment_slot = 0
      AND cardinality(definition.class_ids) = 0
      AND definition.min_level IS NULL
      AND definition.max_level IS NULL
      AND definition.hand IS NULL
      AND definition.skill_flag IS NULL
      AND definition.texture =
          './Localization/en_us/UI/Texture/Icon2.gwo'
      AND definition.icon = '432,972'
      AND definition.stats @> jsonb_build_object(
          'ID', '4109',
          'Type', 'consume item',
          'Random', '0',
          'Distribution', '0,0',
          'Money', '0',
          'Overlap', '1',
          'Use', '1',
          'BindType', '1',
          'Skill', '4720',
          'Mode', '4');
    IF NOT FOUND THEN
        RAISE EXCEPTION
            'Published Special Pet Shed definition/policy is missing or invalid';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
          AND item.prop_id = $itemId
          AND (item.bound <> 1 OR item.stack <> 1 OR
               item.item_quality <> 1 OR item.item_grade <> 1 OR
               item.item_exp <> 0 OR item.holy_suit_code <> 0))
    THEN
        RAISE EXCEPTION
            'Existing Special Pet Shed rows violate the bound nonstacking policy';
    END IF;

    SELECT count(*)::integer INTO v_copies
    FROM public.character_items
    WHERE user_id = v_character_id
      AND item_location = 1
      AND slot_index BETWEEN 0 AND 95
      AND prop_id = $itemId;
    SELECT count(*)::integer INTO v_empty_slots
    FROM generate_series(0, 95) slot(slot_index)
    WHERE NOT EXISTS (
        SELECT 1
        FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.slot_index = slot.slot_index);

    INSERT INTO pet_shed_grant_context (
        account_id, character_id, character_name,
        old_inventory_revision, publication_revision,
        copies_before, empty_slots_before)
    VALUES (
        $AccountId, v_character_id, '$safeName',
        v_inventory_revision, v_revision, v_copies, v_empty_slots);
END
`$context`$;

WITH ranked AS MATERIALIZED (
    SELECT item.id,
           row_number() OVER (ORDER BY item.slot_index, item.id) AS rank
    FROM public.character_items item
    JOIN pet_shed_grant_context context
      ON context.character_id = item.user_id
    WHERE item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95
      AND item.prop_id = $itemId
), deleted AS (
    DELETE FROM public.character_items item
    USING ranked
    WHERE item.id = ranked.id AND ranked.rank > $desiredCount
    RETURNING item.*
)
INSERT INTO pet_shed_grant_mutations (action, slot_index, old_item)
SELECT 'delete_excess', slot_index, to_jsonb(deleted)
FROM deleted;

DO `$capacity`$
DECLARE v_character_id integer; v_copies integer; v_empty integer;
BEGIN
    SELECT character_id INTO STRICT v_character_id
    FROM pet_shed_grant_context;
    SELECT count(*)::integer INTO v_copies
    FROM public.character_items
    WHERE user_id = v_character_id
      AND item_location = 1
      AND slot_index BETWEEN 0 AND 95
      AND prop_id = $itemId;
    SELECT count(*)::integer INTO v_empty
    FROM generate_series(0, 95) slot(slot_index)
    WHERE NOT EXISTS (
        SELECT 1 FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.slot_index = slot.slot_index);
    IF $desiredCount - v_copies > v_empty THEN
        RAISE EXCEPTION
            'Special Pet Shed grant needs % empty bag slots but only % exist',
            $desiredCount - v_copies, v_empty;
    END IF;
END
`$capacity`$;

WITH current_count AS MATERIALIZED (
    SELECT count(*)::integer AS value
    FROM public.character_items item
    JOIN pet_shed_grant_context context
      ON context.character_id = item.user_id
    WHERE item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95
      AND item.prop_id = $itemId
), missing AS MATERIALIZED (
    SELECT ordinal,
           row_number() OVER (ORDER BY ordinal) AS row_number
    FROM current_count,
         generate_series(1, $desiredCount) ordinal
    WHERE ordinal > current_count.value
), empty_slots AS MATERIALIZED (
    SELECT slot_index::smallint,
           row_number() OVER (ORDER BY slot_index) AS row_number
    FROM generate_series(0, 95) slot(slot_index)
    CROSS JOIN pet_shed_grant_context context
    WHERE NOT EXISTS (
        SELECT 1 FROM public.character_items item
        WHERE item.user_id = context.character_id
          AND item.item_location = 1
          AND item.slot_index = slot.slot_index)
), inserted AS (
    INSERT INTO public.character_items (
        user_id, item_location, slot_index, prop_id,
        item_quality, item_grade, bound, stack, item_exp, holy_suit_code)
    SELECT context.character_id, 1, empty_slots.slot_index, $itemId,
           1, 1, 1, 1, 0, 0
    FROM missing
    JOIN empty_slots USING (row_number)
    CROSS JOIN pet_shed_grant_context context
    ORDER BY missing.ordinal
    RETURNING slot_index
)
INSERT INTO pet_shed_grant_mutations (action, slot_index, old_item)
SELECT 'add', slot_index, NULL FROM inserted;

UPDATE pet_shed_grant_context
SET new_inventory_revision = old_inventory_revision;

WITH mutation_state AS (
    SELECT count(*) AS mutation_count FROM pet_shed_grant_mutations
), advanced AS (
    UPDATE public.character_base character_row
    SET inventory_revision = character_row.inventory_revision + 1
    FROM pet_shed_grant_context context, mutation_state
    WHERE character_row.id = context.character_id
      AND character_row.inventory_revision = context.old_inventory_revision
      AND mutation_state.mutation_count > 0
    RETURNING character_row.inventory_revision
)
UPDATE pet_shed_grant_context context
SET new_inventory_revision = advanced.inventory_revision
FROM advanced;

DO `$verify`$
DECLARE v_character_id integer; v_count integer;
BEGIN
    SELECT character_id INTO STRICT v_character_id
    FROM pet_shed_grant_context;
    SELECT count(*)::integer INTO v_count
    FROM public.character_items
    WHERE user_id = v_character_id
      AND item_location = 1
      AND slot_index BETWEEN 0 AND 95
      AND prop_id = $itemId
      AND bound = 1 AND stack = 1;
    IF v_count <> $desiredCount THEN
        RAISE EXCEPTION
            'Expected % bound nonstacking sheds; found %',
            $desiredCount, v_count;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pet_shed_grant_context
        WHERE new_inventory_revision = old_inventory_revision +
            CASE WHEN EXISTS (
                SELECT 1 FROM pet_shed_grant_mutations)
            THEN 1 ELSE 0 END)
    THEN
        RAISE EXCEPTION
            'Inventory revision did not follow the mutation count';
    END IF;
END
`$verify`$;

INSERT INTO public.character_item_audit (
    source, action, user_id, item_location, slot_index,
    prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'localdev-special-pet-shed-grant', mutation.action,
       context.character_id, 1, mutation.slot_index,
       $itemId, 1, 1, 0, mutation.old_item
FROM pet_shed_grant_mutations mutation
CROSS JOIN pet_shed_grant_context context
ORDER BY mutation.ordinal;

COMMIT;
SELECT 'SPECIAL_PET_SHED_GRANT_RESULT|' || jsonb_build_object(
    'accountId', context.account_id,
    'characterId', context.character_id,
    'characterName', context.character_name,
    'itemId', $itemId,
    'itemName', 'Special Pet Shed',
    'publishedRevision', context.publication_revision,
    'copiesBefore', context.copies_before,
    'copiesAfter', (
        SELECT count(*) FROM public.character_items item
        WHERE item.user_id = context.character_id
          AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
          AND item.prop_id = $itemId),
    'emptySlotsBefore', context.empty_slots_before,
    'inventoryRevisionBefore', context.old_inventory_revision,
    'inventoryRevisionAfter', context.new_inventory_revision,
    'mutationCount', (SELECT count(*) FROM pet_shed_grant_mutations),
    'slots', (
        SELECT jsonb_agg(item.slot_index ORDER BY item.slot_index)
        FROM public.character_items item
        WHERE item.user_id = context.character_id
          AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
          AND item.prop_id = $itemId))::text
FROM pet_shed_grant_context context;
"@

$output = $sql | & docker exec -i $postgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $databaseUser -d $database 2>&1
$exitCode = $LASTEXITCODE
$lines = @($output | ForEach-Object { $_.ToString() })
if ($exitCode -ne 0) {
    throw "Special Pet Shed grant failed and rolled back:`n$($lines -join "`n")"
}
$prefix = 'SPECIAL_PET_SHED_GRANT_RESULT|'
$result = $lines | Where-Object {
    $_.StartsWith($prefix)
} | Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($result)) {
    throw 'The Special Pet Shed grant returned no verification receipt.'
}
$result.Substring($prefix.Length) | ConvertFrom-Json
