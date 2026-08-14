[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [ValidatePattern('^godswar(?:_pet_rank100_[a-f0-9]{8,12})?$')]
    [string]$Database = 'godswar',

    [switch]$DisposableTest
)

# One-time isolated-development fixture for account 13 / character 2 / pet 1.
# It changes only the main pet's rank and pet revision. Status is read-only;
# Apply requires the server, Origin, and targeted Redis login leases offline.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-server'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$operationText =
    'localdev|pet-rank100|account:13|character:2|pet:1|v1'
$requestText = $operationText +
    '|source-rank:5.590000|source-revision:1202' +
    '|target-rank:100.000000|target-revision:1203' +
    '|skills:3920,4519,5220,5620@tier6'

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
. (Join-Path $PSScriptRoot `
    'SetLocalDevelopmentPetRank100.Sql.ps1')

if ($DisposableTest) {
    if ($Database -notmatch '^godswar_pet_rank100_[a-f0-9]{8,12}$') {
        throw 'Disposable tests require the pet-rank100 database prefix.'
    }
}
elseif ($Database -ne 'godswar') {
    throw 'The real fixture can target only the godswar database.'
}

function Invoke-RankFixturePsql([string]$Sql, [string]$Marker) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d $Database 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { $_.ToString() })
    if ($exitCode -ne 0) {
        throw "Pet-rank fixture failed and rolled back:`n$($lines -join "`n")"
    }
    $receipt = $lines | Where-Object {
        $_.StartsWith($Marker, [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no pet-rank fixture receipt.'
    }
    $receipt.Substring($Marker.Length) | ConvertFrom-Json
}

function Assert-PetRankFixtureOffline($Environment) {
    $serverName = $Environment.Server.Name.TrimStart('/')
    $currentServer = Get-RepairContainer $serverName
    Assert-RepairContainer $currentServer $serverName ''
    if ($currentServer.State.Running) {
        throw "Stop '$serverName' cleanly before changing pet rank."
    }
    if (Test-OriginRunning) {
        throw 'Close Origin.exe before changing pet rank.'
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
$operationHex = Get-RepairSha256Hex $operationText
$requestHashHex = Get-RepairSha256Hex $requestText
$status = Invoke-RankFixturePsql `
    (Get-PetRank100StatusSql $operationHex $requestHashHex) `
    'PET_RANK100_FIXTURE_STATUS|'

if ($status.receiptCount -gt 1 -or
    ($status.receiptCount -eq 1 -and
     (-not $status.receiptValid -or -not $status.postReady))) {
    throw 'Existing pet-rank fixture receipt or post-state is inconsistent.'
}
$sourceReady = $status.identityReady -and $status.sourceReady -and
    $status.pendingPreviewCount -eq 0
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
    PetId = 1
    CurrentRank = $status.petRank
    TargetRank = 100.000000
    CurrentPetRevision = $status.petRevision
    TargetPetRevision = 1203
    SpeciesId = $status.speciesId
    LearnedSkills = $status.skillState
    PendingPreviewCount = $status.pendingPreviewCount
    ReceiptAuditId = $status.receiptAuditId
    ServerRunning = $serverRunning
    OriginRunning = $originRunning
    RedisPlayerLoginKeyCount = $redisKeyCount
    OperationIdSha256 = $operationHex.ToUpperInvariant()
    RequestHashSha256 = $requestHashHex.ToUpperInvariant()
}
if ($Mode -eq 'Status') { return $summary }
if ($state -eq 'Applied') { return $summary }
if ($state -ne 'Ready') {
    throw 'The pinned source state or offline guard is not ready.'
}
if (-not $DisposableTest) {
    Assert-PetRankFixtureOffline $environment
}
if (-not $PSCmdlet.ShouldProcess(
        'isolated-development character 2 / pet 1',
        'Set pet rank from 5.59 to exactly 100.00')) {
    return
}
if (-not $DisposableTest) {
    Assert-PetRankFixtureOffline $environment
}

$result = Invoke-RankFixturePsql `
    (Get-PetRank100ApplySql $operationHex $requestHashHex) `
    'PET_RANK100_FIXTURE_RESULT|'
if ($result.currentRank -ne 100 -or
    $result.currentPetRevision -ne 1203 -or
    $result.auditId -le 0) {
    throw 'The committed pet-rank receipt failed post-verification.'
}
$result
