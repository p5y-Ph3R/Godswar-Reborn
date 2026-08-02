[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $ExpectedCurrentV5Revision,

    [ValidatePattern('^$|^[0-9A-Fa-f]{64}$')]
    [string] $TargetV4Revision = '',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $PostgresContainer = 'godswar-postgres',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $ServerContainer = 'godswar-server',

    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_.-]*$')]
    [string] $Database = 'godswar',

    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_.-]*$')]
    [string] $DatabaseUser = 'godswar'
)

# This is an offline LocalDevelopment recovery boundary, not a general content
# administration command. It only changes the singleton publication pointer.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DockerInspect {
    param(
        [Parameter(Mandatory)]
        [string] $Container
    )

    $raw = & docker container inspect $Container 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Required Docker container '$Container' does not exist."
    }

    try {
        return @($raw | ConvertFrom-Json)[0]
    }
    catch {
        throw "Docker returned invalid metadata for '$Container'."
    }
}

function Assert-NoLocalGodswarServer {
    try {
        $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop)
    }
    catch {
        throw 'Could not verify that no local Godswar server process is running.'
    }

    $matches = @($processes | Where-Object {
        $line = [string]$_.CommandLine
        -not [string]::IsNullOrWhiteSpace($line) -and
        ($line -match '(?i)Godswar\.Server\.dll' -or
         $line -match '(?i)Godswar\.Server[/\\]Godswar\.Server\.csproj')
    })
    if ($matches.Count -ne 0) {
        throw 'Refusing rollback while a local Godswar server process is running.'
    }
}

if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required but was not found on PATH.'
}

$server = Invoke-DockerInspect -Container $ServerContainer
if ([bool]$server.State.Running) {
    throw (
        "Refusing rollback while server container '$ServerContainer' is " +
        'running. Drain and stop it cleanly first.'
    )
}

$serverEnvironment = @($server.Config.Env | ForEach-Object { [string]$_ })
if ($serverEnvironment -notcontains 'GODSWAR_RUNTIME_PROFILE=LocalDevelopment') {
    throw (
        "Server container '$ServerContainer' is not explicitly configured " +
        'with GODSWAR_RUNTIME_PROFILE=LocalDevelopment.'
    )
}

$connectionSetting = @($serverEnvironment | Where-Object {
    $_ -like 'GODSWAR_POSTGRES_CONNECTION_STRING=*'
})
if ($connectionSetting.Count -ne 1) {
    throw 'The stopped server must expose exactly one PostgreSQL connection setting.'
}
$databasePattern = '(?i)(?:^|;)\s*Database\s*=\s*' +
    [Regex]::Escape($Database) + '\s*(?:;|$)'
$connectionValue = $connectionSetting[0].Substring(
    'GODSWAR_POSTGRES_CONNECTION_STRING='.Length)
if ($connectionValue -notmatch $databasePattern) {
    throw (
        "Server container '$ServerContainer' is not configured for database " +
        "'$Database'."
    )
}

Assert-NoLocalGodswarServer

$postgres = Invoke-DockerInspect -Container $PostgresContainer
if (-not [bool]$postgres.State.Running) {
    throw "PostgreSQL container '$PostgresContainer' is not running."
}
$health = $postgres.State.PSObject.Properties['Health']
if ($null -ne $health -and
    $null -ne $health.Value -and
    [string]$health.Value.Status -ne 'healthy') {
    throw (
        "PostgreSQL container '$PostgresContainer' is not healthy " +
        "(state: $($health.Value.Status))."
    )
}

$expectedV5 = $ExpectedCurrentV5Revision.ToUpperInvariant()
$requestedV4 = $TargetV4Revision.ToUpperInvariant()
$targetClause = if ([string]::IsNullOrEmpty($requestedV4)) {
    'NULL::text'
}
else {
    "'$requestedV4'::text"
}

$sql = @"
\set ON_ERROR_STOP on
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
SELECT pg_advisory_xact_lock(5283924461224152910);

CREATE TEMP TABLE selected_item_v4_revision (
    revision text PRIMARY KEY
) ON COMMIT DROP;

DO `$rollback`$
DECLARE
    expected_v5 constant text := '$expectedV5';
    requested_v4 text := $targetClause;
    current_revision text;
    current_release public.item_template_content_revisions%ROWTYPE;
    target_release public.item_template_content_revisions%ROWTYPE;
    v4_count integer;
    actual_templates integer;
    actual_attributes integer;
    actual_ranks integer;
    actual_effects integer;
    actual_materials integer;
    actual_recipes integer;
    actual_suit_tiers integer;
    actual_suit_upgrades integer;
    actual_suit_consumables integer;
    actual_suit_policies integer;
    changed_rows integer;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM public.schema_migrations
        WHERE migration_id = '20260801_046_holy_suit_content_release'
    ) THEN
        RAISE EXCEPTION 'Holy Suit migration 046 is not installed';
    END IF;

    IF EXISTS (
        SELECT 1 FROM public.character_base
        WHERE checkpoint_owner_id IS NOT NULL
    ) THEN
        RAISE EXCEPTION
            'active character checkpoint ownership remains; drain the server first';
    END IF;

    SELECT publication.revision INTO current_revision
    FROM public.item_template_content_publication publication
    WHERE publication.family = 'items'
    FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'the official items publication is missing';
    END IF;
    IF current_revision <> expected_v5 THEN
        RAISE EXCEPTION
            'current items publication % does not equal expected v5 %',
            current_revision, expected_v5;
    END IF;

    SELECT * INTO current_release
    FROM public.item_template_content_revisions
    WHERE revision = current_revision;
    IF current_release.manifest_version <> 5 THEN
        RAISE EXCEPTION
            'current items publication % is manifest v%, not v5',
            current_revision, current_release.manifest_version;
    END IF;
    IF current_release.sealed_at IS NULL THEN
        RAISE EXCEPTION 'current v5 revision % is unsealed', current_revision;
    END IF;

    SELECT count(*)::integer INTO actual_templates
    FROM public.item_template_content_definitions
    WHERE revision = current_revision;
    SELECT count(*)::integer INTO actual_attributes
    FROM public.item_attribute_content_definitions
    WHERE revision = current_revision;
    SELECT count(*)::integer INTO actual_ranks
    FROM public.equipment_rank_content_definitions
    WHERE revision = current_revision;
    SELECT count(*)::integer INTO actual_effects
    FROM public.holy_suit_effect_content_definitions
    WHERE revision = current_revision;
    SELECT count(*)::integer,
           count(*) FILTER (WHERE recipe_kind IS NOT NULL)::integer
      INTO actual_materials, actual_recipes
    FROM public.item_material_content_definitions
    WHERE revision = current_revision;
    SELECT count(*)::integer INTO actual_suit_tiers
    FROM public.holy_suit_tier_content_definitions
    WHERE revision = current_revision;
    SELECT count(*)::integer INTO actual_suit_upgrades
    FROM public.holy_suit_upgrade_content_definitions
    WHERE revision = current_revision;
    SELECT count(*)::integer INTO actual_suit_consumables
    FROM public.holy_suit_consumable_content_definitions
    WHERE revision = current_revision;
    SELECT count(*)::integer INTO actual_suit_policies
    FROM public.holy_suit_operation_policy_content_definitions
    WHERE revision = current_revision;
    IF actual_templates <> current_release.entry_count
       OR actual_attributes <> current_release.attribute_count
       OR actual_ranks <> current_release.equipment_rank_count
       OR actual_effects <> current_release.holy_suit_effect_count
       OR actual_materials <> current_release.material_policy_count
       OR actual_recipes <> current_release.material_recipe_count
       OR actual_suit_tiers <> current_release.holy_suit_tier_count
       OR actual_suit_upgrades <> current_release.holy_suit_upgrade_count
       OR actual_suit_consumables <>
            current_release.holy_suit_consumable_count
       OR actual_suit_policies <> current_release.holy_suit_policy_count THEN
        RAISE EXCEPTION 'current v5 revision % is incomplete', current_revision;
    END IF;

    IF requested_v4 IS NULL THEN
        SELECT count(*)::integer, min(revision)
          INTO v4_count, requested_v4
        FROM public.item_template_content_revisions
        WHERE manifest_version = 4;
        IF v4_count = 0 THEN
            RAISE EXCEPTION 'no retained manifest-v4 revision exists';
        END IF;
        IF v4_count <> 1 THEN
            RAISE EXCEPTION
                'manifest-v4 target is ambiguous: % revisions exist; specify none until reconciled',
                v4_count;
        END IF;
    END IF;

    SELECT * INTO target_release
    FROM public.item_template_content_revisions
    WHERE revision = requested_v4;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'requested rollback revision % is missing', requested_v4;
    END IF;
    IF target_release.manifest_version <> 4 THEN
        RAISE EXCEPTION
            'requested rollback revision % is manifest v%, not v4',
            requested_v4, target_release.manifest_version;
    END IF;
    IF target_release.sealed_at IS NULL THEN
        RAISE EXCEPTION
            'requested manifest-v4 revision % is unsealed', requested_v4;
    END IF;
    IF target_release.holy_suit_tier_count <> 0
       OR target_release.holy_suit_upgrade_count <> 0
       OR target_release.holy_suit_consumable_count <> 0
       OR target_release.holy_suit_policy_count <> 0 THEN
        RAISE EXCEPTION
            'requested manifest-v4 revision % contains v5-only declarations',
            requested_v4;
    END IF;

    SELECT count(*)::integer INTO actual_templates
    FROM public.item_template_content_definitions
    WHERE revision = requested_v4;
    SELECT count(*)::integer INTO actual_attributes
    FROM public.item_attribute_content_definitions
    WHERE revision = requested_v4;
    SELECT count(*)::integer INTO actual_ranks
    FROM public.equipment_rank_content_definitions
    WHERE revision = requested_v4;
    SELECT count(*)::integer INTO actual_effects
    FROM public.holy_suit_effect_content_definitions
    WHERE revision = requested_v4;
    SELECT count(*)::integer,
           count(*) FILTER (WHERE recipe_kind IS NOT NULL)::integer
      INTO actual_materials, actual_recipes
    FROM public.item_material_content_definitions
    WHERE revision = requested_v4;
    SELECT
        (SELECT count(*) FROM public.holy_suit_tier_content_definitions
         WHERE revision = requested_v4),
        (SELECT count(*) FROM public.holy_suit_upgrade_content_definitions
         WHERE revision = requested_v4),
        (SELECT count(*) FROM public.holy_suit_consumable_content_definitions
         WHERE revision = requested_v4),
        (SELECT count(*)
         FROM public.holy_suit_operation_policy_content_definitions
         WHERE revision = requested_v4)
      INTO actual_suit_tiers, actual_suit_upgrades,
           actual_suit_consumables, actual_suit_policies;
    IF actual_templates <> target_release.entry_count
       OR actual_attributes <> target_release.attribute_count
       OR actual_ranks <> target_release.equipment_rank_count
       OR actual_effects <> target_release.holy_suit_effect_count
       OR actual_materials <> target_release.material_policy_count
       OR actual_recipes <> target_release.material_recipe_count
       OR target_release.entry_count <= 0
       OR target_release.attribute_count <= 0
       OR target_release.equipment_rank_count <= 0
       OR target_release.holy_suit_effect_count <= 0
       OR target_release.material_policy_count <= 0
       OR target_release.material_recipe_count <= 0
       OR actual_suit_tiers <> 0 OR actual_suit_upgrades <> 0
       OR actual_suit_consumables <> 0 OR actual_suit_policies <> 0 THEN
        RAISE EXCEPTION
            'requested manifest-v4 revision % is incomplete', requested_v4;
    END IF;

    INSERT INTO selected_item_v4_revision (revision) VALUES (requested_v4);
    UPDATE public.item_template_content_publication
    SET revision = requested_v4, published_at = clock_timestamp()
    WHERE family = 'items' AND revision = expected_v5;
    GET DIAGNOSTICS changed_rows = ROW_COUNT;
    IF changed_rows <> 1 THEN
        RAISE EXCEPTION 'items publication compare-and-swap did not change one row';
    END IF;
END
`$rollback`$;

SELECT json_build_object(
    'status', 'repointed',
    'previousRevision', '$expectedV5',
    'currentRevision', selected.revision,
    'manifestVersion', 4,
    'contentRowsMutated', false
)::text
FROM selected_item_v4_revision selected;
COMMIT;
"@

$targetDescription = if ([string]::IsNullOrEmpty($requestedV4)) {
    'the single retained, complete manifest-v4 revision'
}
else {
    "manifest-v4 revision $requestedV4"
}
if (-not $PSCmdlet.ShouldProcess(
        "database '$Database' in '$PostgresContainer'",
        "Atomically repoint items from $expectedV5 to $targetDescription")) {
    return
}

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
if ($LASTEXITCODE -ne 0) {
    $safeOutput = ($output | ForEach-Object { [string]$_ }) -join "`n"
    throw "Item-content v4 rollback failed closed: $safeOutput"
}

$resultLine = @($output | ForEach-Object { [string]$_ } | Where-Object {
    $_.TrimStart().StartsWith('{')
} | Select-Object -Last 1)
if ($resultLine.Count -ne 1) {
    throw 'Rollback committed but did not return one structured receipt.'
}

$result = $resultLine[0] | ConvertFrom-Json
Write-Output $result
