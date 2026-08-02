[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $PostgresContainer = 'godswar-postgres',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $ServerImageContainer = 'godswar-server',

    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_.-]*$')]
    [string] $DatabaseUser = 'godswar',

    [ValidateSet('127.0.0.1', 'localhost')]
    [string] $PostgresHost = '127.0.0.1',

    [ValidateRange(1, 65535)]
    [int] $PostgresPort = 5432,

    [string] $PostgresPassword =
        $env:GODSWAR_TEST_POSTGRES_PASSWORD,

    [switch] $SkipBuild
)

# End-to-end verification uses an exact random disposable database. It never
# points the rollback tool at the LocalDevelopment Tempest database.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rollbackTool = Join-Path $PSScriptRoot `
    'SetLocalDevelopmentItemContentV4Publication.ps1'
$checksProject = Join-Path $repositoryRoot `
    'tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj'
$checksAssembly = Join-Path $repositoryRoot (
    'tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/' +
    'Godswar.Server.ProtocolChecks.dll')
$token = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$database = "godswar_v4rollback_$token"
$dummyServer = "godswar-v4rollback-server-$token"
$createdDatabase = $false
$createdDummyServer = $false
$oldTestConnection = $env:GODSWAR_TEST_POSTGRES_CONNECTION_STRING

if ($database -notmatch '^godswar_v4rollback_[0-9a-f]{12}$') {
    throw 'Internal disposable database safety invariant failed.'
}

function Invoke-DockerChecked {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [Parameter(Mandatory)]
        [string] $Operation
    )

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = ($output | ForEach-Object { [string]$_ }) -join "`n"
        throw "$Operation failed: $message"
    }
    return @($output)
}

function Invoke-PsqlScalar {
    param(
        [Parameter(Mandatory)]
        [string] $Sql
    )

    $output = $Sql | & docker exec -i $PostgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $DatabaseUser -d $database 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = ($output | ForEach-Object { [string]$_ }) -join "`n"
        throw "Disposable PostgreSQL query failed: $message"
    }
    $values = @($output | ForEach-Object { ([string]$_).Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($values.Count -ne 1) {
        throw "Expected one PostgreSQL scalar, received $($values.Count)."
    }
    return $values[0]
}

function Invoke-ItemPublicationCheck {
    if (-not (Test-Path -LiteralPath $checksAssembly -PathType Leaf)) {
        throw 'Release protocol-check assembly is missing.'
    }

    $output = & dotnet $checksAssembly `
        'PostgreSQL item-template publication' 2>&1
    if ($LASTEXITCODE -ne 0) {
        $tail = @($output | Select-Object -Last 80) -join "`n"
        throw "Item publication integration check failed:`n$tail"
    }
    if (-not (@($output) -match
        'PASS PostgreSQL item-template publication')) {
        throw 'Item publication integration check did not report PASS.'
    }
}

function Invoke-RollbackExpectFailure {
    param(
        [Parameter(Mandatory)]
        [string] $ExpectedV5,

        [string] $TargetV4 = '',

        [Parameter(Mandatory)]
        [string] $ExpectedMessage
    )

    $caught = $null
    try {
        & $rollbackTool `
            -ExpectedCurrentV5Revision $ExpectedV5 `
            -TargetV4Revision $TargetV4 `
            -PostgresContainer $PostgresContainer `
            -ServerContainer $dummyServer `
            -Database $database `
            -DatabaseUser $DatabaseUser `
            -Confirm:$false | Out-Null
    }
    catch {
        $caught = $_
    }
    if ($null -eq $caught) {
        throw "Rollback unexpectedly accepted: $ExpectedMessage"
    }
    if ([string]$caught.Exception.Message -notmatch $ExpectedMessage) {
        throw (
            "Rollback failed for the wrong reason. Expected /$ExpectedMessage/; " +
            "received: $($caught.Exception.Message)"
        )
    }
}

function Get-ImmutableContentFingerprint {
    $query = @'
SELECT md5(string_agg(fingerprint.payload, E'\n' ORDER BY fingerprint.payload))
FROM (
    SELECT 'revision|' || to_jsonb(row_data)::text AS payload
    FROM public.item_template_content_revisions row_data
    UNION ALL
    SELECT 'template|' || to_jsonb(row_data)::text
    FROM public.item_template_content_definitions row_data
    UNION ALL
    SELECT 'attribute|' || to_jsonb(row_data)::text
    FROM public.item_attribute_content_definitions row_data
    UNION ALL
    SELECT 'rank|' || to_jsonb(row_data)::text
    FROM public.equipment_rank_content_definitions row_data
    UNION ALL
    SELECT 'effect|' || to_jsonb(row_data)::text
    FROM public.holy_suit_effect_content_definitions row_data
    UNION ALL
    SELECT 'material|' || to_jsonb(row_data)::text
    FROM public.item_material_content_definitions row_data
    UNION ALL
    SELECT 'suit-tier|' || to_jsonb(row_data)::text
    FROM public.holy_suit_tier_content_definitions row_data
    UNION ALL
    SELECT 'suit-upgrade|' || to_jsonb(row_data)::text
    FROM public.holy_suit_upgrade_content_definitions row_data
    UNION ALL
    SELECT 'suit-consumable|' || to_jsonb(row_data)::text
    FROM public.holy_suit_consumable_content_definitions row_data
    UNION ALL
    SELECT 'suit-policy|' || to_jsonb(row_data)::text
    FROM public.holy_suit_operation_policy_content_definitions row_data
) fingerprint;
'@
    return Invoke-PsqlScalar -Sql $query
}

try {
    if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker is required but was not found on PATH.'
    }
    if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet is required but was not found on PATH.'
    }

    $postgresJson = Invoke-DockerChecked `
        -Arguments @('container', 'inspect', $PostgresContainer) `
        -Operation 'Inspect PostgreSQL container'
    $postgres = @($postgresJson | ConvertFrom-Json)[0]
    if (-not [bool]$postgres.State.Running) {
        throw "PostgreSQL container '$PostgresContainer' is not running."
    }
    if ([string]::IsNullOrWhiteSpace($PostgresPassword)) {
        $passwordSettings = @($postgres.Config.Env | Where-Object {
            [string]$_ -like 'POSTGRES_PASSWORD=*'
        })
        if ($passwordSettings.Count -ne 1) {
            throw (
                'Set GODSWAR_TEST_POSTGRES_PASSWORD; the PostgreSQL ' +
                'container does not expose one development password.'
            )
        }
        $PostgresPassword = ([string]$passwordSettings[0]).Substring(
            'POSTGRES_PASSWORD='.Length)
    }

    if (-not $SkipBuild) {
        $build = & dotnet build $checksProject `
            --configuration Release --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Protocol-check build failed:`n$(@($build | Select-Object -Last 80) -join "`n")"
        }
    }

    Invoke-DockerChecked -Arguments @(
        'exec', $PostgresContainer,
        'createdb', '-U', $DatabaseUser, '--owner', $DatabaseUser,
        $database
    ) -Operation 'Create disposable rollback database' | Out-Null
    $createdDatabase = $true

    $sourceImageJson = Invoke-DockerChecked `
        -Arguments @(
            'container', 'inspect',
            '--format', '{{json .Config.Image}}',
            $ServerImageContainer
        ) `
        -Operation 'Read server image'
    $sourceImage = ((@($sourceImageJson) -join "`n") | ConvertFrom-Json)
    Invoke-DockerChecked -Arguments @(
        'create', '--name', $dummyServer,
        '--env', 'GODSWAR_RUNTIME_PROFILE=LocalDevelopment',
        '--env', 'GODSWAR_STORAGE_PROVIDER=postgres',
        '--env',
        "GODSWAR_POSTGRES_CONNECTION_STRING=Host=postgres;Port=5432;Database=$database;Username=$DatabaseUser;Pooling=true",
        '--entrypoint', '/bin/sh', $sourceImage, '-c', 'sleep 300'
    ) -Operation 'Create stopped LocalDevelopment server marker' | Out-Null
    $createdDummyServer = $true

    $env:GODSWAR_TEST_POSTGRES_CONNECTION_STRING =
        "Host=$PostgresHost;Port=$PostgresPort;Database=$database;" +
        "Username=$DatabaseUser;Password=$PostgresPassword;Pooling=false"
    Invoke-ItemPublicationCheck

    $v5 = Invoke-PsqlScalar -Sql @'
SELECT publication.revision
FROM public.item_template_content_publication publication
JOIN public.item_template_content_revisions release
  ON release.revision = publication.revision
WHERE publication.family = 'items' AND release.manifest_version = 5;
'@
    $v4 = Invoke-PsqlScalar -Sql @'
SELECT revision
FROM public.item_template_content_revisions
WHERE manifest_version = 4 AND sealed_at IS NOT NULL;
'@
    $beforeFingerprint = Get-ImmutableContentFingerprint

    Invoke-DockerChecked -Arguments @('start', $dummyServer) `
        -Operation 'Start live-server refusal marker' | Out-Null
    try {
        Invoke-RollbackExpectFailure -ExpectedV5 $v5 `
            -TargetV4 $v4 -ExpectedMessage 'while server container.*running'
    }
    finally {
        Invoke-DockerChecked -Arguments @('stop', $dummyServer) `
            -Operation 'Stop live-server refusal marker' | Out-Null
    }

    Invoke-RollbackExpectFailure -ExpectedV5 $v5 `
        -TargetV4 ('A' * 64) -ExpectedMessage 'is missing'
    Invoke-RollbackExpectFailure -ExpectedV5 $v5 `
        -TargetV4 $v5 -ExpectedMessage 'not v4'

    $unsealedV4 = 'B' * 64
    $insertUnsealed = @"
INSERT INTO public.item_template_content_revisions (
    revision, entry_count, source, manifest_version,
    attribute_count, equipment_rank_count, holy_suit_effect_count,
    material_policy_count, material_recipe_count,
    holy_suit_tier_count, holy_suit_upgrade_count,
    holy_suit_consumable_count, holy_suit_policy_count)
SELECT '$unsealedV4', entry_count, 'rollback-unsealed-negative', 4,
       attribute_count, equipment_rank_count, holy_suit_effect_count,
       material_policy_count, material_recipe_count, 0, 0, 0, 0
FROM public.item_template_content_revisions
WHERE revision = '$v4';
SELECT 'inserted';
"@
    Invoke-PsqlScalar -Sql $insertUnsealed | Out-Null
    Invoke-RollbackExpectFailure -ExpectedV5 $v5 `
        -TargetV4 $unsealedV4 -ExpectedMessage 'is unsealed'
    Invoke-RollbackExpectFailure -ExpectedV5 $v5 `
        -ExpectedMessage 'target is ambiguous'

    $beforeRollbackFingerprint = Get-ImmutableContentFingerprint
    $receipt = & $rollbackTool `
        -ExpectedCurrentV5Revision $v5 `
        -TargetV4Revision $v4 `
        -PostgresContainer $PostgresContainer `
        -ServerContainer $dummyServer `
        -Database $database `
        -DatabaseUser $DatabaseUser `
        -Confirm:$false
    if ([string]$receipt.currentRevision -ne $v4 -or
        [int]$receipt.manifestVersion -ne 4 -or
        [bool]$receipt.contentRowsMutated) {
        throw 'Rollback receipt did not prove the exact v4 pointer-only change.'
    }
    $publishedAfterRollback = Invoke-PsqlScalar -Sql @'
SELECT publication.revision
FROM public.item_template_content_publication publication
JOIN public.item_template_content_revisions release
  ON release.revision = publication.revision
WHERE publication.family = 'items' AND release.manifest_version = 4;
'@
    if ($publishedAfterRollback -ne $v4) {
        throw 'The official items pointer did not select the expected v4.'
    }
    if ((Get-ImmutableContentFingerprint) -ne $beforeRollbackFingerprint) {
        throw 'Rollback mutated immutable item-content rows.'
    }
    Invoke-RollbackExpectFailure -ExpectedV5 $v5 `
        -TargetV4 $v4 -ExpectedMessage 'does not equal expected v5'

    Invoke-ItemPublicationCheck
    $publishedAfterForward = Invoke-PsqlScalar -Sql @'
SELECT publication.revision
FROM public.item_template_content_publication publication
JOIN public.item_template_content_revisions release
  ON release.revision = publication.revision
WHERE publication.family = 'items' AND release.manifest_version = 5;
'@
    if ($publishedAfterForward -ne $v5) {
        throw 'The v5 publisher did not re-forward the official items pointer.'
    }
    if ($beforeFingerprint -eq (Get-ImmutableContentFingerprint)) {
        throw 'Negative fixture was not represented in the immutable fingerprint.'
    }

    [pscustomobject]@{
        Status = 'PASS'
        Database = $database
        V5Revision = $v5
        V4Revision = $v4
        LiveServerRejected = $true
        MissingRejected = $true
        NonV4Rejected = $true
        UnsealedRejected = $true
        AmbiguousRejected = $true
        PointerOnlyRollback = $true
        ReForwardedToV5 = $true
    }
}
finally {
    $env:GODSWAR_TEST_POSTGRES_CONNECTION_STRING = $oldTestConnection
    if ($createdDummyServer) {
        & docker container rm --force $dummyServer 2>&1 | Out-Null
    }
    if ($createdDatabase) {
        & docker exec $PostgresContainer dropdb `
            -U $DatabaseUser --if-exists --force $database 2>&1 | Out-Null
    }
}
