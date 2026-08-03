[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateRange(1, [int]::MaxValue)][int]$AccountId = 13,
    [ValidateNotNullOrEmpty()][string]$CharacterName = 'test2',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$PostgresContainer = 'godswar-postgres',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$ServerContainer = 'godswar-server',
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = 'godswar',
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$DatabaseUser = 'godswar'
)

# Local-development fixture only. It reconciles one offline character to one
# 99-stack of each canonical elemental stone, records every mutation in
# character_item_audit, and advances inventory_revision once only when the
# inventory actually changes.
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
    throw "Refusing the offline elemental fixture while '$ServerContainer' is running."
}
$postgresState = Get-ContainerState $PostgresContainer
if (-not $postgresState.Running) { throw "PostgreSQL '$PostgresContainer' is not running." }
$health = $postgresState.PSObject.Properties['Health']
if ($null -ne $health -and $null -ne $health.Value -and
    $health.Value.Status -ne 'healthy') {
    throw "PostgreSQL '$PostgresContainer' is not healthy."
}

$target = "account $AccountId / character '$CharacterName'"
if (-not $PSCmdlet.ShouldProcess(
        $target,
        'Reconcile seven-stone elemental test kit')) { return }
$safeName = $CharacterName.Replace("'", "''")

$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE desired_elemental_test_kit (
    ordinal smallint PRIMARY KEY,
    prop_id integer UNIQUE NOT NULL,
    first_attribute_id integer NOT NULL
);
INSERT INTO desired_elemental_test_kit
    (ordinal, prop_id, first_attribute_id)
VALUES
    (0, 16300, 480),
    (1, 16303, 483),
    (2, 16306, 486),
    (3, 16309, 489),
    (4, 16312, 492),
    (5, 16315, 495),
    (6, 16318, 498);

CREATE TEMP TABLE retired_elemental_test_kit (
    prop_id integer PRIMARY KEY
);
INSERT INTO retired_elemental_test_kit (prop_id)
VALUES
    (16301), (16302),
    (16304), (16305),
    (16307), (16308),
    (16310), (16311),
    (16313), (16314),
    (16316), (16317),
    (16319), (16320);

CREATE TEMP TABLE elemental_test_kit_context (
    account_id integer NOT NULL,
    character_id integer NOT NULL,
    character_name text NOT NULL,
    old_inventory_revision bigint NOT NULL,
    new_inventory_revision bigint,
    publication_revision text NOT NULL
);
CREATE TEMP TABLE elemental_test_kit_mutations (
    ordinal bigint GENERATED ALWAYS AS IDENTITY,
    action text NOT NULL,
    slot_index smallint NOT NULL,
    prop_id integer NOT NULL,
    old_item jsonb
);

DO `$context`$
DECLARE
    v_character_id integer;
    v_inventory_revision bigint;
    v_checkpoint_owner uuid;
    v_revision text;
    v_missing text;
BEGIN
    SELECT id, inventory_revision, checkpoint_owner_id
    INTO v_character_id, v_inventory_revision, v_checkpoint_owner
    FROM public.character_base
    WHERE account_id = $AccountId AND name = '$safeName'
    FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Character % on account % does not exist', '$safeName', $AccountId;
    END IF;
    IF v_checkpoint_owner IS NOT NULL THEN
        RAISE EXCEPTION 'Character % has active checkpoint owner %', v_character_id, v_checkpoint_owner;
    END IF;

    PERFORM 1 FROM public.character_items
    WHERE user_id = v_character_id AND item_location = 1
      AND slot_index BETWEEN 0 AND 95
    ORDER BY slot_index FOR UPDATE;

    SELECT publication.revision INTO v_revision
    FROM public.item_template_content_publication publication
    JOIN public.item_template_content_revisions revision
      ON revision.revision = publication.revision
    WHERE publication.family = 'items'
      AND revision.sealed_at IS NOT NULL;
    IF v_revision IS NULL THEN
        RAISE EXCEPTION 'No sealed item publication is active';
    END IF;

    SELECT string_agg(desired.prop_id::text, ', ' ORDER BY desired.prop_id)
    INTO v_missing
    FROM desired_elemental_test_kit desired
    LEFT JOIN public.item_template_content_definitions definition
      ON definition.revision = v_revision AND definition.id = desired.prop_id
    LEFT JOIN public.item_material_content_definitions material
      ON material.revision = v_revision AND material.item_id = desired.prop_id
    WHERE definition.id IS NULL OR material.item_id IS NULL
       OR material.stack_cap <> 99 OR material.granted_bound <> 0
       OR material.policy_kind <> 'attribute_stone'
       OR material.attribute_ids <> ARRAY[
            desired.first_attribute_id,
            desired.first_attribute_id + 1,
            desired.first_attribute_id + 2];
    IF v_missing IS NOT NULL THEN
        RAISE EXCEPTION
            'Published canonical elemental-stone policy mismatch: %',
            v_missing;
    END IF;

    SELECT string_agg(material.item_id::text, ', ' ORDER BY material.item_id)
    INTO v_missing
    FROM public.item_material_content_definitions material
    WHERE material.revision = v_revision
      AND material.item_id BETWEEN 16300 AND 16320
      AND NOT EXISTS (
          SELECT 1
          FROM desired_elemental_test_kit desired
          WHERE desired.prop_id = material.item_id);
    IF v_missing IS NOT NULL THEN
        RAISE EXCEPTION
            'Published retired elemental-stone policies remain active: %',
            v_missing;
    END IF;

    INSERT INTO elemental_test_kit_context
        (account_id, character_id, character_name,
         old_inventory_revision, publication_revision)
    VALUES ($AccountId, v_character_id, '$safeName',
            v_inventory_revision, v_revision);
END
`$context`$;

WITH ranked AS MATERIALIZED (
    SELECT item.id,
           row_number() OVER (PARTITION BY item.prop_id ORDER BY item.slot_index, item.id) AS row_number
    FROM public.character_items item
    JOIN elemental_test_kit_context context ON context.character_id = item.user_id
    JOIN desired_elemental_test_kit desired ON desired.prop_id = item.prop_id
    WHERE item.item_location = 1 AND item.slot_index BETWEEN 0 AND 95
), deleted AS (
    DELETE FROM public.character_items item USING ranked
    WHERE item.id = ranked.id AND ranked.row_number > 1
    RETURNING item.*
)
INSERT INTO elemental_test_kit_mutations (action, slot_index, prop_id, old_item)
SELECT 'delete_duplicate', slot_index, prop_id, to_jsonb(deleted) FROM deleted;

WITH deleted AS (
    DELETE FROM public.character_items item
    USING elemental_test_kit_context context,
          retired_elemental_test_kit retired
    WHERE item.user_id = context.character_id
      AND item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95
      AND item.prop_id = retired.prop_id
    RETURNING item.*
)
INSERT INTO elemental_test_kit_mutations
    (action, slot_index, prop_id, old_item)
SELECT 'retire', slot_index, prop_id, to_jsonb(deleted)
FROM deleted;

DO `$capacity`$
DECLARE v_character_id integer; v_missing integer; v_empty integer;
BEGIN
    SELECT character_id INTO STRICT v_character_id FROM elemental_test_kit_context;
    SELECT count(*) INTO v_missing FROM desired_elemental_test_kit desired
    WHERE NOT EXISTS (
        SELECT 1 FROM public.character_items item
        WHERE item.user_id = v_character_id AND item.item_location = 1
          AND item.prop_id = desired.prop_id AND item.slot_index BETWEEN 0 AND 95);
    SELECT count(*) INTO v_empty FROM generate_series(0,95) slot(slot_index)
    WHERE NOT EXISTS (
        SELECT 1 FROM public.character_items item
        WHERE item.user_id = v_character_id AND item.item_location = 1
          AND item.slot_index = slot.slot_index);
    IF v_missing > v_empty THEN
        RAISE EXCEPTION 'Elemental kit needs % empty slots but only % exist', v_missing, v_empty;
    END IF;
END
`$capacity`$;

WITH before_state AS MATERIALIZED (
    SELECT item.id, to_jsonb(item) old_item
    FROM public.character_items item
    JOIN elemental_test_kit_context context ON context.character_id = item.user_id
    JOIN desired_elemental_test_kit desired ON desired.prop_id = item.prop_id
    WHERE item.item_location = 1 AND item.slot_index BETWEEN 0 AND 95
      AND (
          item.attribute1 IS NOT NULL OR item.attribute2 IS NOT NULL OR
          item.attribute3 IS NOT NULL OR item.attribute4 IS NOT NULL OR
          item.attribute5 IS NOT NULL OR
          item.attribute_level1 IS NOT NULL OR
          item.attribute_level2 IS NOT NULL OR
          item.attribute_level3 IS NOT NULL OR
          item.attribute_level4 IS NOT NULL OR
          item.attribute_level5 IS NOT NULL OR
          item.class_attribute1 IS NOT NULL OR
          item.class_attribute2 IS NOT NULL OR
          item.elemental_attribute1 IS NOT NULL OR
          item.elemental_attribute2 IS NOT NULL OR
          item.item_quality <> 1 OR item.item_grade <> 1 OR
          item.bound <> 0 OR item.stack <> 99 OR item.item_exp <> 0 OR
          item.holy_suit_code <> 0 OR item.holy_socket_count <> 0 OR
          item.holy_socket1_effect_id IS NOT NULL OR
          item.holy_socket1_level IS NOT NULL OR
          item.holy_socket2_effect_id IS NOT NULL OR
          item.holy_socket2_level IS NOT NULL OR
          item.holy_socket3_effect_id IS NOT NULL OR
          item.holy_socket3_level IS NOT NULL OR
          item.holy_socket4_effect_id IS NOT NULL OR
          item.holy_socket4_level IS NOT NULL OR
          item.holy_socket5_effect_id IS NOT NULL OR
          item.holy_socket5_level IS NOT NULL OR
          item.holy_socket6_effect_id IS NOT NULL OR
          item.holy_socket6_level IS NOT NULL)
), updated AS (
    UPDATE public.character_items item
    SET attribute1=NULL, attribute2=NULL, attribute3=NULL,
        attribute4=NULL, attribute5=NULL,
        attribute_level1=NULL, attribute_level2=NULL, attribute_level3=NULL,
        attribute_level4=NULL, attribute_level5=NULL,
        class_attribute1=NULL, class_attribute2=NULL,
        elemental_attribute1=NULL, elemental_attribute2=NULL,
        item_quality=1, item_grade=1, bound=0, stack=99, item_exp=0,
        holy_suit_code=0, holy_socket_count=0,
        holy_socket1_effect_id=NULL, holy_socket1_level=NULL,
        holy_socket2_effect_id=NULL, holy_socket2_level=NULL,
        holy_socket3_effect_id=NULL, holy_socket3_level=NULL,
        holy_socket4_effect_id=NULL, holy_socket4_level=NULL,
        holy_socket5_effect_id=NULL, holy_socket5_level=NULL,
        holy_socket6_effect_id=NULL, holy_socket6_level=NULL,
        updated_at=now()
    FROM before_state
    WHERE item.id = before_state.id
    RETURNING item.id, item.slot_index, item.prop_id
)
INSERT INTO elemental_test_kit_mutations (action, slot_index, prop_id, old_item)
SELECT 'reset', updated.slot_index, updated.prop_id, before_state.old_item
FROM updated JOIN before_state ON before_state.id = updated.id;

WITH missing AS MATERIALIZED (
    SELECT desired.*, row_number() OVER (ORDER BY desired.ordinal) row_number
    FROM desired_elemental_test_kit desired
    CROSS JOIN elemental_test_kit_context context
    WHERE NOT EXISTS (
        SELECT 1 FROM public.character_items item
        WHERE item.user_id = context.character_id AND item.item_location = 1
          AND item.prop_id = desired.prop_id AND item.slot_index BETWEEN 0 AND 95)
), empty_slots AS MATERIALIZED (
    SELECT slot_index::smallint,
           row_number() OVER (ORDER BY slot_index) row_number
    FROM generate_series(0,95) slot(slot_index)
    CROSS JOIN elemental_test_kit_context context
    WHERE NOT EXISTS (
        SELECT 1 FROM public.character_items item
        WHERE item.user_id = context.character_id AND item.item_location = 1
          AND item.slot_index = slot.slot_index)
), inserted AS (
    INSERT INTO public.character_items (
        user_id, item_location, slot_index, prop_id,
        item_quality, item_grade, bound, stack, item_exp, holy_suit_code)
    SELECT context.character_id, 1, empty_slots.slot_index, missing.prop_id,
           1, 1, 0, 99, 0, 0
    FROM missing JOIN empty_slots USING (row_number)
    CROSS JOIN elemental_test_kit_context context
    ORDER BY missing.ordinal
    RETURNING slot_index, prop_id
)
INSERT INTO elemental_test_kit_mutations (action, slot_index, prop_id, old_item)
SELECT 'add', slot_index, prop_id, NULL FROM inserted;

UPDATE elemental_test_kit_context
SET new_inventory_revision = old_inventory_revision;

WITH mutation_state AS (
    SELECT count(*) AS mutation_count
    FROM elemental_test_kit_mutations
), advanced AS (
    UPDATE public.character_base character_row
    SET inventory_revision = character_row.inventory_revision + 1
    FROM elemental_test_kit_context context,
         mutation_state
    WHERE character_row.id = context.character_id
      AND character_row.inventory_revision = context.old_inventory_revision
      AND mutation_state.mutation_count > 0
    RETURNING character_row.inventory_revision
)
UPDATE elemental_test_kit_context context
SET new_inventory_revision = advanced.inventory_revision FROM advanced;

DO `$revision`$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM elemental_test_kit_context
        WHERE new_inventory_revision = old_inventory_revision +
            CASE WHEN EXISTS (
                SELECT 1 FROM elemental_test_kit_mutations)
            THEN 1 ELSE 0 END)
    THEN RAISE EXCEPTION
        'Inventory revision did not follow the mutation count';
    END IF;
END
`$revision`$;

INSERT INTO public.character_item_audit (
    source, action, user_id, item_location, slot_index,
    prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'localdev-elemental-stone-test-kit', mutation.action,
       context.character_id, 1, mutation.slot_index,
       mutation.prop_id, 1, 1, 0, mutation.old_item
FROM elemental_test_kit_mutations mutation
CROSS JOIN elemental_test_kit_context context
ORDER BY mutation.ordinal;

COMMIT;
SELECT 'ELEMENTAL_TEST_KIT_RESULT|' || jsonb_build_object(
    'accountId', context.account_id,
    'characterId', context.character_id,
    'characterName', context.character_name,
    'publishedRevision', context.publication_revision,
    'inventoryRevisionBefore', context.old_inventory_revision,
    'inventoryRevisionAfter', context.new_inventory_revision,
    'mutationCount', (SELECT count(*) FROM elemental_test_kit_mutations),
    'items', (
        SELECT jsonb_agg(jsonb_build_object(
            'slot', item.slot_index, 'itemId', item.prop_id,
            'name', definition.display_name, 'stack', item.stack)
            ORDER BY desired.ordinal)
        FROM desired_elemental_test_kit desired
        JOIN public.character_items item
          ON item.user_id = context.character_id
         AND item.item_location = 1
         AND item.slot_index BETWEEN 0 AND 95
         AND item.prop_id = desired.prop_id
        JOIN public.item_template_content_definitions definition
          ON definition.revision = context.publication_revision
         AND definition.id = desired.prop_id))::text
FROM elemental_test_kit_context context;
"@

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
$exitCode = $LASTEXITCODE
$lines = @($output | ForEach-Object { $_.ToString() })
if ($exitCode -ne 0) {
    throw "Elemental test-kit transaction failed and rolled back:`n$($lines -join "`n")"
}
$result = $lines | Where-Object {
    $_.StartsWith('ELEMENTAL_TEST_KIT_RESULT|')
} | Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($result)) {
    throw 'The elemental test-kit transaction returned no receipt.'
}
$result.Substring('ELEMENTAL_TEST_KIT_RESULT|'.Length) | ConvertFrom-Json
