[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$AccountId = 13,

    [ValidatePattern('^[A-Za-z0-9_]{1,32}$')]
    [string]$CharacterName = 'test2',

    [ValidateRange(1, 99)]
    [int]$TargetQuantityEach = 1,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$PostgresContainer = 'godswar-dev-postgres',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$ServerContainer = 'godswar-dev-server',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = 'godswar',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$DatabaseUser = 'godswar'
)

# Local-development fixture only. It reaches the requested exact quantity of
# every Magic Jade whose published deputy-species Merge cap is exactly 7.80. It
# never publishes templates, moves items, or changes unrelated bag contents.
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
        "Ensure exactly $TargetQuantityEach of each published 7.80-cap Magic Jade")) {
    return
}

$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE desired_cap780_magic_jades (
    ordinal smallint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    prop_id integer UNIQUE NOT NULL,
    species_id smallint UNIQUE NOT NULL,
    appearance_name text NOT NULL,
    quantity_before integer,
    quantity_after integer
);
CREATE TEMP TABLE cap780_magic_jade_grant_context (
    account_id integer NOT NULL,
    character_id integer NOT NULL,
    character_name text NOT NULL,
    item_publication_revision text NOT NULL,
    pet_publication_revision text NOT NULL,
    old_inventory_revision bigint NOT NULL,
    new_inventory_revision bigint,
    empty_slots_before integer NOT NULL,
    required_new_slots integer,
    changed boolean NOT NULL DEFAULT false
);
CREATE TEMP TABLE cap780_magic_jade_grant_mutations (
    ordinal bigint GENERATED ALWAYS AS IDENTITY,
    action text NOT NULL,
    prop_id integer NOT NULL,
    appearance_name text NOT NULL,
    slot_index smallint NOT NULL,
    old_item jsonb,
    old_stack smallint NOT NULL,
    new_stack smallint NOT NULL
);

INSERT INTO desired_cap780_magic_jades (
    prop_id, species_id, appearance_name)
SELECT magic_jade_item_id, species_id, appearance_name
FROM public.current_pet_magic_jade_appearance_groups
WHERE merge_cap = 7.80
ORDER BY magic_jade_item_id;

DO `$preflight`$
DECLARE
    v_item_revision text;
    v_pet_revision text;
BEGIN
    IF (SELECT count(*) FROM desired_cap780_magic_jades) <> 39 OR
       (SELECT array_agg(prop_id ORDER BY prop_id)
        FROM desired_cap780_magic_jades) <>
       ARRAY[
           11053,11054,11057,11058,11060,11061,11062,11063,11064,
           11065,11066,11067,11068,11069,11070,11071,11072,11073,
           11074,11075,11076,11077,11078,11079,11080,11081,11082,
           11083,11084,11085,11086,11087,11088,11089,11090,11091,
           11092,11093,11094
       ]::integer[] OR
       EXISTS (
           SELECT 1 FROM desired_cap780_magic_jades
           WHERE prop_id NOT BETWEEN 11050 AND 11094
              OR species_id <> prop_id - 11049
              OR btrim(appearance_name) = '') OR
       (SELECT count(*) FROM public.current_pet_magic_jade_appearance_groups)
           <> 45 OR
       (SELECT count(*) FROM public.current_pet_magic_jade_appearance_groups
        WHERE merge_cap = 2.40) <> 4 OR
       (SELECT count(*) FROM public.current_pet_magic_jade_appearance_groups
        WHERE merge_cap = 4.20) <> 2
    THEN
        RAISE EXCEPTION
            'Published Magic Jade appearance groups are not the reviewed 4/2/39 policy';
    END IF;

    SELECT publication.revision
    INTO v_pet_revision
    FROM public.pet_content_publication publication
    WHERE publication.family = 'pets';
    IF v_pet_revision IS NULL OR EXISTS (
        SELECT 1 FROM desired_cap780_magic_jades desired
        JOIN public.current_pet_magic_jade_appearance_groups jade
          ON jade.magic_jade_item_id = desired.prop_id
        WHERE jade.revision <> v_pet_revision)
    THEN
        RAISE EXCEPTION 'No single current pet publication owns the jade group';
    END IF;

    SELECT publication.revision
    INTO v_item_revision
    FROM public.item_template_content_publication publication
    JOIN public.item_template_content_revisions release
      ON release.revision = publication.revision
     AND release.sealed_at IS NOT NULL
    WHERE publication.family = 'items';
    IF v_item_revision IS NULL THEN
        RAISE EXCEPTION 'No sealed item publication is active';
    END IF;

    IF (SELECT count(*)
        FROM desired_cap780_magic_jades desired
        JOIN public.item_template_content_definitions definition
          ON definition.revision = v_item_revision
         AND definition.id = desired.prop_id
        JOIN public.item_templates mutable
          ON mutable.id = definition.id
        WHERE definition.kind = 'consume item'
          AND definition.name_key = 'Pet' || desired.prop_id::text
          AND btrim(definition.display_name) <> ''
          AND definition.equipment_slot = 0
          AND cardinality(definition.class_ids) = 0
          AND definition.min_level IS NULL
          AND definition.max_level IS NULL
          AND definition.hand IS NULL
          AND definition.skill_flag IS NULL
          AND definition.texture =
              './Localization/en_us/UI/Texture/Icon2.gwo'
          AND definition.icon = '396,756'
          AND definition.stats = jsonb_build_object(
              'ID', desired.prop_id::text,
              'Type', 'consume item',
              'Texture', './Localization/en_us/UI/Texture/Icon2.gwo',
              'Icon', '396,756',
              'Random', '0',
              'Distribution', '0,0',
              'Money', '0',
              'Overlap', '99')
          AND mutable.kind = definition.kind
          AND mutable.name_key = definition.name_key
          AND mutable.display_name = definition.display_name
          AND mutable.equipment_slot = definition.equipment_slot
          AND mutable.class_ids = definition.class_ids
          AND mutable.min_level IS NOT DISTINCT FROM definition.min_level
          AND mutable.max_level IS NOT DISTINCT FROM definition.max_level
          AND mutable.hand IS NOT DISTINCT FROM definition.hand
          AND mutable.skill_flag IS NOT DISTINCT FROM definition.skill_flag
          AND mutable.texture = definition.texture
          AND mutable.icon = definition.icon
          AND mutable.stats = definition.stats) <> 39
    THEN
        RAISE EXCEPTION
            'The sealed item publication lacks 39 exact stock-client 7.80-cap Magic Jades';
    END IF;

    INSERT INTO cap780_magic_jade_grant_context (
        account_id, character_id, character_name,
        item_publication_revision, pet_publication_revision,
        old_inventory_revision, empty_slots_before)
    VALUES ($AccountId, 0, '$safeName', v_item_revision, v_pet_revision, 0, 0);
END
`$preflight`$;

DO `$grant`$
DECLARE
    v_character_id integer;
    v_inventory_revision bigint;
    v_empty_slots integer;
    v_required_slots integer;
    v_remaining integer;
    v_added integer;
    v_open_slot smallint;
    v_item record;
    v_jade record;
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

    IF EXISTS (
        SELECT 1
        FROM public.character_items item
        JOIN desired_cap780_magic_jades desired
          ON desired.prop_id = item.prop_id
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
          AND (item.stack < 1 OR item.stack > 99 OR
               item.bound <> 0 OR
               item.item_quality <> 1 OR item.item_grade <> 1 OR
               item.item_exp <> 0 OR item.holy_suit_code <> 0))
    THEN
        RAISE EXCEPTION
            'An existing 7.80-cap Magic Jade violates the simple-item policy';
    END IF;

    UPDATE desired_cap780_magic_jades desired
    SET quantity_before = (
        SELECT COALESCE(sum(item.stack), 0)::integer
        FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
          AND item.prop_id = desired.prop_id);
    IF EXISTS (
        SELECT 1 FROM desired_cap780_magic_jades
        WHERE quantity_before > $TargetQuantityEach)
    THEN
        RAISE EXCEPTION
            'Refusing to trim a 7.80-cap Magic Jade above target %',
            $TargetQuantityEach;
    END IF;

    SELECT count(*)::integer
    INTO v_empty_slots
    FROM generate_series(0, 95) candidate(slot_index)
    WHERE NOT EXISTS (
        SELECT 1 FROM public.character_items occupied
        WHERE occupied.user_id = v_character_id
          AND occupied.item_location = 1
          AND occupied.slot_index = candidate.slot_index);
    SELECT COALESCE(sum(CEIL(GREATEST(
               0,
               $TargetQuantityEach - desired.quantity_before -
                   COALESCE(capacity.available, 0)) /
               99.0)), 0)::integer
    INTO v_required_slots
    FROM desired_cap780_magic_jades desired
    LEFT JOIN LATERAL (
        SELECT sum(99 - item.stack)::integer AS available
        FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
          AND item.prop_id = desired.prop_id
          AND item.bound = 0
    ) capacity ON true;
    IF v_required_slots > v_empty_slots THEN
        RAISE EXCEPTION
            'Magic Jade grant needs % empty bag slots but only % are available',
            v_required_slots, v_empty_slots;
    END IF;

    UPDATE cap780_magic_jade_grant_context
    SET character_id = v_character_id,
        old_inventory_revision = v_inventory_revision,
        empty_slots_before = v_empty_slots,
        required_new_slots = v_required_slots;

    FOR v_jade IN
        SELECT * FROM desired_cap780_magic_jades ORDER BY prop_id
    LOOP
        v_remaining := $TargetQuantityEach - v_jade.quantity_before;
        FOR v_item IN
            SELECT item.id, item.slot_index, item.stack,
                   to_jsonb(item) AS old_item
            FROM public.character_items item
            WHERE item.user_id = v_character_id
              AND item.item_location = 1
              AND item.slot_index BETWEEN 0 AND 95
              AND item.prop_id = v_jade.prop_id
              AND item.bound = 0
              AND item.stack < 99
            ORDER BY item.slot_index
        LOOP
            EXIT WHEN v_remaining = 0;
            v_added := LEAST(v_remaining, 99 - v_item.stack);
            INSERT INTO cap780_magic_jade_grant_mutations (
                action, prop_id, appearance_name, slot_index,
                old_item, old_stack, new_stack)
            VALUES (
                'update', v_jade.prop_id, v_jade.appearance_name,
                v_item.slot_index, v_item.old_item,
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
                SELECT 1 FROM public.character_items occupied
                WHERE occupied.user_id = v_character_id
                  AND occupied.item_location = 1
                  AND occupied.slot_index = candidate.slot_index)
            ORDER BY candidate.slot_index
            LIMIT 1;
            IF v_open_slot IS NULL THEN
                RAISE EXCEPTION 'No authoritative kit-bag slot remains';
            END IF;
            v_added := LEAST(v_remaining, 99);
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code)
            VALUES (
                v_character_id, 1, v_open_slot, v_jade.prop_id,
                1, 1, 0, v_added, 0, 0);
            INSERT INTO cap780_magic_jade_grant_mutations (
                action, prop_id, appearance_name, slot_index,
                old_item, old_stack, new_stack)
            VALUES (
                'add', v_jade.prop_id, v_jade.appearance_name,
                v_open_slot, NULL, 0, v_added);
            v_remaining := v_remaining - v_added;
        END LOOP;
    END LOOP;

    IF EXISTS (SELECT 1 FROM cap780_magic_jade_grant_mutations) THEN
        UPDATE public.character_base character_row
        SET inventory_revision = character_row.inventory_revision + 1
        WHERE character_row.id = v_character_id
          AND character_row.inventory_revision = v_inventory_revision
        RETURNING character_row.inventory_revision INTO v_inventory_revision;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'Inventory revision did not advance exactly once';
        END IF;
        UPDATE cap780_magic_jade_grant_context
        SET new_inventory_revision = v_inventory_revision,
            changed = true;
    ELSE
        UPDATE cap780_magic_jade_grant_context
        SET new_inventory_revision = old_inventory_revision;
    END IF;

    UPDATE desired_cap780_magic_jades desired
    SET quantity_after = (
        SELECT COALESCE(sum(item.stack), 0)::integer
        FROM public.character_items item
        WHERE item.user_id = v_character_id
          AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
          AND item.prop_id = desired.prop_id);
    IF EXISTS (
        SELECT 1 FROM desired_cap780_magic_jades
        WHERE quantity_after <> $TargetQuantityEach) OR
       NOT EXISTS (
           SELECT 1 FROM cap780_magic_jade_grant_context
           WHERE new_inventory_revision = old_inventory_revision +
               CASE WHEN changed THEN 1 ELSE 0 END)
    THEN
        RAISE EXCEPTION 'Magic Jade grant failed post-write validation';
    END IF;
END
`$grant`$;

INSERT INTO public.character_item_audit (
    source, action, user_id, item_location, slot_index,
    prop_id, item_quality, item_grade, item_exp, old_item)
SELECT 'localdev-cap780-magic-jade-grant', mutation.action,
       context.character_id, 1, mutation.slot_index,
       mutation.prop_id, 1, 1, 0, mutation.old_item
FROM cap780_magic_jade_grant_mutations mutation
CROSS JOIN cap780_magic_jade_grant_context context
ORDER BY mutation.ordinal;

COMMIT;
SELECT 'CAP780_MAGIC_JADE_GRANT_RESULT|' || jsonb_build_object(
    'accountId', context.account_id,
    'characterId', context.character_id,
    'characterName', context.character_name,
    'itemPublicationRevision', context.item_publication_revision,
    'petPublicationRevision', context.pet_publication_revision,
    'targetQuantityEach', $TargetQuantityEach,
    'jadeTypes', (SELECT count(*) FROM desired_cap780_magic_jades),
    'totalPiecesAdded',
        (SELECT sum(quantity_after - quantity_before)
         FROM desired_cap780_magic_jades),
    'emptySlotsBefore', context.empty_slots_before,
    'requiredNewSlots', context.required_new_slots,
    'inventoryRevisionBefore', context.old_inventory_revision,
    'inventoryRevisionAfter', context.new_inventory_revision,
    'changed', context.changed,
    'mutations', (SELECT count(*) FROM cap780_magic_jade_grant_mutations),
    'jades', (
        SELECT jsonb_agg(jsonb_build_object(
            'itemId', desired.prop_id,
            'appearance', desired.appearance_name,
            'quantityBefore', desired.quantity_before,
            'quantityAfter', desired.quantity_after)
            ORDER BY desired.prop_id)
        FROM desired_cap780_magic_jades desired))::text
FROM cap780_magic_jade_grant_context context;
"@

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
$exitCode = $LASTEXITCODE
$lines = @($output | ForEach-Object { $_.ToString() })
if ($exitCode -ne 0) {
    throw "7.80-cap Magic Jade grant failed and rolled back:`n$($lines -join "`n")"
}
$prefix = 'CAP780_MAGIC_JADE_GRANT_RESULT|'
$result = $lines | Where-Object {
    $_.StartsWith($prefix)
} | Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($result)) {
    throw 'The 7.80-cap Magic Jade grant returned no verification receipt.'
}
$result.Substring($prefix.Length) | ConvertFrom-Json
