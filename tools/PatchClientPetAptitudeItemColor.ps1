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
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$mapping = '7 Grumpy; 8 Brave; 9 Zealous; 10 Smart'
$specifications = @(
    [pscustomobject]@{
        Locale = 'en_us'
        StockHash =
            'E04DA2A7E55B7E2250ACD5DF52A83CADA42FA9B173297BE7F7A554FE133A7C00'
        LegacyProjectHash =
            '8202DBF6F83DE1B0916FC140AA93337414FF8DEC049AE5CBB7BAF2903806E91A'
        ProjectHash =
            '172FC5E55F93D2D49B3D6B2976E647F5D11D6EF28403EFC38E324EB5EC814646'
    },
    [pscustomobject]@{
        Locale = 'zh_cn'
        StockHash =
            '2DDC5FB8192DDC3ABF9493578ED8DA9A3034862FE6D5DD72874DF735A04FD3BC'
        LegacyProjectHash =
            '99C72FB3818A3C3AB5A1B5CFB0278A43F2339B37CCE7F1A6390FB05BECA625A9'
        ProjectHash =
            '8073EDF033DD2F2D3E7031EFB98002EB8DD97D2575A4277A38E5A501F2424BC5'
    }
)

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-IsWithin([string]$Candidate, [string]$Directory) {
    $candidatePath = [IO.Path]::GetFullPath($Candidate).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $directoryPath = [IO.Path]::GetFullPath($Directory).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if ([string]::Equals($candidatePath, $directoryPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    $directoryPrefix = $directoryPath +
        [IO.Path]::DirectorySeparatorChar
    return $candidatePath.StartsWith(
        $directoryPrefix, [StringComparison]::OrdinalIgnoreCase)
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

function Set-SmartColor(
    [string]$Text,
    [object]$Specification,
    [string]$Expected,
    [string]$Desired
) {
    $rows = Get-AptitudeRows $Text $Specification.Locale
    $smart = $rows[9]
    if ($smart.GetAttribute('BaseColor') -cne $Expected) {
        throw "$($Specification.Locale) aptitude 10 expected $Expected."
    }
    if ($Expected -ceq $Desired) { return $Text }
    $name = $smart.GetAttribute('BaseName')
    $old = 'BaseLv="10" BaseName="{0}" BaseColor="{1}"' -f
        $name, $Expected
    $new = 'BaseLv="10" BaseName="{0}" BaseColor="{1}"' -f
        $name, $Desired
    if ([regex]::Matches($Text, [regex]::Escape($old)).Count -ne 1) {
        throw "$($Specification.Locale) aptitude 10 color is not uniquely guarded."
    }
    $result = $Text.Replace($old, $new)
    [void](Get-AptitudeRows $result $Specification.Locale)
    return $result
}

function Convert-AptitudeState(
    [string]$Text,
    [object]$Specification,
    [string]$FromState,
    [string]$ToState
) {
    $fromUsesProjectNames = $FromState -cne 'StockOrder'
    $toUsesProjectNames = $ToState -cne 'StockOrder'
    $result = $Text
    if ($fromUsesProjectNames -ne $toUsesProjectNames) {
        $result = Convert-AptitudeNames $result $Specification `
            $toUsesProjectNames
    }
    $oldColor = if ($FromState -ceq 'ProjectOrder') {
        'YELLOW_TEXTCOLOR'
    }
    else { 'GREEN_TEXTCOLOR' }
    $newColor = if ($ToState -ceq 'ProjectOrder') {
        'YELLOW_TEXTCOLOR'
    }
    else { 'GREEN_TEXTCOLOR' }
    return (Set-SmartColor $result $Specification $oldColor $newColor)
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
    $state = if ($hash -eq $specification.StockHash) {
        'StockOrder'
    }
    elseif ($hash -eq $specification.LegacyProjectHash) {
        'LegacyProjectOrder'
    }
    elseif ($hash -eq $specification.ProjectHash) {
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
$currentState = $distinctStates[0]
$isPatched = $currentState -eq 'ProjectOrder'
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) {
            'Patched'
        }
        elseif ($currentState -eq 'LegacyProjectOrder') {
            'Migration required'
        }
        else { 'Ready to apply' }
        State = $currentState
        Mapping = $mapping
        SmartColor = if ($isPatched) {
            'YELLOW_TEXTCOLOR'
        }
        else { 'GREEN_TEXTCOLOR' }
        Locales = 'en_us, zh_cn'
    }
    return
}

Assert-ClientClosed $root
$wantPatched = $Mode -eq 'Apply'
$targetState = if ($wantPatched) { 'ProjectOrder' } else { 'StockOrder' }
if ($currentState -eq $targetState) {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) { 'Already patched' } else { 'Already reverted' }
        State = $currentState
        Locales = 'en_us, zh_cn'
    }
    return
}

$backupRootPath = [IO.Path]::GetFullPath($BackupRoot)
if (Test-IsWithin $backupRootPath $root) {
    throw 'BackupRoot must be outside the client directory.'
}
$backupDirectory = Join-Path $backupRootPath (
    'client-pet-aptitude-itemcolor-' + $Mode + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$stages = [Collections.Generic.List[object]]::new()
$receiptPath = Join-Path $backupDirectory 'receipt.json'
$receiptStage = "$receiptPath.$([guid]::NewGuid().ToString('N')).stage"
$receiptHash = $null
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
        $text = Convert-AptitudeState $state.Text $state.Specification `
            $currentState $targetState
        $stage = "$($state.Path).$([guid]::NewGuid().ToString('N')).stage"
        [IO.File]::WriteAllText($stage, $text, $gb2312)
        $targetHash = if ($targetState -eq 'ProjectOrder') {
            $state.Specification.ProjectHash
        }
        else {
            $state.Specification.StockHash
        }
        $stageHash = Get-Sha256 $stage
        if ($stageHash -ne $targetHash) {
            throw "Staged hash failed for $($state.Specification.Locale): $stageHash."
        }
        $stages.Add([pscustomobject]@{
                State = $state
                Stage = $stage
                Backup = $backup
                Relative = $relative
                TargetHash = $targetHash
            })
    }
    Assert-ClientClosed $root
    foreach ($entry in $stages) {
        if ((Get-Sha256 $entry.State.Path) -ne $entry.State.Hash) {
            throw "Source changed before install for " +
                "$($entry.State.Specification.Locale)."
        }
        Assert-ClientClosed $root
        [IO.File]::Copy($entry.Stage, $entry.State.Path, $true)
        if ((Get-Sha256 $entry.State.Path) -ne $entry.TargetHash) {
            throw "Installed hash failed for $($entry.State.Specification.Locale)."
        }
    }
    $receipt = [ordered]@{
        Schema = 'reborn.client-pet-aptitude-itemcolor/v2'
        CreatedAtUtc = [DateTime]::UtcNow.ToString('o')
        Mode = $Mode
        ClientRoot = $root
        SourceState = $currentState
        TargetState = $targetState
        Mapping = $mapping
        SmartColor = if ($targetState -eq 'ProjectOrder') {
            'YELLOW_TEXTCOLOR'
        }
        else { 'GREEN_TEXTCOLOR' }
        Locales = @($stages | ForEach-Object {
                [ordered]@{
                    Locale = $_.State.Specification.Locale
                    RelativePath = $_.Relative
                    BackupPath = $_.Backup
                    BeforeSha256 = $_.State.Hash
                    AfterSha256 = $_.TargetHash
                }
            })
    }
    $json = $receipt | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($receiptStage, $json, $utf8NoBom)
    [IO.File]::Move($receiptStage, $receiptPath)
    $readback = [IO.File]::ReadAllText($receiptPath, $utf8NoBom) |
        ConvertFrom-Json
    if ($readback.Schema -cne $receipt.Schema -or
        $readback.Mode -cne $Mode -or
        $readback.SourceState -cne $currentState -or
        $readback.TargetState -cne $targetState -or
        @($readback.Locales).Count -ne $stages.Count) {
        throw 'Receipt verification failed.'
    }
    foreach ($entry in $stages) {
        $locale = $entry.State.Specification.Locale
        $record = @($readback.Locales | Where-Object Locale -CEQ $locale)
        if ($record.Count -ne 1 -or
            $record[0].BeforeSha256 -cne $entry.State.Hash -or
            $record[0].AfterSha256 -cne $entry.TargetHash -or
            $record[0].BackupPath -cne $entry.Backup -or
            (Get-Sha256 $entry.Backup) -ne $entry.State.Hash -or
            (Get-Sha256 $entry.State.Path) -ne $entry.TargetHash) {
            throw "Receipt verification failed for $locale."
        }
    }
    $receiptHash = Get-Sha256 $receiptPath
}
catch {
    $failure = $_
    foreach ($entry in $stages) {
        if (Test-Path -LiteralPath $entry.Backup -PathType Leaf) {
            [IO.File]::Copy($entry.Backup, $entry.State.Path, $true)
        }
    }
    Remove-Item -LiteralPath $receiptPath -Force -ErrorAction SilentlyContinue
    throw "Pet aptitude tooltip install failed; predecessors restored: $failure"
}
finally {
    foreach ($entry in $stages) {
        Remove-Item -LiteralPath $entry.Stage -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $receiptStage -Force -ErrorAction SilentlyContinue
}

[pscustomobject]@{
    Mode = $Mode
    Status = if ($wantPatched) { 'Patched' } else { 'Reverted' }
    SourceState = $currentState
    State = $targetState
    Mapping = $mapping
    SmartColor = if ($wantPatched) {
        'YELLOW_TEXTCOLOR'
    }
    else { 'GREEN_TEXTCOLOR' }
    Backup = $backupDirectory
    Receipt = $receiptPath
    ReceiptSha256 = $receiptHash
    Locales = 'en_us, zh_cn'
}
