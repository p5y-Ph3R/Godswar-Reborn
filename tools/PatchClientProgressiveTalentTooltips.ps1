[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

    [ValidateSet('Check', 'Apply', 'Rollback')]
    [string]$Mode = 'Check',

    [string]$BackupRoot,

    [string]$ReceiptPath,

    [scriptblock]$InternalTestBeforeCommit,

    [scriptblock]$InternalTestAfterWrite
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot (
        'client_patch_helpers\ProgressiveTalentTooltips.Transaction.ps1'))
. (Join-Path $PSScriptRoot (
        'client_patch_helpers\ProgressiveTalentTooltips.Binary.ps1'))
. (Join-Path $PSScriptRoot (
        'client_patch_helpers\ProgressiveTalentTooltips.SkillData.ps1'))

$clientRootPath = [IO.Path]::GetFullPath($ClientRoot)
Assert-ProgressiveTalentClientRootPolicy $clientRootPath
if ($Mode -eq 'Rollback') {
    if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
        throw 'Rollback requires -ReceiptPath from the successful Apply result.'
    }
    $result = Invoke-ProgressiveTalentReceiptRollback $clientRootPath (
        $ReceiptPath) $InternalTestAfterWrite
    $result
    return
}
if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
    throw '-ReceiptPath is valid only with -Mode Rollback.'
}

$definitions = @(
    [pscustomobject]@{
        Label = 'Origin.exe'
        RelativePath = 'Origin.exe'
        BackupName = 'Origin.exe'
        Path = Join-Path $clientRootPath 'Origin.exe'
        Locale = $null
    },
    [pscustomobject]@{
        Label = 'en_us Skill.ini'
        RelativePath = 'Localization\en_us\Settings\Sys\Skill.ini'
        BackupName = 'en_us-Skill.ini'
        Path = Join-Path $clientRootPath (
            'Localization\en_us\Settings\Sys\Skill.ini')
        Locale = 'en_us'
    },
    [pscustomobject]@{
        Label = 'zh_cn Skill.ini'
        RelativePath = 'Localization\zh_cn\Settings\Sys\Skill.ini'
        BackupName = 'zh_cn-Skill.ini'
        Path = Join-Path $clientRootPath (
            'Localization\zh_cn\Settings\Sys\Skill.ini')
        Locale = 'zh_cn'
    }
)
foreach ($definition in $definitions) {
    if (-not (Test-Path -LiteralPath $definition.Path -PathType Leaf)) {
        throw "Required client file is missing: $($definition.Path)"
    }
}

$binaryProfile = Get-ProgressiveTalentBinaryProfile
[byte[]]$origin = [IO.File]::ReadAllBytes($definitions[0].Path)
$binaryState = Get-ProgressiveTalentBinaryState (
    $origin) $binaryProfile -AuditXrefs
$skillStates = @{}
$skillBytes = @{}
$skillProfiles = @{}
foreach ($definition in @($definitions | Where-Object { $null -ne $_.Locale })) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($definition.Path)
    $profile = Get-ProgressiveTalentSkillProfile $definition.Locale
    $skillBytes[$definition.Locale] = $bytes
    $skillProfiles[$definition.Locale] = $profile
    $skillStates[$definition.Locale] =
        Get-ProgressiveTalentSkillState $bytes $profile
}
$allStock = @($skillStates.Values | Where-Object {
        $_.State -ne 'Stock' }).Count -eq 0
if ($binaryState.State -eq 'Patched' -and -not $allStock) {
    throw 'Client has a mixed native-tooltip/resource state; no writes were made.'
}
$combinedState = if ($binaryState.State -eq 'Patched') {
    'Patched'
} else { 'Ready' }

if ($Mode -eq 'Check') {
    [pscustomobject]@{
        Status = $combinedState
        OriginSha256 = $binaryState.Sha256
        BinaryState = $binaryState.State
        CaveInboundRelativeXrefs = $binaryState.CaveInboundRelativeXrefs
        EnUsSkillState = $skillStates['en_us'].State
        ZhCnSkillState = $skillStates['zh_cn'].State
        EnUsTalentCount = $skillStates['en_us'].TalentCount
        ZhCnTalentCount = $skillStates['zh_cn'].TalentCount
        CurrentRankDisplay = 'E(rank)'
        NextRankDisplay = 'E(min(rank + 1, 100))'
        SkillScalars = if ($allStock) { 'stock' } else {
            'reviewed legacy Champion tooltip vector'
        }
    }
    return
}

if ($combinedState -eq 'Patched') {
    [pscustomobject]@{
        Status = 'Already patched'
        Changed = $false
        OriginSha256 = $binaryState.Sha256
    }
    return
}
if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'backups'
}

[byte[]]$patchedOrigin = Convert-ProgressiveTalentBinary (
    $origin) $binaryProfile 'Patched'
$snapshots = @(
    New-ProgressiveTalentSnapshot $definitions[0].Label (
        $definitions[0].RelativePath) $definitions[0].BackupName (
        $definitions[0].Path) $origin $patchedOrigin
)
foreach ($definition in @($definitions | Where-Object { $null -ne $_.Locale })) {
    [byte[]]$before = $skillBytes[$definition.Locale]
    [byte[]]$after = Convert-ProgressiveTalentSkillBytes $before (
        $skillProfiles[$definition.Locale]) 'Stock'
    $snapshots += New-ProgressiveTalentSnapshot $definition.Label (
        $definition.RelativePath) $definition.BackupName $definition.Path (
        $before) $after
}

$result = Invoke-ProgressiveTalentApplyTransaction $clientRootPath (
    $BackupRoot) $snapshots $InternalTestBeforeCommit $InternalTestAfterWrite
$result | Add-Member -NotePropertyName OriginSha256 -NotePropertyValue (
    $binaryProfile.PatchedSha256)
$result | Add-Member -NotePropertyName SkillScalars -NotePropertyValue 'stock'
$result
