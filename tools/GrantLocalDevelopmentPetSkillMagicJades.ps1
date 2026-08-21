[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [ValidatePattern('^godswar(?:_pet_skill_jades_[a-f0-9]{8,12})?$')]
    [string]$Database = 'godswar',

    [switch]$DisposableTest
)

# Appends exactly one Cretan Bull, Totoro, King Lion, and Kratortle Magic
# Jade to the isolated rebirth-test bag. It never clears or moves an item.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-tempest-openworld-01'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$operationText =
    'localdev|pet-skill-magic-jades|account:13|character:2|v1'
$requestText = $operationText +
    '|source-inv:691|target-inv:692|source-bag:rebirth-skillbooks-v2' +
    '|items:11074@25,11078@26,11086@27,11089@28|quantity:1'

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
. (Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentPetSkillMagicJades.Sql.ps1')

if ($DisposableTest) {
    if ($Database -notmatch '^godswar_pet_skill_jades_[a-f0-9]{8,12}$') {
        throw 'Disposable tests require the pet-skill-jades DB prefix.'
    }
}
elseif ($Database -ne 'godswar') {
    throw 'The real grant can target only the godswar database.'
}

function Invoke-PetSkillJadePsql([string]$Sql, [string]$Marker) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d $Database 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { $_.ToString() })
    if ($exitCode -ne 0) {
        throw "Pet-skill Magic Jade grant failed and rolled back:`n$($lines -join "`n")"
    }
    $receipt = $lines | Where-Object {
        $_.StartsWith($Marker, [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no Magic Jade grant receipt.'
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
$status = Invoke-PetSkillJadePsql `
    (Get-PetSkillMagicJadeStatusSql $operationHex $requestHashHex) `
    'PET_SKILL_MAGIC_JADE_GRANT_STATUS|'

if ($status.receiptCount -gt 1 -or
    ($status.receiptCount -eq 1 -and -not $status.receiptValid)) {
    throw 'Existing Magic Jade grant receipt or post-state is inconsistent.'
}
$sourceReady = $status.contentValid -and $status.identityReady -and
    $status.sourceBagReady
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
    CurrentInventoryRevision = $status.inventoryRevision
    TargetInventoryRevision = 692
    CurrentBagRows = $status.bagRows
    TargetBagRows = 29
    ExistingJadeRows = $status.jadeRows
    ItemIds = @(11074, 11078, 11086, 11089)
    TargetSlots = @(25, 26, 27, 28)
    PublishedItemRevision = $status.itemRevision
    PublishedPetRevision = $status.petRevision
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
    throw 'Content, exact source bag, or offline guard is not ready.'
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environment $redisContainer
}
if (-not $PSCmdlet.ShouldProcess(
        'isolated-development godswar character 2 slots 25-28',
        'Append the four pet-skill Magic Jades and permanent audits')) {
    return
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environment $redisContainer
}

$result = Invoke-PetSkillJadePsql `
    (Get-PetSkillMagicJadeApplySql $operationHex $requestHashHex) `
    'PET_SKILL_MAGIC_JADE_GRANT_RESULT|'
$result | Add-Member NoteProperty OperationIdSha256 `
    $operationHex.ToUpperInvariant()
$result | Add-Member NoteProperty RequestHashSha256 `
    $requestHashHex.ToUpperInvariant()
$result
