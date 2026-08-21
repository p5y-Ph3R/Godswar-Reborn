[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [ValidatePattern('^godswar(?:_pet_unseal_vitals_[a-f0-9]{10})?$')]
    [string]$Database = 'godswar',

    [switch]$DisposableTest
)

# One-time, exact-state repair for isolated-development account 13,
# character 2, and pet 1. Status is read-only. Live Apply requires every
# server/client/lease fence to be offline and appends an immutable receipt.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-tempest-openworld-01'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$operationText =
    'localdev|pet-unseal-vitals-energy|account:13|character:2|pet:1|v1'
$requestText = $operationText +
    '|hp:29350->134341|mp:1287->6047|vitals:9895->9896' +
    '|energy:31->100|pet-revision:1413->1414'

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetUnsealVitalsEnergy.Sql.Common.ps1')
. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetUnsealVitalsEnergy.Sql.Status.ps1')
. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetUnsealVitalsEnergy.Sql.Apply.ps1')

if ($DisposableTest) {
    if ($Database -notmatch '^godswar_pet_unseal_vitals_[a-f0-9]{10}$') {
        throw 'Disposable tests require the pet-unseal-vitals DB prefix.'
    }
}
elseif ($Database -ne 'godswar') {
    throw 'The real repair can target only the godswar database.'
}

function Invoke-PetUnsealVitalsEnergyPsql(
    [string]$Sql,
    [string]$Marker
) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d $Database 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { $_.ToString() })
    if ($exitCode -ne 0) {
        throw "Pet unseal vitals/energy repair failed and rolled back:`n$($lines -join "`n")"
    }
    $receipt = $lines | Where-Object {
        $_.StartsWith($Marker, [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no pet unseal repair receipt.'
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
$status = Invoke-PetUnsealVitalsEnergyPsql `
    (Get-PetUnsealVitalsEnergyStatusSql $operationHex $requestHashHex) `
    'PET_UNSEAL_VITALS_ENERGY_STATUS|'

if ($status.receiptCount -gt 1 -or
    ($status.receiptCount -eq 1 -and -not $status.receiptValid)) {
    throw 'The permanent pet unseal repair receipt or target state is inconsistent.'
}
$sourceReady = [bool]$status.authorityValid -and
    [bool]$status.sourceReady -and [bool]$status.immutableAuditTrigger
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
    CurrentHp = $status.currentHp
    CurrentMp = $status.currentMp
    CalculatedMaximumHp = $status.calculatedMaximumHp
    CalculatedMaximumMp = $status.calculatedMaximumMp
    CurrentVitalsRevision = $status.vitalsRevision
    TargetVitalsRevision = 9896
    CurrentEnergy = $status.currentEnergy
    MaximumEnergy = $status.maximumEnergy
    CurrentPetRevision = $status.petRevision
    TargetPetRevision = 1414
    ItemContentRevision = $status.itemContentRevision
    GameplayContentRevision = $status.gameplayContentRevision
    PetSkillContentRevision = $status.petSkillContentRevision
    PetOwnerMergeContentRevision = $status.petOwnerMergeContentRevision
    AuthorityValid = $status.authorityValid
    SourceReady = $status.sourceReady
    PostReady = $status.postReady
    ReceiptAuditId = $status.receiptAuditId
    ServerRunning = $serverRunning
    OriginRunning = $originRunning
    RedisPlayerLoginKeyCount = $redisKeyCount
    ImmutableAuditTrigger = $status.immutableAuditTrigger
    OperationIdSha256 = $operationHex.ToUpperInvariant()
    RequestHashSha256 = $requestHashHex.ToUpperInvariant()
}
if ($Mode -eq 'Status' -or $state -eq 'Applied') { return $summary }
if ($state -ne 'Ready') {
    throw 'Exact content/state authority or every offline fence is not ready.'
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environment $redisContainer
}
if (-not $PSCmdlet.ShouldProcess(
        'isolated-development account 13 / character 2 / pet 1',
        'Restore exact calculated HP/MP and pet energy with permanent audit')) {
    return
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environment $redisContainer
}

$result = Invoke-PetUnsealVitalsEnergyPsql `
    (Get-PetUnsealVitalsEnergyApplySql $operationHex $requestHashHex) `
    'PET_UNSEAL_VITALS_ENERGY_RESULT|'
$verified = Invoke-PetUnsealVitalsEnergyPsql `
    (Get-PetUnsealVitalsEnergyStatusSql $operationHex $requestHashHex) `
    'PET_UNSEAL_VITALS_ENERGY_STATUS|'
if ($result.status -ne 'Applied' -or -not $result.changed -or
    $result.auditId -le 0 -or $verified.receiptCount -ne 1 -or
    -not $verified.receiptValid -or -not $verified.postReady -or
    $verified.receiptAuditId -ne $result.auditId -or
    $verified.currentHp -ne $verified.calculatedMaximumHp -or
    $verified.currentMp -ne $verified.calculatedMaximumMp -or
    $verified.currentEnergy -ne $verified.maximumEnergy) {
    throw 'The committed pet unseal repair failed exact read-back verification.'
}
$result
