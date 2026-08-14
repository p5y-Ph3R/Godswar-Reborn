[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [ValidatePattern('^godswar(?:_pet_rebirth_fixture_[a-f0-9]{8,12})?$')]
    [string]$Database = 'godswar',

    [switch]$DisposableTest
)

# One-purpose isolated-development fixture for account 13 / character 2 /
# pet 1. Status is read-only. Apply requires the game server and Origin to be
# stopped and all three relevant Redis leases to be absent.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-server'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$family = 'pet_rebirth_skillbook_fixture'
$operationText =
    'localdev|pet-rebirth-skillbooks|account:13|character:2|pet:1|v2'
$requestText = $operationText +
    '|source-inv:690|source-pet-rev:1168|source-exp:7597955' +
    '|delta:1500000000|target:1507597955' +
    '|bag:skills-10464-10469,10510-10515,10590-10595,10700-10705' +
    '|rebirth-spirit:10104x99@24'

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
. (Join-Path $PSScriptRoot `
    'PrepareLocalDevelopmentPetRebirthSkillBookFixture.Sql.ps1')
. (Join-Path $PSScriptRoot `
    'PrepareLocalDevelopmentPetRebirthSkillBookFixture.Apply.Audit.ps1')
. (Join-Path $PSScriptRoot `
    'PrepareLocalDevelopmentPetRebirthSkillBookFixture.Apply.ps1')

if ($DisposableTest) {
    if ($Database -notmatch
        '^godswar_pet_rebirth_fixture_[a-f0-9]{8,12}$') {
        throw 'Disposable tests require the pet-rebirth-fixture DB prefix.'
    }
}
elseif ($Database -ne 'godswar') {
    throw 'The real fixture can target only the godswar database.'
}

function Invoke-FixturePsql([string]$Sql, [string]$Marker) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d $Database 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { $_.ToString() })
    if ($exitCode -ne 0) {
        throw "Pet rebirth fixture failed and rolled back:`n$($lines -join "`n")"
    }
    $receipt = $lines | Where-Object {
        $_.StartsWith($Marker, [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no fixture receipt.'
    }
    $receipt.Substring($Marker.Length) | ConvertFrom-Json
}

$environment = Initialize-RebirthRepairEnvironment `
    $postgresContainer $serverContainer $redisContainer
$serverRunning = [bool]$environment.Server.State.Running
$originRunning = Test-OriginRunning
$redisKeyCount = Get-RebirthRepairRedisKeyCount $redisContainer
$operationHex = Get-RepairSha256Hex $operationText
$requestHashHex = Get-RepairSha256Hex $requestText
$status = Invoke-FixturePsql `
    (Get-PetRebirthSkillBookStatusSql $operationHex $requestHashHex) `
    'PET_REBIRTH_SKILLBOOK_FIXTURE_STATUS|'

if ($status.receiptCount -gt 1 -or
    ($status.receiptCount -eq 1 -and -not $status.receiptValid)) {
    throw 'Existing fixture receipt or post-state is inconsistent.'
}
$sourceReady = $status.contentValid -and
    $status.contentDefinitionCount -eq 24 -and
    $status.identityReady -and $status.petReady -and
    $status.petStatsReady -and $status.bagReady -and
    $status.sealedLinksReady -and $status.previewsReady
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
    CurrentInventoryRevision = $status.inventoryRevision
    CurrentPetExperience = $status.petExperience
    TargetPetExperience = 1507597955L
    CurrentPetRevision = $status.petRevision
    CurrentBagRows = $status.bagRows
    CurrentBagUnits = $status.bagUnits
    TargetBagRows = 25
    TargetBagUnits = 123
    PublishedItemRevision = $status.publishedItemRevision
    ContentDefinitionCount = $status.contentDefinitionCount
    ContentValid = $status.contentValid
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
    throw 'Content, source rows, or offline guard is not ready.'
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environment $redisContainer
}
if (-not $PSCmdlet.ShouldProcess(
        "isolated-development $Database character 2 / pet 1",
        'Clear kit bag, grant the exact rebirth kit, and add 1.5b pet EXP')) {
    return
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environment $redisContainer
}

$result = Invoke-FixturePsql `
    (Get-PetRebirthSkillBookApplySql $operationHex $requestHashHex) `
    'PET_REBIRTH_SKILLBOOK_FIXTURE_RESULT|'
$result | Add-Member NoteProperty OperationIdSha256 `
    $operationHex.ToUpperInvariant()
$result | Add-Member NoteProperty RequestHashSha256 `
    $requestHashHex.ToUpperInvariant()
$result
