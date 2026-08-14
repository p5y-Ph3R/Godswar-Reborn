[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [ValidatePattern('^godswar(?:_empty_seal_jade_[a-f0-9]{10})?$')]
    [string]$Database = 'godswar',

    [switch]$DisposableTest
)

# One-purpose preserve-only fixture for account 13 / character 2. It appends
# one unbound empty Seal Jade (10108) to the exact lowest free bag slot 0.
# Status is read-only. Live Apply requires the server, Origin, and leases off.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-server'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$operationText =
    'localdev|empty-seal-jade|account:13|character:2|v1'
$requestText = $operationText +
    '|source-inv:729|target-inv:730|preserve:10104x94@24' +
    '|item:10108x1@0|item-release:1851FC6E|pet-revision:1406'

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
. (Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentEmptySealJade.Sql.Common.ps1')
. (Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentEmptySealJade.Sql.Status.ps1')
. (Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentEmptySealJade.Sql.Apply.ps1')

if ($DisposableTest) {
    if ($Database -notmatch '^godswar_empty_seal_jade_[a-f0-9]{10}$') {
        throw 'Disposable tests require the empty-seal-jade DB prefix.'
    }
}
elseif ($Database -ne 'godswar') {
    throw 'The real fixture can target only the godswar database.'
}

function Invoke-EmptySealJadePsql([string]$Sql, [string]$Marker) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d $Database 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { $_.ToString() })
    if ($exitCode -ne 0) {
        throw "Empty Seal Jade fixture failed and rolled back:`n$($lines -join "`n")"
    }
    $receipt = $lines | Where-Object {
        $_.StartsWith($Marker, [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no empty Seal Jade receipt.'
    }
    $receipt.Substring($Marker.Length) | ConvertFrom-Json
}

function Test-EmptySealJadeCatalogSource {
    $path = Join-Path $PSScriptRoot `
        '..\src\Godswar.Server\Application\Items\PinnedDeveloperItemGrantCatalog.Pets.cs'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    $source = Get-Content -Raw -LiteralPath $path
    $source.IndexOf(
        'new(10108, "Seal Jade (Empty)", "936,972"',
        [StringComparison]::Ordinal) -ge 0 -and
    $source.IndexOf(
        '["emptysealjade", "sealjadeempty", "sealstone"]',
        [StringComparison]::Ordinal) -ge 0
}

function Assert-EmptySealJadeOffline($Environment) {
    $serverName = $Environment.Server.Name.TrimStart('/')
    $currentServer = Get-RepairContainer $serverName
    Assert-RepairContainer $currentServer $serverName ''
    if ($currentServer.State.Running) {
        throw "Stop '$serverName' cleanly before granting the Seal Jade."
    }
    if (Test-OriginRunning) {
        throw 'Close Origin.exe before granting the Seal Jade.'
    }
    if ((Get-RebirthRepairRedisKeyCount $redisContainer) -ne 0) {
        throw 'Redis still has a player/login lease for account 13 or character 2.'
    }
}

$environment = Initialize-RebirthRepairEnvironment `
    $postgresContainer $serverContainer $redisContainer
$serverRunning = [bool]$environment.Server.State.Running
$originRunning = Test-OriginRunning
$redisKeyCount = Get-RebirthRepairRedisKeyCount $redisContainer
$catalogReviewed = Test-EmptySealJadeCatalogSource
$operationHex = Get-RepairSha256Hex $operationText
$requestHashHex = Get-RepairSha256Hex $requestText
$status = Invoke-EmptySealJadePsql `
    (Get-EmptySealJadeStatusSql $operationHex $requestHashHex) `
    'EMPTY_SEAL_JADE_STATUS|'

if ($status.receiptCount -gt 1 -or
    ($status.receiptCount -eq 1 -and -not $status.receiptValid)) {
    throw 'The permanent Seal Jade receipt or item-audit chain is inconsistent.'
}
$sourceReady = $catalogReviewed -and $status.sourceReady
$offline = -not $serverRunning -and -not $originRunning -and
    $redisKeyCount -eq 0
$state = if ($status.receiptCount -eq 1) { 'Applied' }
    elseif (-not $sourceReady) { 'Refused' }
    elseif ($DisposableTest -or $offline) { 'Ready' }
    else { 'AwaitingOffline' }
$summary = [pscustomobject]@{
    Status = $state
    Database = $Database
    AccountId = 13
    CharacterId = 2
    ItemId = 10108
    ItemName = 'Seal Jade (Empty)'
    Quantity = 1
    TargetSlot = 0
    CurrentInventoryRevision = $status.inventoryRevision
    TargetInventoryRevision = 730
    CurrentBagRows = $status.bagRows
    CurrentBagUnits = $status.bagUnits
    PreservedSourceItem = '10104 x94 @ bag slot 24'
    PreservedItemsSha256 = $status.preservedItemsSha256
    MainPet = $status.mainPet
    PetCount = $status.petCount
    PublishedItemRevision = $status.itemRevision
    ContentValid = $status.contentValid
    CatalogReviewed = $catalogReviewed
    SourceReady = $status.sourceReady
    PostReady = $status.postReady
    ReceiptAuditId = $status.receiptAuditId
    LinkedItemAuditCount = $status.linkedItemAuditCount
    ServerRunning = $serverRunning
    OriginRunning = $originRunning
    RedisPlayerLoginKeyCount = $redisKeyCount
    OperationIdSha256 = $operationHex.ToUpperInvariant()
    RequestHashSha256 = $requestHashHex.ToUpperInvariant()
}
if ($Mode -eq 'Status' -or $state -eq 'Applied') { return $summary }
if ($state -ne 'Ready') {
    throw 'Exact content, source inventory/pet state, or offline guard is not ready.'
}
if (-not $DisposableTest) {
    Assert-EmptySealJadeOffline $environment
}
if (-not $PSCmdlet.ShouldProcess(
        'isolated-development character 2 / bag slot 0',
        'Append exactly one empty Seal Jade and permanent audits')) {
    return
}
if (-not $DisposableTest) {
    Assert-EmptySealJadeOffline $environment
}

$result = Invoke-EmptySealJadePsql `
    (Get-EmptySealJadeApplySql $operationHex $requestHashHex) `
    'EMPTY_SEAL_JADE_RESULT|'
$verified = Invoke-EmptySealJadePsql `
    (Get-EmptySealJadeStatusSql $operationHex $requestHashHex) `
    'EMPTY_SEAL_JADE_STATUS|'
if ($result.status -ne 'Applied' -or -not $result.changed -or
    $result.auditId -le 0 -or $result.itemAuditId -le 0 -or
    $verified.receiptCount -ne 1 -or -not $verified.receiptValid -or
    -not $verified.postReady -or
    $verified.receiptAuditId -ne $result.auditId) {
    throw 'The committed Seal Jade receipt failed read-back verification.'
}
$result
