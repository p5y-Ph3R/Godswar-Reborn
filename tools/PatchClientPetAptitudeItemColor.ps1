[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$BackupRoot = (Join-Path $PSScriptRoot '..\backups')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$gb2312 = [Text.Encoding]::GetEncoding(936)
$specifications = @(
    [pscustomobject]@{
        Locale = 'en_us'
        SourceHash =
            'E04DA2A7E55B7E2250ACD5DF52A83CADA42FA9B173297BE7F7A554FE133A7C00'
        TargetHash =
            '8202DBF6F83DE1B0916FC140AA93337414FF8DEC049AE5CBB7BAF2903806E91A'
    },
    [pscustomobject]@{
        Locale = 'zh_cn'
        SourceHash =
            '2DDC5FB8192DDC3ABF9493578ED8DA9A3034862FE6D5DD72874DF735A04FD3BC'
        TargetHash =
            '99C72FB3818A3C3AB5A1B5CFB0278A43F2339B37CCE7F1A6390FB05BECA625A9'
    }
)

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-ClientClosed([string]$Root) {
    $origin = [IO.Path]::GetFullPath((Join-Path $Root 'Origin.exe'))
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try {
            try { $path = $process.Path } catch { $path = $null }
            if ($path -and [string]::Equals(
                    [IO.Path]::GetFullPath($path), $origin,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Close Origin.exe before changing pet aptitude tooltips.'
            }
        }
        finally { $process.Dispose() }
    }
}

function Get-AptitudeRows([string]$Text, [string]$Locale) {
    try { [xml]$document = $Text }
    catch { throw "$Locale ItemColor.xml is not valid XML: $($_.Exception.Message)" }
    $rows = @($document.SelectNodes('/ItemColor/Equip/Pet/*'))
    if ($rows.Count -ne 20) {
        throw "$Locale ItemColor.xml must contain exactly 20 pet aptitude rows."
    }
    for ($level = 1; $level -le 20; $level++) {
        $row = @($document.SelectNodes(
                "/ItemColor/Equip/Pet/Aptitude$level"))
        if ($row.Count -ne 1 -or
            $row[0].GetAttribute('BaseLv') -cne [string]$level -or
            [string]::IsNullOrWhiteSpace($row[0].GetAttribute('BaseName')) -or
            [string]::IsNullOrWhiteSpace($row[0].GetAttribute('BaseColor'))) {
            throw "$Locale ItemColor.xml has an invalid aptitude row $level."
        }
    }
    return $rows
}

function Convert-AptitudeNames(
    [string]$Text,
    [object]$Specification,
    [bool]$ToTarget
) {
    $rows = Get-AptitudeRows $Text $Specification.Locale
    $current = @(7..10 | ForEach-Object {
            $rows[$_ - 1].GetAttribute('BaseName')
        })
    # Reorder existing localized strings instead of embedding non-ASCII text
    # in this Windows PowerShell script. The source order is
    # Smart/Zealous/Grumpy/Brave; the project order is
    # Grumpy/Brave/Zealous/Smart.
    $order = if ($ToTarget) { @(2, 3, 1, 0) } else { @(3, 2, 0, 1) }
    $desired = @($order | ForEach-Object { $current[$_] })
    $result = $Text
    for ($index = 0; $index -lt 4; $index++) {
        $level = 7 + $index
        $old = 'BaseLv="{0}" BaseName="{1}"' -f $level, $current[$index]
        $new = 'BaseLv="{0}" BaseName="{1}"' -f $level, $desired[$index]
        if ([regex]::Matches($result, [regex]::Escape($old)).Count -ne 1) {
            throw "$($Specification.Locale) aptitude $level is not uniquely guarded."
        }
        $result = $result.Replace($old, $new)
    }
    [void](Get-AptitudeRows $result $Specification.Locale)
    return $result
}

$root = [IO.Path]::GetFullPath($ClientRoot)
$states = foreach ($specification in $specifications) {
    $path = Join-Path $root (
        "Localization\$($specification.Locale)\Settings\Sys\ItemColor.xml")
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing client aptitude catalog: $path"
    }
    [byte[]]$bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 3 -or
        ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and
            $bytes[2] -eq 0xBF) -or
        ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or
        ($bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF)) {
        throw "$($specification.Locale) ItemColor.xml must be BOM-less GB2312."
    }
    $hash = Get-Sha256 $path
    $state = if ($hash -eq $specification.SourceHash) {
        'StockOrder'
    }
    elseif ($hash -eq $specification.TargetHash) {
        'ProjectOrder'
    }
    else {
        throw "Unsupported $($specification.Locale) ItemColor.xml SHA-256 $hash."
    }
    $text = [IO.File]::ReadAllText($path, $gb2312)
    [void](Get-AptitudeRows $text $specification.Locale)
    [pscustomobject]@{
        Specification = $specification
        Path = $path
        Text = $text
        Hash = $hash
        State = $state
    }
}

$distinctStates = @($states.State | Sort-Object -Unique)
if ($distinctStates.Count -ne 1) {
    throw 'Pet aptitude tooltip locales are in different states.'
}
$isPatched = $distinctStates[0] -eq 'ProjectOrder'
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) { 'Patched' } else { 'Ready to apply' }
        Mapping = '7 Grumpy; 8 Brave; 9 Zealous; 10 Smart'
        Locales = 'en_us, zh_cn'
    }
    return
}

Assert-ClientClosed $root
$wantPatched = $Mode -eq 'Apply'
if ($wantPatched -eq $isPatched) {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) { 'Already patched' } else { 'Already reverted' }
        Locales = 'en_us, zh_cn'
    }
    return
}

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-pet-aptitude-itemcolor-' + $Mode + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$stages = [Collections.Generic.List[object]]::new()
try {
    foreach ($state in $states) {
        $relative = "Localization\$($state.Specification.Locale)" +
            '\Settings\Sys\ItemColor.xml'
        $backup = Join-Path $backupDirectory $relative
        [IO.Directory]::CreateDirectory((Split-Path $backup -Parent)) |
            Out-Null
        Copy-Item -LiteralPath $state.Path -Destination $backup
        if ((Get-Sha256 $backup) -ne $state.Hash) {
            throw "Backup verification failed for $($state.Specification.Locale)."
        }
        $text = Convert-AptitudeNames $state.Text $state.Specification $wantPatched
        $stage = "$($state.Path).$([guid]::NewGuid().ToString('N')).stage"
        [IO.File]::WriteAllText($stage, $text, $gb2312)
        $targetHash = if ($wantPatched) {
            $state.Specification.TargetHash
        }
        else {
            $state.Specification.SourceHash
        }
        $stageHash = Get-Sha256 $stage
        if ($stageHash -ne $targetHash) {
            throw "Staged hash failed for $($state.Specification.Locale): $stageHash."
        }
        $stages.Add([pscustomobject]@{
                State = $state
                Stage = $stage
                Backup = $backup
                TargetHash = $targetHash
            })
    }
    Assert-ClientClosed $root
    foreach ($entry in $stages) {
        [IO.File]::Copy($entry.Stage, $entry.State.Path, $true)
        if ((Get-Sha256 $entry.State.Path) -ne $entry.TargetHash) {
            throw "Installed hash failed for $($entry.State.Specification.Locale)."
        }
    }
}
catch {
    $failure = $_
    foreach ($entry in $stages) {
        if (Test-Path -LiteralPath $entry.Backup -PathType Leaf) {
            [IO.File]::Copy($entry.Backup, $entry.State.Path, $true)
        }
    }
    throw "Pet aptitude tooltip install failed; predecessors restored: $failure"
}
finally {
    foreach ($entry in $stages) {
        Remove-Item -LiteralPath $entry.Stage -Force -ErrorAction SilentlyContinue
    }
}

[pscustomobject]@{
    Mode = $Mode
    Status = if ($wantPatched) { 'Patched' } else { 'Reverted' }
    Mapping = '7 Grumpy; 8 Brave; 9 Zealous; 10 Smart'
    Backup = $backupDirectory
    Locales = 'en_us, zh_cn'
}
