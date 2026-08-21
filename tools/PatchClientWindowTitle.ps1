[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Rollback')]
    [string]$Mode = 'Status',
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot = '',
    [string]$RollbackFrom = '',
    [switch]$AllowMutation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$targetTitle = 'Godswar Reborn'
$chinesePristineTitle = -join @(
    [char]0x795E,
    [char]0x6218,
    [char]0x8D77,
    [char]0x6E90)
$assetDefinitions = @(
    [pscustomobject]@{
        Locale = 'en_us'
        RelativePath = 'Localization\en_us\Text\Message.dat'
        PristineTitles = @('Godswar Origin')
    },
    [pscustomobject]@{
        Locale = 'zh_cn'
        RelativePath = 'Localization\zh_cn\Text\Message.dat'
        PristineTitles = @($chinesePristineTitle)
    }
)
$strictUtf16 = [Text.UnicodeEncoding]::new($false, $false, $true)
$utf16WithBom = [Text.UnicodeEncoding]::new($false, $true, $true)
$originRelativePath = 'Origin.exe'
$originTitleFormatOffset = 0x554FCC
$originAppTitleKeyOffset = 0x554F78
$originDynamicSuffixOffset = 0x557904
$originAreaFormat = $strictUtf16.GetBytes("%s %s`0")
$originBaseOnlyFormat = $strictUtf16.GetBytes("%s`0`0`0`0")
$originDynamicSuffix = $strictUtf16.GetBytes(" - `0")
$originAppTitleKey = [Text.Encoding]::ASCII.GetBytes("AppTitle`0")
if ($targetTitle.Length -gt 127) {
    throw 'The base title exceeds Origin.exe''s 128-code-unit title buffer.'
}

function Get-Sha256Hex {
    param([byte[]]$Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($Bytes))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-FullClientPath {
    param(
        [string]$Root,
        [string]$RelativePath
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath((Join-Path $rootPath $RelativePath))
    $prefix = $rootPath + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Client asset escapes the client root: $RelativePath"
    }
    return $path
}

function Test-BytesAtOffset {
    param(
        [byte[]]$Bytes,
        [int]$Offset,
        [byte[]]$Expected
    )

    if ($Offset -lt 0 -or $Offset + $Expected.Length -gt $Bytes.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Bytes[$Offset + $index] -ne $Expected[$index]) {
            return $false
        }
    }
    return $true
}

function Read-OriginTitleAsset {
    param([string]$Root)

    $path = Get-FullClientPath $Root $originRelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required client executable is missing: $path"
    }
    $bytes = [IO.File]::ReadAllBytes($path)
    $hasAppTitleKey = Test-BytesAtOffset `
        $bytes $originAppTitleKeyOffset $originAppTitleKey
    $hasDynamicSuffix = Test-BytesAtOffset `
        $bytes $originDynamicSuffixOffset $originDynamicSuffix
    if ($bytes.Length -le $originDynamicSuffixOffset +
            $originDynamicSuffix.Length -or
        $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A -or
        -not $hasAppTitleKey -or -not $hasDynamicSuffix) {
        throw 'Origin.exe does not match the reviewed title-source layout.'
    }

    $hasBaseOnlyFormat = Test-BytesAtOffset `
        $bytes $originTitleFormatOffset $originBaseOnlyFormat
    $hasAreaFormat = Test-BytesAtOffset `
        $bytes $originTitleFormatOffset $originAreaFormat
    $state = if ($hasBaseOnlyFormat) {
        'Patched'
    }
    elseif ($hasAreaFormat) {
        'Pristine'
    }
    else {
        'Unknown'
    }
    return [pscustomobject]@{
        Kind = 'binary'
        Locale = $null
        RelativePath = $originRelativePath
        Path = $path
        Bytes = $bytes
        State = $state
        Sha256 = Get-Sha256Hex $bytes
        Title = $null
    }
}

function New-PatchedOriginBytes {
    param([pscustomobject]$Asset)

    if ($Asset.State -ceq 'Patched') {
        return $Asset.Bytes
    }
    if ($Asset.State -cne 'Pristine') {
        throw 'Origin.exe title format is not a recognized predecessor.'
    }
    $result = [byte[]]$Asset.Bytes.Clone()
    [Array]::Copy(
        $originBaseOnlyFormat,
        0,
        $result,
        $originTitleFormatOffset,
        $originBaseOnlyFormat.Length)
    return $result
}

function Read-MessageAsset {
    param(
        [pscustomobject]$Definition,
        [string]$Root
    )

    $path = Get-FullClientPath $Root $Definition.RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required client title asset is missing: $path"
    }

    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 4 -or $bytes[0] -ne 0xFF -or
        $bytes[1] -ne 0xFE -or (($bytes.Length - 2) % 2) -ne 0) {
        throw "$path is not a well-formed BOM-prefixed UTF-16LE asset."
    }
    $text = $strictUtf16.GetString($bytes, 2, $bytes.Length - 2)
    if ($text.IndexOf("`r`n", [StringComparison]::Ordinal) -lt 0) {
        throw "$path does not retain the expected CRLF record format."
    }

    $titleMatches = [regex]::Matches(
        $text,
        '(?m)^AppTitle\t([^\r\n]*)\r?$')
    if ($titleMatches.Count -ne 1) {
        throw "$path must contain exactly one AppTitle record."
    }

    $areaTitles = [ordered]@{}
    foreach ($key in @('AreaTitle11', 'AreaTitle12', 'AreaTitle13')) {
        $matches = [regex]::Matches(
            $text,
            "(?m)^$key\t([^\r\n]*)\r?`$")
        if ($matches.Count -ne 1) {
            throw "$path must contain exactly one $key record."
        }
        $areaTitles[$key] = $matches[0].Groups[1].Value
    }

    $title = $titleMatches[0].Groups[1].Value
    $state = if ($title -ceq $targetTitle) {
        'Patched'
    }
    elseif ($Definition.PristineTitles -ccontains $title) {
        'Pristine'
    }
    else {
        'Unknown'
    }

    return [pscustomobject]@{
        Kind = 'localization'
        Locale = $Definition.Locale
        RelativePath = $Definition.RelativePath
        Path = $path
        Bytes = $bytes
        Text = $text
        Title = $title
        State = $state
        Sha256 = Get-Sha256Hex $bytes
        AreaTitles = $areaTitles
        TitleMatch = $titleMatches[0]
    }
}

function New-PatchedBytes {
    param([pscustomobject]$Asset)

    $match = $Asset.TitleMatch
    $value = $match.Groups[1]
    $updated = $Asset.Text.Substring(0, $value.Index) +
        $targetTitle +
        $Asset.Text.Substring($value.Index + $value.Length)
    $body = $strictUtf16.GetBytes($updated)
    $preamble = $utf16WithBom.GetPreamble()
    $result = [byte[]]::new($preamble.Length + $body.Length)
    [Array]::Copy($preamble, 0, $result, 0, $preamble.Length)
    [Array]::Copy($body, 0, $result, $preamble.Length, $body.Length)
    return $result
}

function Get-StatusResult {
    param(
        [object[]]$Assets,
        [pscustomobject]$Origin,
        [string]$BackupDirectory = ''
    )

    $running = @(Get-Process -Name Origin -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Id)
    $state = if ($Assets.State -contains 'Unknown' -or
        $Origin.State -ceq 'Unknown') {
        'Unknown'
    }
    elseif ($Assets.State -notcontains 'Pristine' -and
        $Origin.State -ceq 'Patched') {
        'Patched'
    }
    elseif ($Assets.State -notcontains 'Patched' -and
        $Origin.State -ceq 'Pristine') {
        'Pristine'
    }
    elseif ($Assets.State -notcontains 'Pristine' -and
        $Origin.State -ceq 'Pristine') {
        'BrandOnly'
    }
    else {
        'Mixed'
    }
    $baseTitle = if ($Assets[0].State -ceq 'Patched') {
        $targetTitle
    }
    else {
        $Assets[0].Title
    }
    $loginTitle = if ($Origin.State -ceq 'Patched') {
        $baseTitle
    }
    else {
        "$baseTitle $($Assets[0].AreaTitles.AreaTitle11)"
    }
    return [pscustomobject]@{
        State = $state
        TargetTitle = $targetTitle
        LoginTitle = $loginTitle
        RealmTitleTemplate = "$loginTitle - <realm>"
        Assets = @($Assets | ForEach-Object {
            [pscustomobject]@{
                Locale = $_.Locale
                Path = $_.Path
                State = $_.State
                Title = $_.Title
                Sha256 = $_.Sha256
                Region11 = $_.AreaTitles.AreaTitle11
            }
        })
        Executable = [pscustomobject]@{
            Path = $Origin.Path
            State = $Origin.State
            Sha256 = $Origin.Sha256
            FormatOffset = ('0x{0:X}' -f $originTitleFormatOffset)
            DynamicRealmSuffixPreserved = $true
        }
        BackupDirectory = $BackupDirectory
        RunningOriginProcessIds = $running
        RestartRequired = $running.Count -gt 0
    }
}

function Write-VerifiedBytes {
    param(
        [string]$Path,
        [byte[]]$Bytes
    )

    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $replacementBackup =
        "$Path.$([Guid]::NewGuid().ToString('N')).replace.bak"
    try {
        [IO.File]::WriteAllBytes($temporary, $Bytes)
        if ((Get-Sha256Hex ([IO.File]::ReadAllBytes($temporary))) -cne
            (Get-Sha256Hex $Bytes)) {
            throw "Temporary title asset verification failed: $Path"
        }
        [IO.File]::Replace($temporary, $Path, $replacementBackup)
    }
    finally {
        foreach ($candidate in @($temporary, $replacementBackup)) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                Remove-Item -LiteralPath $candidate -Force
            }
        }
    }
}

$resolvedClientRoot = [IO.Path]::GetFullPath($ClientRoot)
$assets = @($assetDefinitions | ForEach-Object {
    Read-MessageAsset $_ $resolvedClientRoot
})
$origin = Read-OriginTitleAsset $resolvedClientRoot

if ($Mode -ceq 'Status') {
    Get-StatusResult $assets $origin
    return
}
if (-not $AllowMutation) {
    throw "$Mode requires -AllowMutation."
}

if ($Mode -ceq 'Apply') {
    if ($assets.State -contains 'Unknown' -or
        $origin.State -ceq 'Unknown') {
        throw 'At least one title source is unknown; refusing to patch.'
    }
    $beforeStatus = Get-StatusResult $assets $origin
    if ($beforeStatus.State -ceq 'Patched') {
        $beforeStatus
        return
    }
    $runningOrigin = @(Get-Process -Name Origin -ErrorAction SilentlyContinue)
    if ($origin.State -ceq 'Pristine' -and $runningOrigin.Count -gt 0) {
        $ids = ($runningOrigin.Id -join ', ')
        throw "Close Origin.exe before patching its title format (PID: $ids)."
    }

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        $repositoryRoot = [IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot '..'))
        $BackupRoot = Join-Path $repositoryRoot `
            'artifacts\client-window-title-backups'
    }
    $resolvedBackupRoot = [IO.Path]::GetFullPath($BackupRoot)
    $predecessor = if (Test-Path -LiteralPath $resolvedBackupRoot) {
        Get-ChildItem -LiteralPath $resolvedBackupRoot -Directory |
            Where-Object {
                Test-Path -LiteralPath (
                    Join-Path $_.FullName 'manifest.json') -PathType Leaf
            } |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
    else {
        $null
    }
    $backupDirectory = Join-Path $resolvedBackupRoot (
        [DateTimeOffset]::UtcNow.UtcDateTime.ToString(
            'yyyyMMddTHHmmssfffZ') + '-' +
        [Guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null

    $manifestAssets = @()
    $patchedBytes = @{}
    $allAssets = @($assets) + @($origin)
    foreach ($asset in $allAssets) {
        $nextBytes = if ($asset.State -ceq 'Patched') {
            $asset.Bytes
        }
        elseif ($asset.Kind -ceq 'binary') {
            New-PatchedOriginBytes $asset
        }
        else {
            New-PatchedBytes $asset
        }
        $patchedBytes[$asset.RelativePath] = $nextBytes
        $backupPath = Get-FullClientPath `
            $backupDirectory $asset.RelativePath
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($backupPath)) | Out-Null
        [IO.File]::WriteAllBytes($backupPath, $asset.Bytes)
        if ((Get-Sha256Hex ([IO.File]::ReadAllBytes($backupPath))) -cne
            $asset.Sha256) {
            throw "Title asset backup verification failed: $backupPath"
        }
        $manifestAssets += [ordered]@{
            kind = $asset.Kind
            locale = $asset.Locale
            relativePath = $asset.RelativePath
            originalSha256 = $asset.Sha256
            patchedSha256 = Get-Sha256Hex $nextBytes
            originalTitle = $asset.Title
            patchedTitle = if ($asset.Kind -ceq 'localization') {
                $targetTitle
            }
            else {
                $null
            }
        }
    }

    $manifest = [ordered]@{
        contractVersion = 2
        clientRoot = $resolvedClientRoot
        targetTitle = $targetTitle
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        predecessorBackupDirectory = $predecessor
        assets = $manifestAssets
    }
    [IO.File]::WriteAllText(
        (Join-Path $backupDirectory 'manifest.json'),
        ($manifest | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($true))

    $changed = [Collections.Generic.List[object]]::new()
    try {
        foreach ($asset in $allAssets) {
            if ($asset.State -ceq 'Patched') {
                continue
            }
            Write-VerifiedBytes `
                $asset.Path $patchedBytes[$asset.RelativePath]
            $changed.Add($asset)
        }
        $verified = @($assetDefinitions | ForEach-Object {
            Read-MessageAsset $_ $resolvedClientRoot
        })
        $verifiedOrigin = Read-OriginTitleAsset $resolvedClientRoot
        $verifiedStatus = Get-StatusResult `
            $verified $verifiedOrigin $backupDirectory
        if ($verifiedStatus.State -cne 'Patched' -or
            $verifiedStatus.LoginTitle -cne $targetTitle -or
            $verifiedStatus.RealmTitleTemplate -cne
                "$targetTitle - <realm>") {
            throw 'Installed client title sources did not verify as patched.'
        }
        $verifiedStatus
        return
    }
    catch {
        foreach ($asset in $changed) {
            $backupPath = Get-FullClientPath `
                $backupDirectory $asset.RelativePath
            Write-VerifiedBytes $asset.Path (
                [IO.File]::ReadAllBytes($backupPath))
        }
        throw
    }
}

if ([string]::IsNullOrWhiteSpace($RollbackFrom)) {
    throw 'Rollback requires -RollbackFrom <verified backup directory>.'
}
$resolvedBackup = [IO.Path]::GetFullPath($RollbackFrom)
$manifestPath = Join-Path $resolvedBackup 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Rollback manifest is missing: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Encoding UTF8 -Raw |
    ConvertFrom-Json
if ($manifest.contractVersion -notin @(1, 2) -or
    $manifest.targetTitle -cne $targetTitle -or
    [IO.Path]::GetFullPath([string]$manifest.clientRoot) -cne
        $resolvedClientRoot) {
    throw 'Rollback manifest does not match this client/title contract.'
}

$allowedPaths = @($assetDefinitions.RelativePath) + @($originRelativePath)
foreach ($entry in $manifest.assets) {
    if ($allowedPaths -cnotcontains [string]$entry.relativePath) {
        throw "Rollback manifest contains an unknown asset: $($entry.relativePath)"
    }
    $destination = Get-FullClientPath `
        $resolvedClientRoot $entry.relativePath
    $currentHash = Get-Sha256Hex ([IO.File]::ReadAllBytes($destination))
    if ($currentHash -cne $entry.patchedSha256 -and
        $currentHash -cne $entry.originalSha256) {
        throw "Rollback refuses to overwrite a diverged asset: $destination"
    }
    if ($currentHash -ceq $entry.originalSha256) {
        continue
    }
    $backupPath = Get-FullClientPath $resolvedBackup $entry.relativePath
    $backupBytes = [IO.File]::ReadAllBytes($backupPath)
    if ((Get-Sha256Hex $backupBytes) -cne $entry.originalSha256) {
        throw "Rollback backup hash mismatch: $backupPath"
    }
    Write-VerifiedBytes $destination $backupBytes
}

$rolledBack = @($assetDefinitions | ForEach-Object {
    Read-MessageAsset $_ $resolvedClientRoot
})
$rolledBackOrigin = Read-OriginTitleAsset $resolvedClientRoot
Get-StatusResult $rolledBack $rolledBackOrigin $resolvedBackup
