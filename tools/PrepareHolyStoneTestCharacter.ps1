[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$AccountId = 13,

    [ValidateNotNullOrEmpty()]
    [string]$CharacterName = 'test2',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$PostgresContainer = 'godswar-postgres',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$ServerContainer = 'godswar-server',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = 'godswar',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$DatabaseUser = 'godswar'
)

# LocalDevelopment fixture only. The operation is deliberately offline,
# bounded to one named character, auditable, and atomic. It clears only the
# 96-slot carried bag; storage and equipped items are never deleted.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($CharacterName.Length -gt 32 -or $CharacterName.Contains([char]0)) {
    throw 'CharacterName must contain 1 to 32 non-NUL characters.'
}
if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required but was not found on PATH.'
}

function Get-ContainerState {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [switch]$AllowMissing
    )

    $output = & docker container inspect --format '{{json .State}}' $Name 2>&1
    if ($LASTEXITCODE -ne 0) {
        if ($AllowMissing) { return $null }
        throw "Could not inspect Docker container '$Name': $output"
    }
    try { return (($output -join "`n").Trim() | ConvertFrom-Json) }
    catch { throw "Docker returned invalid state for '$Name'." }
}

$serverState = Get-ContainerState -Name $ServerContainer -AllowMissing
if ($null -ne $serverState -and $serverState.Running) {
    throw "Refusing the offline fixture while '$ServerContainer' is running."
}
$postgresState = Get-ContainerState -Name $PostgresContainer
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
        'Clear carried bag, open four empty gear sockets, and grant test kit')) {
    return
}

$safeName = $CharacterName.Replace("'", "''")
$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE requested_holy_stone_items (
    ordinal smallint PRIMARY KEY,
    prop_id integer NOT NULL,
    desired_stack smallint NOT NULL,
    desired_grade smallint NOT NULL,
    desired_bound smallint NOT NULL,
    label text NOT NULL
);
INSERT INTO requested_holy_stone_items VALUES
    (0, 9030, 1, 1, 1, 'Level 1 Heated Holy Stone #1'),
    (1, 9030, 1, 1, 1, 'Level 1 Heated Holy Stone #2'),
    (2, 9030, 1, 1, 1, 'Level 1 Heated Holy Stone #3'),
    (3, 9030, 1, 1, 1, 'Level 1 Heated Holy Stone #4'),
    (4, 9030, 1, 1, 1, 'Level 1 Heated Holy Stone #5'),
    (5, 9040, 99, 1, 0, 'Level 1 Eclipse Stone'),
    (6, 9041, 99, 1, 0, 'Level 2 Eclipse Stone'),
    (7, 9042, 99, 1, 0, 'Level 3 Eclipse Stone'),
    (8, 9050, 99, 1, 0, 'Goddess'' Stone'),
    (9, 9051, 99, 1, 0, 'Copper Evasion Signet'),
    (10, 9052, 99, 1, 0, 'Silver Evasion Signet'),
    (11, 9053, 99, 1, 0, 'Gold Evasion Signet 6-to-7'),
    (12, 9054, 99, 1, 0, 'Gold Evasion Signet 7-to-8'),
    (13, 9055, 99, 1, 0, 'Gold Evasion Signet 8-to-9'),
    (14, 9056, 99, 1, 0, 'Gold Evasion Signet 9-to-10'),
    (15, 9060, 99, 1, 0, 'Fire Spirit of Destruction'),
    (16, 9061, 99, 1, 0, 'Fire Spirit of Penetration'),
    (17, 9062, 99, 1, 0, 'Fire Spirit of Fist'),
    (18, 9063, 99, 1, 0, 'Fire Spirit of Fiery'),
    (19, 9064, 99, 1, 0, 'Fire Spirit of Blood'),
    (20, 9065, 99, 1, 0, 'Fire Spirit of Pressure'),
    (21, 9066, 99, 1, 0, 'Fire Spirit of Assail'),
    (22, 9067, 99, 1, 0, 'Fire Spirit of Lightning'),
    (23, 9088, 99, 1, 0, 'Fire Spirit of Flow'),
    (24, 9089, 99, 1, 0, 'Fire Spirit of Tranquility'),
    (25, 9080, 99, 1, 0, 'Water Spirit of Darkness'),
    (26, 9081, 99, 1, 0, 'Water Spirit of Mist'),
    (27, 9082, 99, 1, 0, 'Water Spirit of Silence'),
    (28, 9083, 99, 1, 0, 'Water Spirit of Chillness'),
    (29, 9084, 99, 1, 0, 'Water Spirit of Ice'),
    (30, 9085, 99, 1, 0, 'Water Spirit of Frost'),
    (31, 9086, 99, 1, 0, 'Water Spirit of Intent'),
    (32, 9087, 99, 1, 0, 'Water Spirit of Resilience'),
    (33, 9068, 99, 1, 0, 'Water Spirit of Renewal'),
    (34, 9069, 99, 1, 0, 'Water Spirit of Vitality');

CREATE TEMP TABLE holy_stone_fixture_context (
    account_id integer NOT NULL,
    character_id integer NOT NULL,
    character_name text NOT NULL,
    old_inventory_revision bigint NOT NULL,
    new_inventory_revision bigint,
    publication_revision text NOT NULL
);
CREATE TEMP TABLE holy_stone_fixture_mutations (
    ordinal bigint GENERATED ALWAYS AS IDENTITY,
    action text NOT NULL,
    item_instance_id bigint,
    item_location smallint NOT NULL,
    slot_index smallint NOT NULL,
    prop_id integer,
    item_quality smallint,
    item_grade smallint,
    item_exp integer,
    old_item jsonb
);

DO `$context`$
DECLARE
    v_character_id integer;
    v_inventory_revision bigint;
    v_checkpoint_owner uuid;
    v_publication_revision text;
    v_missing text;
    v_gear_count integer;
BEGIN
    SELECT id, inventory_revision, checkpoint_owner_id
    INTO v_character_id, v_inventory_revision, v_checkpoint_owner
    FROM public.character_base
    WHERE account_id = $AccountId AND name = '$safeName'
    FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Character % on account % does not exist',
            '$safeName', $AccountId;
    END IF;
    IF v_checkpoint_owner IS NOT NULL THEN
        RAISE EXCEPTION 'Character % has active checkpoint owner %',
            v_character_id, v_checkpoint_owner;
    END IF;

    PERFORM 1 FROM public.character_items
    WHERE user_id = v_character_id
    ORDER BY item_location, slot_index FOR UPDATE;

    SELECT publication.revision INTO v_publication_revision
    FROM public.item_template_content_publication publication
    JOIN public.item_template_content_revisions revision
      ON revision.revision = publication.revision
    WHERE publication.family = 'items'
      AND revision.sealed_at IS NOT NULL
      AND revision.manifest_version = 9;
    IF v_publication_revision IS NULL THEN
        RAISE EXCEPTION 'No sealed v9 item publication is active';
    END IF;

    SELECT string_agg(expected.prop_id::text, ', ' ORDER BY expected.prop_id)
    INTO v_missing
    FROM (SELECT DISTINCT prop_id FROM requested_holy_stone_items) expected
    LEFT JOIN public.item_templates mutable ON mutable.id = expected.prop_id
    LEFT JOIN public.item_template_content_definitions definition
      ON definition.revision = v_publication_revision
     AND definition.id = expected.prop_id
    WHERE mutable.id IS NULL OR definition.id IS NULL;
    IF v_missing IS NOT NULL THEN
        RAISE EXCEPTION 'Holy Stone templates are not fully published: %',
            v_missing;
    END IF;

    SELECT count(*) INTO v_gear_count
    FROM public.character_items item
    JOIN public.item_templates template ON template.id = item.prop_id
    WHERE item.user_id = v_character_id
      AND item.item_location = 0
      AND item.slot_index BETWEEN 0 AND 11
      AND template.kind IN (
          'head', 'amulet', 'glove', 'armor', 'cuff', 'girdle',
          'shoes', 'leggins', 'ring', 'weapon', 'shield');
    IF v_gear_count <> 12 THEN
        RAISE EXCEPTION 'Expected 12 character-gear rows, found %', v_gear_count;
    END IF;

    INSERT INTO holy_stone_fixture_context (
        account_id, character_id, character_name,
        old_inventory_revision, publication_revision)
    VALUES ($AccountId, v_character_id, '$safeName',
            v_inventory_revision, v_publication_revision);
END
`$context`$;

WITH deleted AS (
    DELETE FROM public.character_items item
    USING holy_stone_fixture_context context
    WHERE item.user_id = context.character_id
      AND item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95
    RETURNING item.*
)
INSERT INTO holy_stone_fixture_mutations (
    action, item_instance_id, item_location, slot_index, prop_id,
    item_quality, item_grade, item_exp, old_item)
SELECT 'clear_bag', id, item_location, slot_index, prop_id,
       item_quality, item_grade, item_exp, to_jsonb(deleted)
FROM deleted;

WITH before_state AS MATERIALIZED (
    SELECT item.id, to_jsonb(item) AS old_item
    FROM public.character_items item
    JOIN holy_stone_fixture_context context ON context.character_id = item.user_id
    JOIN public.item_templates template ON template.id = item.prop_id
    WHERE item.item_location = 0
      AND item.slot_index BETWEEN 0 AND 11
      AND template.kind IN (
          'head', 'amulet', 'glove', 'armor', 'cuff', 'girdle',
          'shoes', 'leggins', 'ring', 'weapon', 'shield')
      AND (item.holy_socket_count <> 4
        OR item.holy_socket1_effect_id IS NOT NULL
        OR item.holy_socket1_level IS NOT NULL
        OR item.holy_socket2_effect_id IS NOT NULL
        OR item.holy_socket2_level IS NOT NULL
        OR item.holy_socket3_effect_id IS NOT NULL
        OR item.holy_socket3_level IS NOT NULL
        OR item.holy_socket4_effect_id IS NOT NULL
        OR item.holy_socket4_level IS NOT NULL
        OR item.holy_socket5_effect_id IS NOT NULL
        OR item.holy_socket5_level IS NOT NULL
        OR item.holy_socket6_effect_id IS NOT NULL
        OR item.holy_socket6_level IS NOT NULL)
), updated AS (
    UPDATE public.character_items item
    SET holy_socket_count = 4,
        holy_socket1_effect_id = NULL, holy_socket1_level = NULL,
        holy_socket2_effect_id = NULL, holy_socket2_level = NULL,
        holy_socket3_effect_id = NULL, holy_socket3_level = NULL,
        holy_socket4_effect_id = NULL, holy_socket4_level = NULL,
        holy_socket5_effect_id = NULL, holy_socket5_level = NULL,
        holy_socket6_effect_id = NULL, holy_socket6_level = NULL,
        updated_at = now()
    FROM before_state
    WHERE item.id = before_state.id
    RETURNING item.*
)
INSERT INTO holy_stone_fixture_mutations (
    action, item_instance_id, item_location, slot_index, prop_id,
    item_quality, item_grade, item_exp, old_item)
SELECT 'open_four_sockets', updated.id, updated.item_location,
       updated.slot_index, updated.prop_id, updated.item_quality,
       updated.item_grade, updated.item_exp, before_state.old_item
FROM updated JOIN before_state ON before_state.id = updated.id;

WITH inserted AS (
    INSERT INTO public.character_items (
        user_id, item_location, slot_index, prop_id,
        item_quality, item_grade, bound, stack, item_exp, holy_suit_code)
    SELECT context.character_id, 1, requested.ordinal,
           requested.prop_id, 1, requested.desired_grade,
           requested.desired_bound, requested.desired_stack, 0, 0
    FROM requested_holy_stone_items requested
    CROSS JOIN holy_stone_fixture_context context
    ORDER BY requested.ordinal
    RETURNING *
)
INSERT INTO holy_stone_fixture_mutations (
    action, item_instance_id, item_location, slot_index, prop_id,
    item_quality, item_grade, item_exp, old_item)
SELECT 'grant', id, item_location, slot_index, prop_id,
       item_quality, item_grade, item_exp, NULL
FROM inserted;

UPDATE holy_stone_fixture_context
SET new_inventory_revision = old_inventory_revision;
WITH advanced AS (
    UPDATE public.character_base character_row
    SET inventory_revision = character_row.inventory_revision + 1
    FROM holy_stone_fixture_context context
    WHERE character_row.id = context.character_id
      AND character_row.inventory_revision = context.old_inventory_revision
      AND EXISTS (SELECT 1 FROM holy_stone_fixture_mutations)
    RETURNING character_row.inventory_revision
)
UPDATE holy_stone_fixture_context context
SET new_inventory_revision = advanced.inventory_revision
FROM advanced;

DO `$revision`$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM holy_stone_fixture_context
        WHERE new_inventory_revision = old_inventory_revision + 1)
    THEN
        RAISE EXCEPTION 'Inventory revision did not advance exactly once';
    END IF;
END
`$revision`$;

INSERT INTO public.character_item_audit (
    source, action, user_id, item_location, slot_index,
    prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'localdev-holy-stone-test-kit', mutation.action,
       context.character_id, mutation.item_location, mutation.slot_index,
       mutation.prop_id, mutation.item_quality, mutation.item_grade,
       mutation.item_exp, mutation.old_item
FROM holy_stone_fixture_mutations mutation
CROSS JOIN holy_stone_fixture_context context
ORDER BY mutation.ordinal;

COMMIT;
SELECT 'HOLY_STONE_TEST_KIT_RESULT|' || jsonb_build_object(
    'accountId', context.account_id,
    'characterId', context.character_id,
    'characterName', context.character_name,
    'publishedRevision', context.publication_revision,
    'inventoryRevisionBefore', context.old_inventory_revision,
    'inventoryRevisionAfter', context.new_inventory_revision,
    'clearedBagRows', (SELECT count(*) FROM holy_stone_fixture_mutations
                       WHERE action = 'clear_bag'),
    'openedGearRows', (SELECT count(*) FROM holy_stone_fixture_mutations
                       WHERE action = 'open_four_sockets'),
    'grantedBagRows', (SELECT count(*) FROM holy_stone_fixture_mutations
                       WHERE action = 'grant'))::text
FROM holy_stone_fixture_context context;
"@

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
$exitCode = $LASTEXITCODE
$lines = @($output | ForEach-Object { $_.ToString() })
if ($exitCode -ne 0) {
    throw "Holy Stone test fixture failed and rolled back:`n$($lines -join "`n")"
}
$receipt = $lines | Where-Object {
    $_.StartsWith('HOLY_STONE_TEST_KIT_RESULT|')
} | Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($receipt)) {
    throw 'The Holy Stone fixture returned no receipt.'
}
$receipt.Substring('HOLY_STONE_TEST_KIT_RESULT|'.Length) | ConvertFrom-Json
