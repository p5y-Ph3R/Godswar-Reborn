[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateRange(1, [int]::MaxValue)][int]$AccountId = 13,
    [ValidateNotNullOrEmpty()][string]$CharacterName = 'test2',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$PostgresContainer = 'godswar-dev-postgres',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$ServerContainer = 'godswar-dev-server',
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = 'godswar',
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$DatabaseUser = 'godswar'
)

# Local-development fixture only. The target must be offline. This clears only
# the 96-slot kit bag, preserves equipment/storage, grants the reviewed Pet
# Manager materials, Phoenix's Feathers, and five quality-specific Rock Elf
# eggs, records each
# mutation, and advances inventory_revision exactly once.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($CharacterName.Length -gt 32 -or $CharacterName.Contains([char]0)) {
    throw 'CharacterName must contain 1 to 32 non-NUL characters.'
}
if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required but was not found on PATH.'
}

function Get-ContainerState([string]$Name, [switch]$AllowMissing) {
    $output = & docker container inspect --format '{{json .State}}' $Name 2>&1
    if ($LASTEXITCODE -ne 0) {
        if ($AllowMissing) { return $null }
        throw "Could not inspect Docker container '$Name': $output"
    }
    try { ($output -join "`n").Trim() | ConvertFrom-Json }
    catch { throw "Docker returned invalid state for '$Name'." }
}

$serverState = Get-ContainerState $ServerContainer -AllowMissing
if ($null -ne $serverState -and $serverState.Running) {
    throw "Refusing the offline pet fixture while '$ServerContainer' is running."
}
$postgresState = Get-ContainerState $PostgresContainer
if (-not $postgresState.Running) {
    throw "PostgreSQL '$PostgresContainer' is not running."
}
$health = $postgresState.PSObject.Properties['Health']
if ($null -ne $health -and $null -ne $health.Value -and
    $health.Value.Status -ne 'healthy') {
    throw "PostgreSQL '$PostgresContainer' is not healthy."
}

$target = "account $AccountId / character '$CharacterName'"
if (-not $PSCmdlet.ShouldProcess(
        $target,
        'Clear kit bag and install Pet Manager test kit')) { return }
$safeName = $CharacterName.Replace("'", "''")

$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE desired_pet_manager_test_kit (
    ordinal smallint PRIMARY KEY,
    prop_id integer NOT NULL,
    item_quality smallint NOT NULL,
    stack smallint NOT NULL,
    expected_name text NOT NULL,
    expected_overlap smallint NOT NULL
);
INSERT INTO desired_pet_manager_test_kit
    (ordinal, prop_id, item_quality, stack, expected_name, expected_overlap)
VALUES
    (0, 10099, 1, 99, 'Pet Enhance Spring', 99),
    (1, 10100, 1, 99, 'Golden Apple Juice', 99),
    (2, 10101, 1, 99, 'Strong Purge Potion', 99),
    (3, 10102, 1, 99, 'Weak Purge Potion', 99),
    (4, 11005, 1, 99, 'Phoenix''s Feather', 99),
    (5, 10150, 6, 1, 'Rock Elf Egg', 1),
    (6, 10150, 7, 1, 'Rock Elf Egg', 1),
    (7, 10150, 8, 1, 'Rock Elf Egg', 1),
    (8, 10150, 9, 1, 'Rock Elf Egg', 1),
    (9, 10150, 10, 1, 'Rock Elf Egg', 1);

CREATE TEMP TABLE pet_manager_test_context (
    account_id integer NOT NULL,
    character_id integer NOT NULL,
    character_name text NOT NULL,
    old_inventory_revision bigint NOT NULL,
    new_inventory_revision bigint,
    publication_revision text NOT NULL
);
CREATE TEMP TABLE pet_manager_test_mutations (
    ordinal bigint GENERATED ALWAYS AS IDENTITY,
    action text NOT NULL,
    slot_index smallint NOT NULL,
    prop_id integer NOT NULL,
    item_quality smallint NOT NULL,
    old_item jsonb
);

DO `$context`$
DECLARE
    v_character_id integer;
    v_inventory_revision bigint;
    v_checkpoint_owner uuid;
    v_login_status smallint;
    v_revision text;
    v_invalid text;
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
    JOIN public.accounts account ON account.id = character_row.account_id
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

    SELECT publication.revision INTO v_revision
    FROM public.item_template_content_publication publication
    JOIN public.item_template_content_revisions revision
      ON revision.revision = publication.revision
    WHERE publication.family = 'items'
      AND revision.sealed_at IS NOT NULL;
    IF v_revision IS NULL THEN
        RAISE EXCEPTION 'No sealed item publication is active';
    END IF;

    SELECT string_agg(
               desired.prop_id::text || '/q' || desired.item_quality::text,
               ', ' ORDER BY desired.ordinal)
    INTO v_invalid
    FROM desired_pet_manager_test_kit desired
    LEFT JOIN public.item_template_content_definitions definition
      ON definition.revision = v_revision
     AND definition.id = desired.prop_id
    LEFT JOIN public.item_templates mutable
      ON mutable.id = desired.prop_id
    WHERE definition.id IS NULL
       OR mutable.id IS NULL
       OR definition.display_name <> desired.expected_name
       OR definition.kind <> 'consume item'
       OR COALESCE((definition.stats ->> 'Overlap')::smallint, 0)
            <> desired.expected_overlap;
    IF v_invalid IS NOT NULL THEN
        RAISE EXCEPTION
            'Published Pet Manager item policy mismatch: %', v_invalid;
    END IF;

    INSERT INTO pet_manager_test_context (
        account_id, character_id, character_name,
        old_inventory_revision, publication_revision)
    VALUES (
        $AccountId, v_character_id, '$safeName',
        v_inventory_revision, v_revision);
END
`$context`$;

WITH deleted AS (
    DELETE FROM public.character_items item
    USING pet_manager_test_context context
    WHERE item.user_id = context.character_id
      AND item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95
    RETURNING item.*
)
INSERT INTO pet_manager_test_mutations (
    action, slot_index, prop_id, item_quality, old_item)
SELECT 'clear', slot_index, prop_id, item_quality, to_jsonb(deleted)
FROM deleted;

WITH inserted AS (
    INSERT INTO public.character_items (
        user_id, item_location, slot_index, prop_id,
        item_quality, item_grade, bound, stack, item_exp, holy_suit_code)
    SELECT context.character_id,
           1,
           desired.ordinal,
           desired.prop_id,
           desired.item_quality,
           1,
           0,
           desired.stack,
           0,
           0
    FROM desired_pet_manager_test_kit desired
    CROSS JOIN pet_manager_test_context context
    ORDER BY desired.ordinal
    RETURNING slot_index, prop_id, item_quality
)
INSERT INTO pet_manager_test_mutations (
    action, slot_index, prop_id, item_quality, old_item)
SELECT 'add', slot_index, prop_id, item_quality, NULL
FROM inserted;

WITH advanced AS (
    UPDATE public.character_base character_row
    SET inventory_revision = character_row.inventory_revision + 1
    FROM pet_manager_test_context context
    WHERE character_row.id = context.character_id
      AND character_row.inventory_revision = context.old_inventory_revision
    RETURNING character_row.inventory_revision
)
UPDATE pet_manager_test_context context
SET new_inventory_revision = advanced.inventory_revision
FROM advanced;

DO `$verify`$
DECLARE v_character_id integer;
BEGIN
    SELECT character_id INTO STRICT v_character_id
    FROM pet_manager_test_context;
    IF NOT EXISTS (
        SELECT 1
        FROM pet_manager_test_context
        WHERE new_inventory_revision = old_inventory_revision + 1)
    THEN
        RAISE EXCEPTION 'Inventory revision did not advance exactly once';
    END IF;
    IF (SELECT count(*) FROM public.character_items
        WHERE user_id = v_character_id AND item_location = 1) <> 10
    THEN
        RAISE EXCEPTION 'The prepared kit bag does not contain exactly 10 rows';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM desired_pet_manager_test_kit desired
        LEFT JOIN public.character_items item
          ON item.user_id = v_character_id
         AND item.item_location = 1
         AND item.slot_index = desired.ordinal
         AND item.prop_id = desired.prop_id
         AND item.item_quality = desired.item_quality
         AND item.stack = desired.stack
        WHERE item.id IS NULL)
    THEN
        RAISE EXCEPTION 'The prepared kit bag differs from the requested fixture';
    END IF;
END
`$verify`$;

INSERT INTO public.character_item_audit (
    source, action, user_id, item_location, slot_index,
    prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'localdev-pet-manager-test-kit',
       mutation.action,
       context.character_id,
       1,
       mutation.slot_index,
       mutation.prop_id,
       mutation.item_quality,
       1,
       0,
       mutation.old_item
FROM pet_manager_test_mutations mutation
CROSS JOIN pet_manager_test_context context
ORDER BY mutation.ordinal;

COMMIT;
SELECT 'PET_MANAGER_TEST_KIT_RESULT|' || jsonb_build_object(
    'accountId', context.account_id,
    'characterId', context.character_id,
    'characterName', context.character_name,
    'publishedRevision', context.publication_revision,
    'inventoryRevisionBefore', context.old_inventory_revision,
    'inventoryRevisionAfter', context.new_inventory_revision,
    'clearedRows', (
        SELECT count(*) FROM pet_manager_test_mutations
        WHERE action = 'clear'),
    'items', (
        SELECT jsonb_agg(jsonb_build_object(
            'slot', item.slot_index,
            'itemId', item.prop_id,
            'name', definition.display_name,
            'quality', item.item_quality,
            'stack', item.stack)
            ORDER BY item.slot_index)
        FROM public.character_items item
        JOIN public.item_template_content_definitions definition
          ON definition.revision = context.publication_revision
         AND definition.id = item.prop_id
        WHERE item.user_id = context.character_id
          AND item.item_location = 1))::text
FROM pet_manager_test_context context;
"@

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
$exitCode = $LASTEXITCODE
$lines = @($output | ForEach-Object { $_.ToString() })
if ($exitCode -ne 0) {
    throw "Pet Manager test-kit transaction failed and rolled back:`n$($lines -join "`n")"
}
$result = $lines | Where-Object {
    $_.StartsWith('PET_MANAGER_TEST_KIT_RESULT|')
} | Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($result)) {
    throw 'The Pet Manager test-kit transaction returned no receipt.'
}
$result.Substring('PET_MANAGER_TEST_KIT_RESULT|'.Length) | ConvertFrom-Json
