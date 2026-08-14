[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$BackupRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$originalSha256 =
    'DD9E8A44282CBE1BF1B34293F8B1DE8F8E6C7A5DF3F5FE958F4F1243670A1BBD'
$legacyPatchedSha256 =
    'AA889B808D7AC6703BF0629116C28708357ACCD96066D9EA4CFB8394E08FE604'
$growthPatchedSha256 =
    '26EC13B594B6EEB25B8D7E9B91E95CAD33193333E2FDE10CD2D041F772C0E1E9'
$previousPatchedSha256 =
    '8DAF1D1DC6DDAC9806066F373F2497686D6C70F74C89035EFEB19ACED94633A9'
$topRowPatchedSha256 =
    '349D2CEE1D60743F475E331AF0883306DD28E7B7749A9C673234BE257BB60BA0'
$bottomPatchedSha256 =
    '9B031D51584ED3DB1A29D3908D9BDD94BCF7EF83BCD49820E070075C83B6DEC8'
$comparisonOrdinalPatchedSha256 =
    'BBA150C665CE0B9010E471699CE302BDFF4A5593B0924827671561611A4240B9'
$patchedSha256 =
    '219B283303ACDA105C19182DC7F5D3CB8284FB6D186F60BE0FA17A105C1AE1C2'
$supportedLayoutSha256 =
    'A92E7154A2D52AD152ECE8A498CC20E87F312A752E4A801437D0009BF49FF260'
$relativePath = 'UI\XML\NpcFun\NpcFunPett.lua'
$layoutRelativePath = 'UI\XML\NpcFun.xml'
$locales = @('en_us', 'zh_cn')
$encoding = [Text.UTF8Encoding]::new($true)
$okX = 440
$okY = 240
$cancelX = 515
$cancelY = 240
$phoenixResetX = 25
$phoenixResetY = 240

. (Join-Path $PSScriptRoot `
    'client_patch_helpers\PetGrowthResetDialog.Resources.ps1')
. (Join-Path $PSScriptRoot `
    'client_patch_helpers\PetGrowthResetDialog.Patcher.ps1')

$resolvedRoot = (Resolve-Path -LiteralPath $ClientRoot).Path
$supportedDialogueHashes = @(
    $originalSha256,
    $legacyPatchedSha256,
    $growthPatchedSha256,
    $previousPatchedSha256,
    $topRowPatchedSha256,
    $bottomPatchedSha256,
    $comparisonOrdinalPatchedSha256,
    $patchedSha256)
$targets = foreach ($locale in $locales) {
    $path = Join-Path $resolvedRoot "Localization\$locale\$relativePath"
    $layoutPath = Join-Path $resolvedRoot (
        "Localization\$locale\$layoutRelativePath")
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Pet Growth dialogue file was not found: $path"
    }
    if (-not (Test-Path -LiteralPath $layoutPath -PathType Leaf)) {
        throw "Pet Growth dialogue layout was not found: $layoutPath"
    }
    $hash = Get-FileSha256 $path
    if ($hash -notin $supportedDialogueHashes) {
        throw "Unsupported $locale Pet Growth dialogue SHA-256: $hash"
    }
    $state = switch ($hash) {
        $patchedSha256 { 'Patched'; break }
        $comparisonOrdinalPatchedSha256 { 'OrdinalPatch'; break }
        $bottomPatchedSha256 { 'BottomPatch'; break }
        $topRowPatchedSha256 { 'TopRowPatch'; break }
        $previousPatchedSha256 { 'PreviousPatch'; break }
        $growthPatchedSha256 { 'GrowthPatch'; break }
        $legacyPatchedSha256 { 'LegacyPatch'; break }
        default { 'Ready' }
    }
    [pscustomobject]@{
        Locale = $locale
        Path = $path
        Hash = $hash
        LayoutPath = $layoutPath
        LayoutHash = Assert-SupportedLayout $layoutPath $locale
        State = $state
    }
}

if (@($targets.Hash | Sort-Object -Unique).Count -ne 1) {
    throw 'Pet Growth dialogue locales are in mixed patch states.'
}
if ($Mode -eq 'Status') {
    $targets | Select-Object Locale, Path, LayoutPath, @{
        Name = 'Status'
        Expression = { $_.State }
    }, Hash, LayoutHash
    return
}

Assert-ClientClosed $resolvedRoot
$wantPatched = $Mode -eq 'Apply'
$alreadyDesired = if ($wantPatched) {
    $targets[0].Hash -eq $patchedSha256
} else {
    $targets[0].Hash -eq $originalSha256
}
if ($alreadyDesired) {
    $targets | Select-Object Locale, Path, @{
        Name = 'Status'
        Expression = {
            if ($wantPatched) { 'Already patched' }
            else { 'Already reverted' }
        }
    }, Hash
    return
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path $PSScriptRoot '..\backups'
}
$backupDirectory = Join-Path $BackupRoot (
    'pet-growth-dialog-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null

$staged = @()
try {
    foreach ($target in $targets) {
        $source = [IO.File]::ReadAllText($target.Path, $encoding)
        $newline = if ($source.Contains("`r`n")) { "`r`n" } else { "`n" }
        $helperAnchor = $helperAnchorLines -join $newline
        $comparisonHelper = $comparisonHelperLines -join $newline
        $originalBlock = $originalBlockLines -join $newline
        $legacyBlock = $legacyPatchedBlockLines -join $newline
        $topRowBlock = $topRowPatchedBlockLines -join $newline
        $bottomBlock = $bottomPatchedBlockLines -join $newline
        $patchedBlock = $patchedBlockLines -join $newline
        $basicOriginal = $basicOriginalBlockLines -join $newline
        $basicPrevious = $basicPreviousPatchedBlockLines -join $newline
        $basicPatched = $basicPatchedBlockLines -join $newline

        $helperFrom = if ($target.Hash -eq $patchedSha256) {
            $comparisonHelper
        } elseif ($target.Hash -eq $comparisonOrdinalPatchedSha256) {
            $comparisonOrdinalHelperLines -join $newline
        } else {
            $helperAnchor
        }
        $growthFrom = if ($target.Hash -eq $originalSha256) {
            $originalBlock
        } elseif ($target.Hash -eq $legacyPatchedSha256) {
            $legacyBlock
        } elseif ($target.Hash -eq $bottomPatchedSha256) {
            $bottomBlock
        } elseif ($target.Hash -in @(
                $comparisonOrdinalPatchedSha256,
                $patchedSha256)) {
            $patchedBlock
        } else {
            $topRowBlock
        }
        $basicFrom = if ($target.Hash -eq $previousPatchedSha256) {
            $basicPrevious
        } elseif ($target.Hash -in @(
                $topRowPatchedSha256,
                $bottomPatchedSha256,
                $comparisonOrdinalPatchedSha256,
                $patchedSha256)) {
            $basicPatched
        } else {
            $basicOriginal
        }
        $helperTo = if ($wantPatched) {
            $comparisonHelper
        } else {
            $helperAnchor
        }
        $growthTo = if ($wantPatched) { $patchedBlock } else { $originalBlock }
        $basicTo = if ($wantPatched) { $basicPatched } else { $basicOriginal }

        foreach ($guard in @(
                @{ Name = 'helper'; Value = $helperFrom },
                @{ Name = 'Growth'; Value = $growthFrom },
                @{ Name = 'Basic/Savvy'; Value = $basicFrom })) {
            if ([regex]::Matches(
                    $source,
                    [regex]::Escape($guard.Value)).Count -ne 1) {
                throw "$($target.Locale) dialogue lacks guarded $($guard.Name) block."
            }
        }

        $candidateText = $source.Replace($helperFrom, $helperTo)
        $candidateText = $candidateText.Replace($growthFrom, $growthTo)
        $candidateText = $candidateText.Replace($basicFrom, $basicTo)
        $candidate = Get-Utf8BomBytes $candidateText
        $expectedHash = if ($wantPatched) {
            $patchedSha256
        } else {
            $originalSha256
        }
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $candidateHash = (($algorithm.ComputeHash($candidate) |
                ForEach-Object { $_.ToString('X2') }) -join '')
        }
        finally {
            $algorithm.Dispose()
        }
        if ($candidateHash -ne $expectedHash) {
            throw "$($target.Locale) staged dialogue failed exact hash verification."
        }

        $backup = Join-Path $backupDirectory "$($target.Locale)-NpcFunPett.lua"
        Copy-Item -LiteralPath $target.Path -Destination $backup
        if ((Get-FileSha256 $backup) -ne $target.Hash) {
            throw "$($target.Locale) backup verification failed."
        }
        $stage = "$($target.Path).$([guid]::NewGuid().ToString('N')).stage"
        [IO.File]::WriteAllBytes($stage, $candidate)
        if ((Get-FileSha256 $stage) -ne $expectedHash) {
            throw "$($target.Locale) written stage verification failed."
        }
        $staged += [pscustomobject]@{
            Target = $target
            Stage = $stage
            Backup = $backup
            ExpectedHash = $expectedHash
        }
    }

    foreach ($entry in $staged) {
        Assert-ClientClosed $resolvedRoot
        if ((Get-FileSha256 $entry.Target.LayoutPath) -ne
            $entry.Target.LayoutHash) {
            throw "$($entry.Target.Locale) layout changed while staging."
        }
        if ((Get-FileSha256 $entry.Target.Path) -ne $entry.Target.Hash) {
            throw "$($entry.Target.Locale) dialogue changed while staging."
        }
        [IO.File]::Copy($entry.Stage, $entry.Target.Path, $true)
        if ((Get-FileSha256 $entry.Target.Path) -ne $entry.ExpectedHash) {
            throw "$($entry.Target.Locale) installed dialogue verification failed."
        }
    }
}
catch {
    $installError = $_
    $rollbackFailures = @()
    foreach ($entry in $staged) {
        try {
            if (-not (Test-Path -LiteralPath $entry.Backup -PathType Leaf)) {
                throw "$($entry.Target.Locale) backup is missing."
            }
            [IO.File]::Copy($entry.Backup, $entry.Target.Path, $true)
            if ((Get-FileSha256 $entry.Target.Path) -ne $entry.Target.Hash) {
                throw "$($entry.Target.Locale) restored dialogue failed hash verification."
            }
        }
        catch {
            $rollbackFailures += $_.Exception.Message
        }
    }
    if ($rollbackFailures.Count -gt 0) {
        throw "Dialogue patch failed: $($installError.Exception.Message) " +
            "Rollback failed: $($rollbackFailures -join '; ')"
    }
    throw $installError
}
finally {
    foreach ($entry in $staged) {
        Remove-Item -LiteralPath $entry.Stage -Force -ErrorAction SilentlyContinue
    }
}

$targets | ForEach-Object {
    [pscustomobject]@{
        Locale = $_.Locale
        Path = $_.Path
        Status = if ($wantPatched) { 'Patched' } else { 'Reverted' }
        Hash = Get-FileSha256 $_.Path
        LayoutHash = Get-FileSha256 $_.LayoutPath
        BackupDirectory = $backupDirectory
    }
}
