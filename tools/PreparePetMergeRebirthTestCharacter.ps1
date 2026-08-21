[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateRange(1, [int]::MaxValue)][int]$AccountId = 13,
    [ValidateNotNullOrEmpty()][string]$CharacterName = 'test2',
    [ValidateNotNullOrEmpty()][string]$PetName = 'Jolo',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$PostgresContainer = 'godswar-dev-postgres',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$ServerContainer = 'godswar-dev-tempest-openworld-01',
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = 'godswar',
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$DatabaseUser = 'godswar'
)

# Isolated local-development fixture. It does not clear the bag. It grants
# one Morning Dew 5 plus full stacks of Merged/Rebirth Spirit, then gives the
# named carried pet one remaining rebirth and a Soul Contract. Every mutation
# is revisioned and permanently audited. The target must be offline.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($value in @($CharacterName, $PetName)) {
    if ($value.Length -gt 32 -or $value.Contains([char]0)) {
        throw 'CharacterName and PetName must contain 1 to 32 non-NUL characters.'
    }
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
    throw "Refusing the offline fixture while '$ServerContainer' is running."
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

$target = "account $AccountId / '$CharacterName' / pet '$PetName'"
if (-not $PSCmdlet.ShouldProcess(
        $target,
        'Install merge/rebirth test items and one rebirth attempt')) {
    return
}
$safeCharacter = $CharacterName.Replace("'", "''")
$safePet = $PetName.Replace("'", "''")

$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE fixture_context (
    account_id integer NOT NULL,
    character_id integer NOT NULL,
    pet_id bigint NOT NULL,
    old_inventory_revision bigint NOT NULL,
    new_inventory_revision bigint,
    old_pet_revision bigint NOT NULL,
    new_pet_revision bigint,
    pet_before jsonb NOT NULL,
    pet_after jsonb,
    publication_revision text NOT NULL
);
CREATE TEMP TABLE fixture_items (
    ordinal smallint PRIMARY KEY,
    prop_id integer NOT NULL,
    quantity smallint NOT NULL,
    expected_name text NOT NULL,
    expected_overlap smallint NOT NULL
);
INSERT INTO fixture_items VALUES
    (0, 10134, 1,  'Morning Dew 5', 99),
    (1, 10103, 99, 'Merged Spirit', 99),
    (2, 10104, 99, 'Rebirth Spirit', 99);
CREATE TEMP TABLE fixture_item_mutations (
    ordinal bigint GENERATED ALWAYS AS IDENTITY,
    action text NOT NULL,
    slot_index smallint NOT NULL,
    prop_id integer NOT NULL,
    old_item jsonb
);

DO `$context`$
DECLARE
    v_character_id integer;
    v_pet_id bigint;
    v_inventory_revision bigint;
    v_pet_revision bigint;
    v_pet_before jsonb;
    v_owner uuid;
    v_login_status smallint;
    v_publication text;
    v_invalid text;
BEGIN
    SELECT c.id, c.inventory_revision, c.checkpoint_owner_id, a.login_status
    INTO v_character_id, v_inventory_revision, v_owner, v_login_status
    FROM public.character_base c
    JOIN public.accounts a ON a.id = c.account_id
    WHERE c.account_id = $AccountId AND c.name = '$safeCharacter'
    FOR UPDATE OF c, a;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Target character does not exist';
    END IF;
    IF v_owner IS NOT NULL OR v_login_status <> 0 THEN
        RAISE EXCEPTION 'Target character is not safely offline';
    END IF;

    SELECT p.id, p.revision, to_jsonb(p)
    INTO v_pet_id, v_pet_revision, v_pet_before
    FROM public.character_pets p
    WHERE p.user_id = v_character_id
      AND p.name = '$safePet'
      AND p.activity_state = 'owned'
      AND p.is_carried
      AND p.is_summoned
      AND NOT p.contributes_to_character
    FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Named pet is not the active unmerged pet';
    END IF;

    PERFORM 1 FROM public.character_items
    WHERE user_id = v_character_id AND item_location = 1
    ORDER BY slot_index FOR UPDATE;

    SELECT publication.revision INTO v_publication
    FROM public.item_template_content_publication publication
    JOIN public.item_template_content_revisions revision
      ON revision.revision = publication.revision
    WHERE publication.family = 'items' AND revision.sealed_at IS NOT NULL;
    IF v_publication IS NULL THEN
        RAISE EXCEPTION 'No sealed item publication is active';
    END IF;

    SELECT string_agg(desired.prop_id::text, ', ' ORDER BY desired.ordinal)
    INTO v_invalid
    FROM fixture_items desired
    LEFT JOIN public.item_template_content_definitions definition
      ON definition.revision = v_publication
     AND definition.id = desired.prop_id
    LEFT JOIN public.item_templates mutable ON mutable.id = desired.prop_id
    WHERE definition.id IS NULL OR mutable.id IS NULL
       OR definition.display_name <> desired.expected_name
       OR definition.kind <> 'consume item'
       OR COALESCE((definition.stats ->> 'Overlap')::smallint, 0)
            <> desired.expected_overlap;
    IF v_invalid IS NOT NULL THEN
        RAISE EXCEPTION 'Invalid or unpublished fixture items: %', v_invalid;
    END IF;

    INSERT INTO fixture_context VALUES (
        $AccountId, v_character_id, v_pet_id,
        v_inventory_revision, NULL, v_pet_revision, NULL,
        v_pet_before, NULL, v_publication);
END
`$context`$;

DO `$grants`$
DECLARE
    desired record;
    v_remaining integer;
    v_slot smallint;
    v_item_id bigint;
    v_stack smallint;
    v_before jsonb;
BEGIN
    FOR desired IN SELECT * FROM fixture_items ORDER BY ordinal LOOP
        v_remaining := desired.quantity;
        SELECT item.id, item.slot_index, item.stack, to_jsonb(item)
        INTO v_item_id, v_slot, v_stack, v_before
        FROM public.character_items item
        JOIN fixture_context context ON context.character_id = item.user_id
        WHERE item.item_location = 1
          AND item.prop_id = desired.prop_id
          AND item.bound = 0
          AND item.stack < desired.expected_overlap
        ORDER BY item.slot_index LIMIT 1 FOR UPDATE OF item;

        IF FOUND THEN
            INSERT INTO fixture_item_mutations(action,slot_index,prop_id,old_item)
            VALUES ('update',v_slot,desired.prop_id,v_before);
            v_stack := LEAST(
                desired.expected_overlap,
                v_stack + v_remaining);
            UPDATE public.character_items
            SET stack = v_stack, updated_at = transaction_timestamp()
            WHERE id = v_item_id;
            v_remaining := v_remaining -
                (v_stack - (v_before ->> 'stack')::smallint);
        END IF;

        WHILE v_remaining > 0 LOOP
            SELECT candidate.slot_index INTO v_slot
            FROM generate_series(0, 95) AS candidate(slot_index)
            WHERE NOT EXISTS (
                SELECT 1 FROM public.character_items item
                JOIN fixture_context context
                  ON context.character_id = item.user_id
                WHERE item.item_location = 1
                  AND item.slot_index = candidate.slot_index)
            ORDER BY candidate.slot_index LIMIT 1;
            IF v_slot IS NULL THEN
                RAISE EXCEPTION 'Insufficient kit-bag capacity';
            END IF;
            v_stack := LEAST(desired.expected_overlap, v_remaining);
            INSERT INTO public.character_items (
                user_id,item_location,slot_index,prop_id,item_quality,
                item_grade,bound,stack,item_exp,holy_suit_code)
            SELECT character_id,1,v_slot,desired.prop_id,1,1,0,v_stack,0,0
            FROM fixture_context;
            INSERT INTO fixture_item_mutations(action,slot_index,prop_id,old_item)
            VALUES ('add',v_slot,desired.prop_id,NULL);
            v_remaining := v_remaining - v_stack;
        END LOOP;
    END LOOP;
END
`$grants`$;

WITH advanced AS (
    UPDATE public.character_base c
    SET inventory_revision = c.inventory_revision + 1
    FROM fixture_context context
    WHERE c.id = context.character_id
      AND c.inventory_revision = context.old_inventory_revision
    RETURNING c.inventory_revision
)
UPDATE fixture_context SET new_inventory_revision = advanced.inventory_revision
FROM advanced;

WITH changed AS (
    UPDATE public.character_pets p
    SET rebirths_remaining = 1,
        has_soul_contract = true,
        revision = p.revision + 1,
        updated_at = transaction_timestamp()
    FROM fixture_context context
    WHERE p.id = context.pet_id
      AND p.user_id = context.character_id
      AND p.revision = context.old_pet_revision
      AND NOT p.contributes_to_character
    RETURNING p.revision, to_jsonb(p) AS after_state
)
UPDATE fixture_context context
SET new_pet_revision = changed.revision,
    pet_after = changed.after_state
FROM changed;

DO `$verify`$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM fixture_context
        WHERE new_inventory_revision = old_inventory_revision + 1
          AND new_pet_revision = old_pet_revision + 1
          AND (pet_after ->> 'rebirths_remaining')::integer = 1
          AND (pet_after ->> 'has_soul_contract')::boolean)
    THEN
        RAISE EXCEPTION 'Fixture revisions or pet state did not advance exactly';
    END IF;
END
`$verify`$;

INSERT INTO public.character_item_audit (
    source,action,user_id,item_location,slot_index,
    prop_id,item_quality,item_grade,item_exp,old_item)
SELECT 'localdev-pet-merge-rebirth-kit', mutation.action,
       context.character_id,1,mutation.slot_index,mutation.prop_id,1,1,0,
       mutation.old_item
FROM fixture_item_mutations mutation CROSS JOIN fixture_context context
ORDER BY mutation.ordinal;

INSERT INTO public.pet_operation_audit (
    request_id,user_id,user_id_snapshot,pet_id,pet_id_snapshot,
    operation,outcome,before_state,after_state,consumed_items,reason_code)
SELECT gen_random_uuid(),character_id,character_id,pet_id,pet_id,
       'rebirth','committed',pet_before,pet_after,'[]'::jsonb,
       'pet_merge_rebirth_test_setup'
FROM fixture_context;

COMMIT;
SELECT 'PET_MERGE_REBIRTH_FIXTURE|' || jsonb_build_object(
    'accountId',account_id,'characterId',character_id,'petId',pet_id,
    'inventoryRevision',new_inventory_revision,
    'petRevision',new_pet_revision,
    'rebirthsRemaining',(pet_after ->> 'rebirths_remaining')::integer,
    'hasSoulContract',(pet_after ->> 'has_soul_contract')::boolean,
    'items',(SELECT jsonb_agg(jsonb_build_object(
        'slot',item.slot_index,'itemId',item.prop_id,'stack',item.stack)
        ORDER BY item.slot_index)
      FROM public.character_items item
      WHERE item.user_id=fixture_context.character_id
        AND item.item_location=1
        AND item.prop_id IN (10103,10104,10134)))::text
FROM fixture_context;
"@

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
$lines = @($output | ForEach-Object { $_.ToString() })
if ($LASTEXITCODE -ne 0) {
    throw "Pet merge/rebirth fixture rolled back:`n$($lines -join "`n")"
}
$receipt = $lines | Where-Object {
    $_.StartsWith('PET_MERGE_REBIRTH_FIXTURE|')
} | Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($receipt)) {
    throw 'Pet merge/rebirth fixture returned no receipt.'
}
$receipt.Substring('PET_MERGE_REBIRTH_FIXTURE|'.Length) | ConvertFrom-Json
