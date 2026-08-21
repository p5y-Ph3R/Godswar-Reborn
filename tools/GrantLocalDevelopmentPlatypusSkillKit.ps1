[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [ValidatePattern('^godswar(?:_platypus_skill_kit_[a-f0-9]{10})?$')]
    [string]$Database = 'godswar',

    [switch]$DisposableTest
)

# One-time preserve-only fixture for account 13 / character 2. It appends one
# Platypus Magic Jade and Focus books I-VI to slots 25-31. Status is read-only;
# Apply requires the isolated server, Origin, and targeted Redis leases offline.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-tempest-openworld-01'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$itemIds = @(11080, 10530, 10531, 10532, 10533, 10534, 10535)
$slots = @(25, 26, 27, 28, 29, 30, 31)
$operationText =
    'localdev|platypus-skill-kit|account:13|character:2|v1'
$requestText = $operationText +
    '|source-inv:721|target-inv:722|preserve-slot:10104x94@24' +
    '|items:11080@25,10530@26,10531@27,10532@28,10533@29,' +
    '10534@30,10535@31|quantity:1|item-release:1851FC6E'

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
. (Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentPlatypusSkillKit.Sql.Common.ps1')
. (Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentPlatypusSkillKit.Sql.Status.ps1')
. (Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentPlatypusSkillKit.Sql.Apply.ps1')

if ($DisposableTest) {
    if ($Database -notmatch
        '^godswar_platypus_skill_kit_[a-f0-9]{10}$') {
        throw 'Disposable tests require the platypus-skill-kit DB prefix.'
    }
}
elseif ($Database -ne 'godswar') {
    throw 'The real fixture can target only the godswar database.'
}

function Invoke-PlatypusSkillKitPsql(
    [string]$Sql,
    [string]$Marker
) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d $Database 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { $_.ToString() })
    if ($exitCode -ne 0) {
        throw "Platypus skill-kit fixture failed and rolled back:`n$($lines -join "`n")"
    }
    $receipt = $lines | Where-Object {
        $_.StartsWith($Marker, [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no Platypus skill-kit receipt.'
    }
    $receipt.Substring($Marker.Length) | ConvertFrom-Json
}

function Test-PlatypusSkillActivationPolicySource {
    $path = Join-Path $PSScriptRoot `
        '..\src\Godswar.Server\State\PetSkillBookActivationPolicy.cs'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    $source = Get-Content -Raw -LiteralPath $path
    $expected = @(
        '[10530] = (4600, 1)', '[10531] = (4604, 2)',
        '[10532] = (4608, 3)', '[10533] = (4612, 4)',
        '[10534] = (4616, 5)', '[10535] = (4620, 6)'
    )
    foreach ($entry in $expected) {
        if ($source.IndexOf($entry, [StringComparison]::Ordinal) -lt 0) {
            return $false
        }
    }
    return $true
}

function Assert-PlatypusSkillKitOffline($Environment) {
    $serverName = $Environment.Server.Name.TrimStart('/')
    $currentServer = Get-RepairContainer $serverName
    Assert-RepairContainer $currentServer $serverName ''
    if ($currentServer.State.Running) {
        throw "Stop '$serverName' cleanly before granting the skill kit."
    }
    if (Test-OriginRunning) {
        throw 'Close Origin.exe before granting the skill kit.'
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
$activationPolicyReviewed = Test-PlatypusSkillActivationPolicySource
$operationHex = Get-RepairSha256Hex $operationText
$requestHashHex = Get-RepairSha256Hex $requestText
$status = Invoke-PlatypusSkillKitPsql `
    (Get-PlatypusSkillKitStatusSql $operationHex $requestHashHex) `
    'PLATYPUS_SKILL_KIT_STATUS|'

if ($status.receiptCount -gt 1 -or
    ($status.receiptCount -eq 1 -and -not $status.receiptValid)) {
    throw 'The permanent grant receipt or its item-audit chain is inconsistent.'
}
$sourceReady = $status.contentValid -and $activationPolicyReviewed -and
    $status.identityReady -and $status.sourceReady
$offline = -not $serverRunning -and -not $originRunning -and
    $redisKeyCount -eq 0
$state = if ($status.receiptCount -eq 1) { 'Applied' }
    elseif (-not $sourceReady) { 'Refused' }
    elseif ($DisposableTest -or $offline) { 'Ready' }
    else { 'AwaitingOffline' }
$latestPresence = [pscustomobject]@{
    CommandInboxId = 9031
    CommandAuditId = 9050
    Operation = 'Take'
    WirePresenceOperation = 1
    PetId = 1
    ResultPetRevision = 1208
    IsCarried = $true
    IsSummoned = $true
    CompletedAtUtc = '2026-08-13T13:31:50.300041Z'
}
$summary = [pscustomobject]@{
    Status = $state
    Database = $Database
    AccountId = 13
    CharacterId = 2
    ItemIds = $itemIds
    TargetSlots = $slots
    Quantities = @(1, 1, 1, 1, 1, 1, 1)
    CurrentInventoryRevision = $status.inventoryRevision
    TargetInventoryRevision = 722
    CurrentBagRows = $status.bagRows
    CurrentBagUnits = $status.bagUnits
    PreservedSourceItem = '10104 x94 @ bag slot 24'
    PreservedItemsSha256 = $status.preservedItemsSha256
    MainPet = $status.mainPet
    LatestDurablePresence = $latestPresence
    PublishedItemRevision = $status.itemRevision
    PublishedLearnedSkillRevision = $status.learnedSkillRevision
    PublishedPetRevision = $status.petRevision
    ItemContentValid = $status.itemContentValid
    ActivationContentValid = $status.activationContentValid
    ActivationPolicyReviewed = $activationPolicyReviewed
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
    Assert-PlatypusSkillKitOffline $environment
}
if (-not $PSCmdlet.ShouldProcess(
        'isolated-development character 2 / bag slots 25-31',
        'Append exactly seven Platypus items and permanent audits')) {
    return
}
if (-not $DisposableTest) {
    Assert-PlatypusSkillKitOffline $environment
}

$result = Invoke-PlatypusSkillKitPsql `
    (Get-PlatypusSkillKitApplySql $operationHex $requestHashHex) `
    'PLATYPUS_SKILL_KIT_RESULT|'
$verified = Invoke-PlatypusSkillKitPsql `
    (Get-PlatypusSkillKitStatusSql $operationHex $requestHashHex) `
    'PLATYPUS_SKILL_KIT_STATUS|'
if ($result.status -ne 'Applied' -or -not $result.changed -or
    $result.auditId -le 0 -or $verified.receiptCount -ne 1 -or
    -not $verified.receiptValid -or -not $verified.postReady -or
    $verified.receiptAuditId -ne $result.auditId) {
    throw 'The committed skill-kit receipt failed read-back verification.'
}
$result
