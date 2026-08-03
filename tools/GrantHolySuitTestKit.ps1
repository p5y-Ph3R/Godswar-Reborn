[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "Medium")]
param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$AccountId = 13,

    [ValidateNotNullOrEmpty()]
    [string]$CharacterName = "test2",

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$PostgresContainer = "godswar-postgres",

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$ServerContainer = "godswar-server",

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = "godswar",

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$DatabaseUser = "godswar"
)

# LocalDevelopment fixture only. This is intentionally not a gameplay or
# production administration path. It may run only while the game server is
# stopped and the character has no PostgreSQL checkpoint owner.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($CharacterName.Length -gt 32 -or $CharacterName.Contains([char]0)) {
    throw "CharacterName must contain 1 to 32 non-NUL characters."
}

if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker is required but was not found on PATH."
}

function Get-ContainerState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [switch]$AllowMissing
    )

    $output = & docker container inspect `
        --format '{{json .State}}' $Name 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | ForEach-Object { $_.ToString() }) -join "`n"

    if ($exitCode -ne 0) {
        if ($AllowMissing) {
            return $null
        }

        throw "Could not inspect Docker container '$Name': $text"
    }

    try {
        return $text.Trim() | ConvertFrom-Json
    } catch {
        throw "Docker returned invalid state for container '$Name'."
    }
}

$serverState = Get-ContainerState `
    -Name $ServerContainer `
    -AllowMissing
if ($null -ne $serverState -and $serverState.Running) {
    throw (
        "Refusing the offline Holy Suit fixture while server container " +
        "'$ServerContainer' is running. Stop it cleanly first."
    )
}

$postgresState = Get-ContainerState -Name $PostgresContainer
if (!$postgresState.Running) {
    throw "PostgreSQL container '$PostgresContainer' is not running."
}
$healthProperty = $postgresState.PSObject.Properties["Health"]
if ($null -ne $healthProperty -and
    $null -ne $healthProperty.Value -and
    $healthProperty.Value.Status -ne "healthy") {
    throw (
        "PostgreSQL container '$PostgresContainer' is not healthy " +
        "(state: $($healthProperty.Value.Status))."
    )
}

$target = "account $AccountId / character '$CharacterName'"
if (!$PSCmdlet.ShouldProcess($target, "Reset offline Holy Suit test kit")) {
    return
}

$safeCharacterName = $CharacterName.Replace("'", "''")
$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE desired_holy_suit_test_kit (
    ordinal smallint PRIMARY KEY,
    prop_id integer UNIQUE NOT NULL,
    desired_role varchar(32) NOT NULL,
    desired_suit_type smallint,
    desired_stack smallint NOT NULL,
    desired_exp integer NOT NULL,
    desired_bound smallint NOT NULL,
    label text NOT NULL
);

INSERT INTO desired_holy_suit_test_kit
    (ordinal, prop_id, desired_role, desired_suit_type,
     desired_stack, desired_exp, desired_bound, label)
VALUES
    (1, 9020, 'holy_box', NULL, 1,    100000, 1, 'Holy Box I'),
    (2, 9021, 'holy_box', NULL, 1,   1000000, 1, 'Holy Box II'),
    (3, 9022, 'holy_box', NULL, 1,  10000000, 1, 'Holy Box III'),
    (4, 9023, 'holy_box', NULL, 1, 100000000, 1, 'Holy Box IV'),
    (5, 9024, 'holy_box', NULL, 1, 400000000, 1, 'Holy Box V'),
    (6, 9010, 'ware', 1, 99, 0, 0, 'Bronze Ware'),
    (7, 9011, 'ware', 2, 99, 0, 0, 'Silver Ware'),
    (8, 9012, 'ware', 3, 99, 0, 0, 'Gold Ware'),
    (9, 9013, 'ware', 4, 99, 0, 0, 'Platinum Ware'),
    (10, 9014, 'ware', 5, 99, 0, 0, 'Mithril Ware'),
    (11, 9015, 'ware', 6, 99, 0, 0, 'Orichalcum Ware'),
    (12, 9016, 'ware', 7, 99, 0, 0, 'Adamantium Ware');

CREATE TEMP TABLE holy_suit_test_kit_context (
    account_id integer NOT NULL,
    character_id integer NOT NULL,
    character_name text NOT NULL,
    old_inventory_revision bigint NOT NULL,
    new_inventory_revision bigint,
    publication_revision text NOT NULL
);

CREATE TEMP TABLE holy_suit_test_kit_mutations (
    mutation_ordinal bigint GENERATED ALWAYS AS IDENTITY,
    action text NOT NULL,
    slot_index smallint NOT NULL,
    prop_id integer NOT NULL,
    item_quality smallint NOT NULL,
    item_grade smallint NOT NULL,
    item_exp integer NOT NULL,
    old_item jsonb
);

DO `$grant`$
DECLARE
    v_character_id integer;
    v_inventory_revision bigint;
    v_checkpoint_owner uuid;
    v_publication_revision text;
    v_missing text;
BEGIN
    SELECT character_row.id,
           character_row.inventory_revision,
           character_row.checkpoint_owner_id
    INTO v_character_id, v_inventory_revision, v_checkpoint_owner
    FROM public.character_base character_row
    WHERE character_row.account_id = $AccountId
      AND character_row.name = '$safeCharacterName'
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION
            'Character % on account % does not exist',
            '$safeCharacterName', $AccountId;
    END IF;
    IF v_checkpoint_owner IS NOT NULL THEN
        RAISE EXCEPTION
            'Character % has active checkpoint owner %; cleanly stop/release it first',
            v_character_id, v_checkpoint_owner;
    END IF;

    PERFORM 1
    FROM public.character_items item
    WHERE item.user_id = v_character_id
      AND item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95
    ORDER BY item.slot_index
    FOR UPDATE;

    SELECT string_agg(desired.prop_id::text, ', ' ORDER BY desired.prop_id)
    INTO v_missing
    FROM desired_holy_suit_test_kit desired
    LEFT JOIN public.item_templates current_template
      ON current_template.id = desired.prop_id
    WHERE current_template.id IS NULL;
    IF v_missing IS NOT NULL THEN
        RAISE EXCEPTION
            'Current item_templates is missing Holy Suit IDs: %', v_missing;
    END IF;

    SELECT publication.revision
    INTO v_publication_revision
    FROM public.item_template_content_publication publication
    JOIN public.item_template_content_revisions revision
      ON revision.revision = publication.revision
    WHERE publication.family = 'items'
      AND revision.sealed_at IS NOT NULL
      AND revision.manifest_version = 7;
    IF v_publication_revision IS NULL THEN
        RAISE EXCEPTION
            'No sealed v7 item manifest is currently published';
    END IF;

    SELECT string_agg(desired.prop_id::text, ', ' ORDER BY desired.prop_id)
    INTO v_missing
    FROM desired_holy_suit_test_kit desired
    LEFT JOIN public.item_template_content_definitions definition
      ON definition.revision = v_publication_revision
     AND definition.id = desired.prop_id
    WHERE definition.id IS NULL;
    IF v_missing IS NOT NULL THEN
        RAISE EXCEPTION
            'Published item revision % is missing Holy Suit IDs: %',
            v_publication_revision, v_missing;
    END IF;

    SELECT string_agg(desired.prop_id::text, ', ' ORDER BY desired.prop_id)
    INTO v_missing
    FROM desired_holy_suit_test_kit desired
    LEFT JOIN public.holy_suit_consumable_content_definitions consumable
      ON consumable.revision = v_publication_revision
     AND consumable.item_id = desired.prop_id
    WHERE consumable.item_id IS NULL
       OR consumable.role <> desired.desired_role
       OR consumable.suit_type IS DISTINCT FROM desired.desired_suit_type
       OR consumable.experience_capacity <> desired.desired_exp
       OR consumable.stack_cap <> desired.desired_stack
       OR consumable.granted_bound <> desired.desired_bound;
    IF v_missing IS NOT NULL THEN
        RAISE EXCEPTION
            'Published Holy Suit consumable policy mismatches IDs: %',
            v_missing;
    END IF;

    INSERT INTO holy_suit_test_kit_context (
        account_id, character_id, character_name,
        old_inventory_revision, publication_revision)
    VALUES (
        $AccountId, v_character_id, '$safeCharacterName',
        v_inventory_revision, v_publication_revision);
END
`$grant`$;

WITH ranked AS MATERIALIZED (
    SELECT item.id,
           row_number() OVER (
               PARTITION BY item.prop_id
               ORDER BY item.slot_index, item.id) AS row_number
    FROM public.character_items item
    JOIN holy_suit_test_kit_context context
      ON context.character_id = item.user_id
    JOIN desired_holy_suit_test_kit desired
      ON desired.prop_id = item.prop_id
    WHERE item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95
),
deleted AS (
    DELETE FROM public.character_items item
    USING ranked
    WHERE item.id = ranked.id
      AND ranked.row_number > 1
    RETURNING item.*
)
INSERT INTO holy_suit_test_kit_mutations (
    action, slot_index, prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'delete_duplicate', slot_index, prop_id,
       item_quality, item_grade, item_exp, to_jsonb(deleted)
FROM deleted;

DO `$capacity`$
DECLARE
    v_character_id integer;
    v_missing_count integer;
    v_empty_count integer;
BEGIN
    SELECT character_id INTO STRICT v_character_id
    FROM holy_suit_test_kit_context;

    SELECT count(*)
    INTO v_missing_count
    FROM desired_holy_suit_test_kit desired
    WHERE NOT EXISTS (
        SELECT 1
        FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.prop_id = desired.prop_id
          AND item.slot_index BETWEEN 0 AND 95
    );

    SELECT count(*)
    INTO v_empty_count
    FROM generate_series(0, 95) slot(slot_index)
    WHERE NOT EXISTS (
        SELECT 1
        FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.slot_index = slot.slot_index
    );

    IF v_missing_count > v_empty_count THEN
        RAISE EXCEPTION
            'Test kit needs % empty bag slots but only % are available',
            v_missing_count, v_empty_count;
    END IF;
END
`$capacity`$;

WITH before_state AS MATERIALIZED (
    SELECT item.id, to_jsonb(item) AS old_item
    FROM public.character_items item
    JOIN holy_suit_test_kit_context context
      ON context.character_id = item.user_id
    JOIN desired_holy_suit_test_kit desired
      ON desired.prop_id = item.prop_id
    WHERE item.item_location = 1
      AND item.slot_index BETWEEN 0 AND 95
),
updated AS (
    UPDATE public.character_items item
    SET attribute1 = NULL,
        attribute2 = NULL,
        attribute3 = NULL,
        attribute4 = NULL,
        attribute5 = NULL,
        attribute_level1 = NULL,
        attribute_level2 = NULL,
        attribute_level3 = NULL,
        attribute_level4 = NULL,
        attribute_level5 = NULL,
        item_quality = 1,
        item_grade = 1,
        bound = desired.desired_bound,
        stack = desired.desired_stack,
        item_exp = desired.desired_exp,
        holy_suit_code = 0,
        holy_socket_count = 0,
        holy_socket1_effect_id = NULL,
        holy_socket1_level = NULL,
        holy_socket2_effect_id = NULL,
        holy_socket2_level = NULL,
        holy_socket3_effect_id = NULL,
        holy_socket3_level = NULL,
        holy_socket4_effect_id = NULL,
        holy_socket4_level = NULL,
        holy_socket5_effect_id = NULL,
        holy_socket5_level = NULL,
        holy_socket6_effect_id = NULL,
        holy_socket6_level = NULL,
        updated_at = now()
    FROM desired_holy_suit_test_kit desired,
         before_state before_item
    WHERE item.id = before_item.id
      AND item.prop_id = desired.prop_id
    RETURNING item.id, item.slot_index, item.prop_id,
              item.item_quality, item.item_grade, item.item_exp
)
INSERT INTO holy_suit_test_kit_mutations (
    action, slot_index, prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'reset', updated.slot_index, updated.prop_id,
       updated.item_quality, updated.item_grade, updated.item_exp,
       before_state.old_item
FROM updated
JOIN before_state ON before_state.id = updated.id;

WITH missing AS MATERIALIZED (
    SELECT desired.*,
           row_number() OVER (ORDER BY desired.ordinal) AS row_number
    FROM desired_holy_suit_test_kit desired
    CROSS JOIN holy_suit_test_kit_context context
    WHERE NOT EXISTS (
        SELECT 1
        FROM public.character_items item
        WHERE item.user_id = context.character_id
          AND item.item_location = 1
          AND item.prop_id = desired.prop_id
          AND item.slot_index BETWEEN 0 AND 95
    )
),
empty_slots AS MATERIALIZED (
    SELECT slot.slot_index::smallint AS slot_index,
           row_number() OVER (ORDER BY slot.slot_index) AS row_number
    FROM generate_series(0, 95) slot(slot_index)
    CROSS JOIN holy_suit_test_kit_context context
    WHERE NOT EXISTS (
        SELECT 1
        FROM public.character_items item
        WHERE item.user_id = context.character_id
          AND item.item_location = 1
          AND item.slot_index = slot.slot_index
    )
),
inserted AS (
    INSERT INTO public.character_items (
        user_id, item_location, slot_index, prop_id,
        item_quality, item_grade, bound, stack, item_exp, holy_suit_code)
    SELECT context.character_id, 1, empty_slots.slot_index, missing.prop_id,
           1, 1, missing.desired_bound, missing.desired_stack,
           missing.desired_exp, 0
    FROM missing
    JOIN empty_slots USING (row_number)
    CROSS JOIN holy_suit_test_kit_context context
    ORDER BY missing.ordinal
    RETURNING slot_index, prop_id, item_quality, item_grade, item_exp
)
INSERT INTO holy_suit_test_kit_mutations (
    action, slot_index, prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'add', slot_index, prop_id, item_quality, item_grade, item_exp, NULL
FROM inserted;

WITH advanced AS (
    UPDATE public.character_base character_row
    SET inventory_revision = character_row.inventory_revision + 1
    FROM holy_suit_test_kit_context context
    WHERE character_row.id = context.character_id
      AND character_row.account_id = context.account_id
      AND character_row.inventory_revision = context.old_inventory_revision
    RETURNING character_row.inventory_revision
)
UPDATE holy_suit_test_kit_context context
SET new_inventory_revision = advanced.inventory_revision
FROM advanced;

DO `$revision`$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM holy_suit_test_kit_context
        WHERE new_inventory_revision = old_inventory_revision + 1
    ) THEN
        RAISE EXCEPTION
            'Inventory revision did not advance exactly once';
    END IF;
END
`$revision`$;

INSERT INTO public.character_item_audit (
    source, action, user_id, item_location, slot_index,
    prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'localdev-holy-suit-test-kit', mutation.action,
       context.character_id, 1, mutation.slot_index,
       mutation.prop_id, mutation.item_quality, mutation.item_grade,
       mutation.item_exp, mutation.old_item
FROM holy_suit_test_kit_mutations mutation
CROSS JOIN holy_suit_test_kit_context context
ORDER BY mutation.mutation_ordinal;

COMMIT;

SELECT 'HOLY_SUIT_TEST_KIT_RESULT|' || jsonb_build_object(
    'accountId', context.account_id,
    'characterId', context.character_id,
    'characterName', context.character_name,
    'publishedRevision', context.publication_revision,
    'inventoryRevisionBefore', context.old_inventory_revision,
    'inventoryRevisionAfter', context.new_inventory_revision,
    'mutationCount', (SELECT count(*) FROM holy_suit_test_kit_mutations),
    'items', (
        SELECT jsonb_agg(jsonb_build_object(
            'slot', item.slot_index,
            'itemId', item.prop_id,
            'name', desired.label,
            'bound', item.bound,
            'stack', item.stack,
            'storedExp', item.item_exp
        ) ORDER BY desired.ordinal)
        FROM desired_holy_suit_test_kit desired
        JOIN public.character_items item
          ON item.user_id = context.character_id
         AND item.item_location = 1
         AND item.prop_id = desired.prop_id
    )
)::text
FROM holy_suit_test_kit_context context;
"@

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
$exitCode = $LASTEXITCODE
$outputLines = @($output | ForEach-Object { $_.ToString() })

if ($exitCode -ne 0) {
    throw (
        "Holy Suit test-kit transaction failed and was rolled back:`n" +
        ($outputLines -join "`n")
    )
}

$resultLine = $outputLines |
    Where-Object { $_.StartsWith("HOLY_SUIT_TEST_KIT_RESULT|") } |
    Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($resultLine)) {
    throw "The transaction committed but returned no verification receipt."
}

$json = $resultLine.Substring("HOLY_SUIT_TEST_KIT_RESULT|".Length)
$receipt = $json | ConvertFrom-Json
$receipt
