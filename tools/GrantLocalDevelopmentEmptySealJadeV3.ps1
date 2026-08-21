[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [ValidatePattern('^godswar(?:_empty_seal_jade_v3_[a-f0-9]{10})?$')]
    [string]$Database = 'godswar',

    [switch]$DisposableTest
)

# Exact-state third Seal Jade fixture for isolated development only. Status is
# read-only. Live Apply requires the server, Origin, and account leases off.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-tempest-openworld-01'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$operationText =
    'localdev|empty-seal-jade|account:13|character:2|v3'
$requestText = $operationText +
    '|inventory:735->736|item:10108x1@0|hp:134341|mp:6047' +
    '|vitals:9896|pet:1@1414|energy:100/100'

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
. (Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentEmptySealJadeV3.Sql.Common.ps1')
. (Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentEmptySealJadeV3.Sql.Status.ps1')
. (Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentEmptySealJadeV3.Sql.Apply.ps1')

if ($DisposableTest) {
    if ($Database -notmatch
        '^godswar_empty_seal_jade_v3_[a-f0-9]{10}$') {
        throw 'Disposable tests require the v3 Seal Jade DB prefix.'
    }
}
elseif ($Database -ne 'godswar') {
    throw 'The live fixture can target only the godswar database.'
}

function Invoke-EmptySealJadeV3Psql([string]$Sql, [string]$Marker) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d $Database 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { $_.ToString() })
    if ($exitCode -ne 0) {
        throw "V3 Seal Jade fixture failed and rolled back:`n$($lines -join "`n")"
    }
    $receipt = $lines | Where-Object {
        $_.StartsWith($Marker, [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no v3 Seal Jade receipt.'
    }
    $receipt.Substring($Marker.Length) | ConvertFrom-Json
}

function Test-EmptySealJadeV3CatalogSource {
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

$environment = Initialize-RebirthRepairEnvironment `
    $postgresContainer $serverContainer $redisContainer
$serverRunning = [bool]$environment.Server.State.Running
$originRunning = Test-OriginRunning
$redisKeyCount = Get-RebirthRepairRedisKeyCount $redisContainer
$catalogReviewed = Test-EmptySealJadeV3CatalogSource
$operationHex = Get-RepairSha256Hex $operationText
$requestHashHex = Get-RepairSha256Hex $requestText
$status = Invoke-EmptySealJadeV3Psql `
    (Get-EmptySealJadeV3StatusSql $operationHex $requestHashHex) `
    'EMPTY_SEAL_JADE_V3_STATUS|'

if ($status.receiptCount -gt 1 -or
    ($status.receiptCount -eq 1 -and -not $status.receiptValid)) {
    throw 'The permanent v3 receipt or linked item audit is inconsistent.'
}
$sourceReady = $catalogReviewed -and [bool]$status.sourceReady
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
    TargetInventoryRevision = 736
    CurrentHp = $status.currentHp
    CurrentMp = $status.currentMp
    VitalsRevision = $status.vitalsRevision
    CurrentBagRows = $status.bagRows
    CurrentBagUnits = $status.bagUnits
    MainPet = $status.mainPet
    PreservedStateSha256 = $status.preservedStateSha256
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
    throw 'Exact repaired state or every offline fence is not ready.'
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environment $redisContainer
}
if (-not $PSCmdlet.ShouldProcess(
        'isolated-development character 2 / bag slot 0',
        'Append one empty Seal Jade with permanent v3 audits')) {
    return
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environment $redisContainer
}

$result = Invoke-EmptySealJadeV3Psql `
    (Get-EmptySealJadeV3ApplySql $operationHex $requestHashHex) `
    'EMPTY_SEAL_JADE_V3_RESULT|'
$verified = Invoke-EmptySealJadeV3Psql `
    (Get-EmptySealJadeV3StatusSql $operationHex $requestHashHex) `
    'EMPTY_SEAL_JADE_V3_STATUS|'
if ($result.status -ne 'Applied' -or -not $result.changed -or
    $result.auditId -le 0 -or $result.itemAuditId -le 0 -or
    $verified.receiptCount -ne 1 -or -not $verified.receiptValid -or
    -not $verified.postReady -or
    $verified.receiptAuditId -ne $result.auditId -or
    $verified.currentHp -ne 134341 -or $verified.currentMp -ne 6047 -or
    $verified.mainPet.energy -ne 100) {
    throw 'The committed v3 Seal Jade grant failed exact read-back.'
}
$result
