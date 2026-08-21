[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientProgressiveTalentTooltips.ps1'
. (Join-Path $PSScriptRoot (
        'client_patch_helpers\ProgressiveTalentTooltips.Transaction.ps1'))
. (Join-Path $PSScriptRoot (
        'client_patch_helpers\ProgressiveTalentTooltips.Binary.ps1'))
. (Join-Path $PSScriptRoot (
        'client_patch_helpers\ProgressiveTalentTooltips.SkillData.ps1'))
. (Join-Path $PSScriptRoot (
        'client_patch_helpers\ProgressiveTalentTooltips.TestSupport.ps1'))

$fixture = [IO.Path]::GetFullPath($FixtureRoot)
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repositoryRoot (
    'artifacts\progressive-talent-tooltip-test-' +
    [guid]::NewGuid().ToString('N'))
$client = Join-Path $testRoot 'client'
$backups = Join-Path $testRoot 'backups'
$files = $null

function Get-Metadata([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    return [pscustomobject]@{
        Length = $item.Length
        CreationTicks = $item.CreationTimeUtc.Ticks
        WriteTicks = $item.LastWriteTimeUtc.Ticks
        Sddl = (Get-Acl -LiteralPath $Path).Sddl
    }
}

try {
    foreach ($relative in @(
            'Origin.exe',
            'Localization\en_us\Settings\Sys\Skill.ini',
            'Localization\zh_cn\Settings\Sys\Skill.ini')) {
        if (-not (Test-Path -LiteralPath (Join-Path $fixture $relative) `
                -PathType Leaf)) {
            throw "Fixture is missing $relative under $fixture."
        }
    }
    $fixtureHashes = @{}
    foreach ($relative in @(
            'Origin.exe',
            'Localization\en_us\Settings\Sys\Skill.ini',
            'Localization\zh_cn\Settings\Sys\Skill.ini')) {
        $fixtureHashes[$relative] = Get-ProgressiveTalentFileSha256 (
            Join-Path $fixture $relative)
    }

    New-ProgressiveTalentTestClient $fixture $client
    $files = Get-ProgressiveTalentTestFileMap $client
    $source = Get-ProgressiveTalentTestBytes $files
    $ready = & $patcher -ClientRoot $client -Mode Check
    Assert-ProgressiveTalentEqual $ready.Status 'Ready' 'source status'
    Assert-ProgressiveTalentEqual $ready.BinaryState 'Original' (
        'source binary state')
    Assert-ProgressiveTalentEqual $ready.EnUsSkillState 'ChampionTooltip' (
        'source English resource state')
    Assert-ProgressiveTalentEqual $ready.ZhCnSkillState 'Stock' (
        'source Chinese resource state')
    Assert-ProgressiveTalentEqual $ready.CaveInboundRelativeXrefs 0 (
        'source cave xrefs')
    Assert-ProgressiveTalentTrue (-not (Test-Path -LiteralPath $backups)) (
        'Check creates no backup directory')

    Assert-ProgressiveTalentThrows {
        & $patcher -ClientRoot 'C:\Godswar Origin B20H' -Mode Check | Out-Null
    } 'protected B20H' 'hard B20H client fence'
    $clientAlias = Join-Path $testRoot 'client-reparse-alias'
    New-Item -ItemType Junction -Path $clientAlias -Target $client |
        Out-Null
    try {
        Assert-ProgressiveTalentThrows {
            & $patcher -ClientRoot $clientAlias -Mode Check | Out-Null
        } 'reparse point' 'client-root reparse alias refusal'
    }
    finally {
        if (Test-Path -LiteralPath $clientAlias) {
            [IO.Directory]::Delete($clientAlias)
        }
    }
    Assert-ProgressiveTalentThrows {
        & $patcher -ClientRoot $client -Mode Apply -BackupRoot $client |
            Out-Null
    } 'outside the client' 'exact client-root backup refusal'
    Assert-ProgressiveTalentThrows {
        & $patcher -ClientRoot $client -Mode Apply -BackupRoot (
            Join-Path $client 'backups') | Out-Null
    } 'outside the client' 'client-descendant backup refusal'
    Assert-ProgressiveTalentThrows {
        Assert-ProgressiveTalentClientClosed {
            [pscustomobject]@{ ProcessName = 'Launch'; Id = 731 }
        }
    } 'Close Origin and its launcher' 'launcher process gate'
    Assert-ProgressiveTalentThrows {
        Assert-ProgressiveTalentClientClosed {
            [pscustomobject]@{ ProcessName = 'Origin'; Id = 732 }
        }
    } 'Close Origin and its launcher' 'Origin process gate'

    $binaryProfile = Get-ProgressiveTalentBinaryProfile
    $sourceBinary = Get-ProgressiveTalentBinaryState (
        $source.Origin) $binaryProfile -AuditXrefs
    Assert-ProgressiveTalentEqual $sourceBinary.Sha256 (
        $binaryProfile.SourceSha256) 'source Origin hash'
    Assert-ProgressiveTalentEqual @(
        Get-ProgressiveTalentAbsoluteRangeReferences $source.Origin (
            $binaryProfile.CaveVa) (
            $binaryProfile.CaveVa + $binaryProfile.CaveLength)).Count 0 (
        'source cave has no pointer-shaped references')

    [byte[]]$helper = $binaryProfile.PatchedCave[
        0..($binaryProfile.HelperLength - 1)]
    Assert-ProgressiveTalentTrue (
        Test-ProgressiveTalentBytes $helper 0 (
            Convert-ProgressiveTalentHexBytes (
                '0F B6 48 25 83 F9 64 83 D1 00 EB 04'))) (
        'next entry keeps CMP/ADC adjacent and jumps to common transform')
    Assert-ProgressiveTalentTrue (
        Test-ProgressiveTalentBytes $helper 12 (
            Convert-ProgressiveTalentHexBytes '0F B6 48 25')) (
        'current entry loads raw rank at offset 12')
    foreach ($raw in 0..255) {
        $current = Invoke-ProgressiveTalentHelperModel $helper 12 ([byte]$raw)
        $next = Invoke-ProgressiveTalentHelperModel $helper 0 ([byte]$raw)
        Assert-ProgressiveTalentEqual $current (
            Get-ProgressiveTalentExpectedRank $raw) "current rank byte $raw"
        Assert-ProgressiveTalentEqual $next (
            Get-ProgressiveTalentExpectedRank ([Math]::Min($raw + 1, 100))) (
            "next rank byte $raw")
    }

    $failureSeen = $false
    try {
        & $patcher -ClientRoot $client -Mode Apply -BackupRoot $backups `
            -InternalTestAfterWrite {
                param($label)
                if ($label -eq 'en_us Skill.ini') {
                    throw 'injected second-write failure'
                }
            } | Out-Null
    }
    catch {
        $failureSeen = $_.Exception.Message -like (
            '*injected second-write failure*')
    }
    Assert-ProgressiveTalentTrue $failureSeen (
        'injected multi-file failure is observable')
    Assert-ProgressiveTalentTestBytes $files $source (
        'failed Apply auto-rollback')
    $failedReceipt = Get-ChildItem -LiteralPath $backups -Recurse `
        -Filter receipt.json | Select-Object -First 1
    $failedManifest = Get-Content -LiteralPath $failedReceipt.FullName -Raw |
        ConvertFrom-Json
    Assert-ProgressiveTalentEqual $failedManifest.Outcome 'AutoRolledBack' (
        'failure receipt records verified automatic rollback')

    $zhBeforeMetadata = Get-Metadata $files.Zh
    $writtenLabels = [Collections.Generic.List[string]]::new()
    $applied = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups -InternalTestAfterWrite {
            param($label)
            $writtenLabels.Add($label)
        }
    Assert-ProgressiveTalentEqual $applied.Status 'Patched' 'Apply status'
    Assert-ProgressiveTalentTrue (
        Test-Path -LiteralPath $applied.Receipt -PathType Leaf) (
        'Apply returns an existing receipt')
    Assert-ProgressiveTalentEqual ($writtenLabels -join ',') (
        'Origin.exe,en_us Skill.ini') 'Apply writes only changed files'
    $patched = Get-ProgressiveTalentTestBytes $files
    $patchedStatus = & $patcher -ClientRoot $client -Mode Check
    Assert-ProgressiveTalentEqual $patchedStatus.Status 'Patched' (
        'patched status')
    Assert-ProgressiveTalentEqual $patchedStatus.CaveInboundRelativeXrefs 4 (
        'patched cave exact inbound xrefs')
    Assert-ProgressiveTalentEqual $patchedStatus.EnUsSkillState 'Stock' (
        'English installed stock state')
    Assert-ProgressiveTalentEqual $patchedStatus.ZhCnSkillState 'Stock' (
        'Chinese installed stock state')

    $differences = @(Get-ProgressiveTalentBinaryDifferences (
            $source.Origin) $patched.Origin)
    $allowed = @($binaryProfile.Hooks | ForEach-Object {
            [pscustomobject]@{ Offset = $_.Offset; Length = $_.Patched.Length }
        }) + @([pscustomobject]@{
            Offset = $binaryProfile.CaveOffset
            Length = $binaryProfile.CaveLength
        })
    Assert-ProgressiveTalentEqual $differences.Count 116 (
        'exact Origin changed-byte count')
    foreach ($offset in $differences) {
        Assert-ProgressiveTalentTrue (
            Test-ProgressiveTalentAllowedOffset $offset $allowed) (
            'Origin difference is inside an audited hook/cave range')
    }
    Assert-ProgressiveTalentTrue (
        Test-ProgressiveTalentBytes $patched.Origin (
            $binaryProfile.CaveOffset) $binaryProfile.PatchedCave) (
        'exact helper and zero cave tail installed')
    Assert-ProgressiveTalentEqual @(
        Get-ProgressiveTalentAbsoluteRangeReferences $patched.Origin (
            $binaryProfile.CaveVa) (
            $binaryProfile.CaveVa + $binaryProfile.CaveLength)).Count 0 (
        'patched cave has no absolute pointer-shaped references')

    $enProfile = Get-ProgressiveTalentSkillProfile 'en_us'
    $zhProfile = Get-ProgressiveTalentSkillProfile 'zh_cn'
    $enState = Get-ProgressiveTalentSkillState $patched.En $enProfile
    $zhState = Get-ProgressiveTalentSkillState $patched.Zh $zhProfile
    Assert-ProgressiveTalentEqual $enState.TalentCount 73 (
        'all 73 primary-locale talents are stock')
    Assert-ProgressiveTalentEqual $zhState.TalentCount 72 (
        'Chinese reviewed locale retains its 72 native sections')
    Assert-ProgressiveTalentTrue (
        Test-ProgressiveTalentSameBytes $source.Zh $patched.Zh) (
        'Chinese Skill.ini bytes are untouched')
    $zhAfterMetadata = Get-Metadata $files.Zh
    Assert-ProgressiveTalentEqual $zhAfterMetadata.Length (
        $zhBeforeMetadata.Length) 'Chinese file length unchanged'
    Assert-ProgressiveTalentEqual $zhAfterMetadata.CreationTicks (
        $zhBeforeMetadata.CreationTicks) 'Chinese creation time unchanged'
    Assert-ProgressiveTalentEqual $zhAfterMetadata.WriteTicks (
        $zhBeforeMetadata.WriteTicks) 'Chinese write time unchanged'
    Assert-ProgressiveTalentEqual $zhAfterMetadata.Sddl (
        $zhBeforeMetadata.Sddl) 'Chinese ACL/owner unchanged'

    $sourceEnText = Read-ProgressiveTalentSkillText $source.En 'source en_us'
    $patchedEnText = Read-ProgressiveTalentSkillText $patched.En 'patched en_us'
    $stockLines = Get-ProgressiveTalentStockLines
    foreach ($id in @($stockLines.Keys | ForEach-Object { [int]$_ })) {
        $beforeLine = (Get-ProgressiveTalentSectionEffect (
                $sourceEnText) $id).Value
        $afterLine = (Get-ProgressiveTalentSectionEffect (
                $patchedEnText) $id).Value
        Assert-ProgressiveTalentEqual $afterLine $stockLines[$id] (
            "talent $id exact stock scalar")
        if ($id -lt 50 -or $id -gt 68) {
            Assert-ProgressiveTalentEqual $afterLine $beforeLine (
                "talent $id non-Champion bytes remain semantically exact")
        }
    }

    $manifest = Get-Content -LiteralPath $applied.Receipt -Raw |
        ConvertFrom-Json
    Assert-ProgressiveTalentEqual $manifest.Outcome 'Applied' (
        'receipt completed state')
    Assert-ProgressiveTalentEqual @($manifest.Files).Count 3 (
        'receipt and backups cover all three target files')
    foreach ($record in @($manifest.Files)) {
        $backupPath = Join-Path (Split-Path -Parent $applied.Receipt) (
            [string]$record.BackupName)
        Assert-ProgressiveTalentEqual (
            Get-ProgressiveTalentFileSha256 $backupPath) (
            [string]$record.BeforeSha256) (
            "$($record.Label) backup hash")
    }
    $backupDirectoryCount = @(Get-ChildItem -LiteralPath $backups -Directory).Count
    $again = & $patcher -ClientRoot $client -Mode Apply -BackupRoot $backups
    Assert-ProgressiveTalentEqual $again.Status 'Already patched' (
        'idempotent Apply status')
    Assert-ProgressiveTalentEqual @(
        Get-ChildItem -LiteralPath $backups -Directory).Count (
        $backupDirectoryCount) 'idempotent Apply creates no backup'

    $tamperedPath = Join-Path (Split-Path -Parent $applied.Receipt) (
        'tampered-receipt.json')
    $tampered = Get-Content -LiteralPath $applied.Receipt -Raw |
        ConvertFrom-Json
    $tampered.Files[0].AfterSha256 = '0' * 64
    Write-ProgressiveTalentJsonAtomic $tamperedPath $tampered
    Assert-ProgressiveTalentThrows {
        & $patcher -ClientRoot $client -Mode Rollback `
            -ReceiptPath $tamperedPath | Out-Null
    } 'not pinned' 'tampered self-consistent receipt hash refusal'

    $rollbackFailed = $false
    try {
        & $patcher -ClientRoot $client -Mode Rollback `
            -ReceiptPath $applied.Receipt -InternalTestAfterWrite {
                param($label)
                if ($label -eq 'en_us Skill.ini') {
                    throw 'injected rollback failure'
                }
            } | Out-Null
    }
    catch {
        $rollbackFailed = $_.Exception.Message -like (
            '*injected rollback failure*')
    }
    Assert-ProgressiveTalentTrue $rollbackFailed (
        'injected Rollback failure is observable')
    Assert-ProgressiveTalentTestBytes $files $patched (
        'failed Rollback restores patched state')

    $rollbackLabels = [Collections.Generic.List[string]]::new()
    $rolled = & $patcher -ClientRoot $client -Mode Rollback `
        -ReceiptPath $applied.Receipt -InternalTestAfterWrite {
            param($label)
            $rollbackLabels.Add($label)
        }
    Assert-ProgressiveTalentEqual $rolled.Status 'Rolled back' (
        'Rollback status')
    Assert-ProgressiveTalentEqual ($rollbackLabels -join ',') (
        'Origin.exe,en_us Skill.ini') 'Rollback skips unchanged Chinese file'
    Assert-ProgressiveTalentTestBytes $files $source 'byte-exact Rollback'
    $rolledAgain = & $patcher -ClientRoot $client -Mode Rollback `
        -ReceiptPath $applied.Receipt
    Assert-ProgressiveTalentEqual $rolledAgain.Status 'Already rolled back' (
        'idempotent Rollback status')

    [byte[]]$partial = [IO.File]::ReadAllBytes($files.Origin)
    $partial[$binaryProfile.Hooks[0].Offset] = 0x90
    [IO.File]::WriteAllBytes($files.Origin, $partial)
    Assert-ProgressiveTalentThrows {
        & $patcher -ClientRoot $client -Mode Check | Out-Null
    } 'Unsupported or partial' 'partial binary state refusal'
    [IO.File]::WriteAllBytes($files.Origin, $source.Origin)

    [byte[]]$unknownSkill = [IO.File]::ReadAllBytes($files.En)
    $unknownSkill[100] = $unknownSkill[100] -bxor 1
    [IO.File]::WriteAllBytes($files.En, $unknownSkill)
    Assert-ProgressiveTalentThrows {
        & $patcher -ClientRoot $client -Mode Check | Out-Null
    } 'Unsupported en_us Skill.ini' 'foreign Skill.ini refusal'
    [IO.File]::WriteAllBytes($files.En, $source.En)

    foreach ($relative in $fixtureHashes.Keys) {
        Assert-ProgressiveTalentEqual (
            Get-ProgressiveTalentFileSha256 (Join-Path $fixture $relative)) (
            $fixtureHashes[$relative]) "source fixture unchanged: $relative"
    }
    foreach ($path in @(
            $patcher,
            (Join-Path $PSScriptRoot (
                'client_patch_helpers\ProgressiveTalentTooltips.Binary.ps1')),
            (Join-Path $PSScriptRoot (
                'client_patch_helpers\ProgressiveTalentTooltips.SkillData.ps1')),
            (Join-Path $PSScriptRoot (
                'client_patch_helpers\ProgressiveTalentTooltips.Transaction.ps1')),
            (Join-Path $PSScriptRoot (
                'client_patch_helpers\ProgressiveTalentTooltips.TestSupport.ps1')),
            $PSCommandPath)) {
        $item = Get-Item -LiteralPath $path
        Assert-ProgressiveTalentTrue ($item.Length -lt 20000) (
            "$($item.Name) is below 20KB")
        Assert-ProgressiveTalentTrue (@(Get-Content -LiteralPath $path).Count -lt
            600) "$($item.Name) is below 600 lines"
    }

    Write-Host (
        'Progressive talent tooltip patch checks passed: ' +
        "$script:ProgressiveTalentAssertions assertions.")
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        $resolvedTest = [IO.Path]::GetFullPath($testRoot)
        $resolvedArtifacts = [IO.Path]::GetFullPath((Join-Path (
                    $repositoryRoot) 'artifacts'))
        if (-not (Test-ProgressiveTalentPathWithin (
                    $resolvedTest) $resolvedArtifacts)) {
            throw 'Refusing to clean a test path outside repository artifacts.'
        }
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}
