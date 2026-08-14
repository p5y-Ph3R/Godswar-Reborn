[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$BackupRoot = (Join-Path $PSScriptRoot '..\backups')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$relativeResource = 'UI\XML\PetIndentureUI.xml'
$stockHash =
    '90C5288452CA1B7B4944DD1FBE799FA3D828CE5C52381006B009607F4393CADD'
$patchedHash =
    'E302C6E340D16A1590C329E9E52DA300AF933696C6B945A973098C4A6966CCB4'
$stockLength = 11359
$patchedLength = 11219
$rankLine =
    "`t`t<PetPinjie Type=`"Text`" Texture=`"`" ID=`"871012`" " +
    "Rectangle=`"91,258,151,290`" Font=`"MainMap`" " +
    "FontColor=`"DEFAULT_TEXTCOLOR`" TextFormat=`"2`" Text=`"88.94`"/>"
$bonusLine =
    "`t`t<PetPinjie2 Type=`"Text`" Texture=`"`" ID=`"871018`" " +
    "Rectangle=`"151,259,181,300`" Font=`"MainMap`" " +
    "FontColor=`"GWORed`" TextFormat=`"0`" Text=`"(8)`"/>"
$stockFragment = $rankLine + "`r`n" + $bonusLine + "`r`n"
$patchedFragment = $rankLine + "`r`n"

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Read-StrictUtf8([string]$Path) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "Unexpected UTF-8 BOM in Soul Contract resource: $Path"
    }
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $encoding.GetString($bytes)
}

function Write-StrictUtf8(
    [string]$Path,
    [string]$Text
) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    [IO.File]::WriteAllBytes($Path, $encoding.GetBytes($Text))
}

function Get-ExactCount([string]$Text, [string]$Value) {
    [regex]::Matches($Text, [regex]::Escape($Value)).Count
}

function Assert-ResourceShape(
    [string]$Text,
    [string]$State,
    [string]$Path
) {
    if ((Get-ExactCount $Text $patchedFragment) -ne 1) {
        throw "Numeric Rank control is missing or duplicated: $Path"
    }
    if ((Get-ExactCount $Text ($bonusLine + "`r`n")) -ne
        $(if ($State -eq 'Stock') { 1 } else { 0 })) {
        throw "Rank bonus annotation has an invalid shape: $Path"
    }
    if (([regex]::Matches($Text, "(?<!`r)`n")).Count -ne 0) {
        throw "Soul Contract resource does not retain CRLF endings: $Path"
    }

    try { [xml]$xml = $Text }
    catch { throw "Soul Contract resource is not valid XML: $Path`n$_" }
    $rank = @($xml.SelectNodes('//*[@ID="871012"]'))
    $rankBonus = @($xml.SelectNodes('//*[@ID="871018"]'))
    if ($rank.Count -ne 1 -or $rank[0].Name -ne 'PetPinjie' -or
        $rank[0].FontColor -ne 'DEFAULT_TEXTCOLOR') {
        throw "Numeric Rank control was not preserved exactly: $Path"
    }
    $expectedBonusCount = if ($State -eq 'Stock') { 1 } else { 0 }
    if ($rankBonus.Count -ne $expectedBonusCount) {
        throw "Unexpected Rank bonus control count: $Path"
    }

    $attributeBonusIds = @(
        '872011', '872021', '872031',
        '872041', '872051', '872061')
    foreach ($id in $attributeBonusIds) {
        $node = @($xml.SelectNodes("//*[@ID='$id']"))
        if ($node.Count -ne 1 -or $node[0].FontColor -ne 'GWORed') {
            throw "Dynamic attribute bonus $id was not preserved: $Path"
        }
    }
}

function Assert-NativeNullGuard([string]$ClientExe) {
    if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
        throw "Origin client is missing: $ClientExe"
    }
    [byte[]]$bytes = [IO.File]::ReadAllBytes($ClientExe)
    [byte[]]$rankLookup =
        0x8B,0x11,0x8B,0x42,0x4C,0x6A,0x01,0x68,0x64,0x4A,0x0D,0x00,
        0xFF,0xD0,0x8B,0xD8,0x3B,0xDD,0x0F,0x84,0x2D,0x01,0x00,0x00
    [byte[]]$bonusLookup =
        0x8B,0x11,0x8B,0x42,0x4C,0x6A,0x01,0x68,0x6A,0x4A,0x0D,0x00,
        0xFF,0xD0,0x8B,0xE8,0x85,0xED,0x0F,0x84,0xFA,0x00,0x00,0x00
    foreach ($signature in @(
            @{ Offset = 0x1BEC92; Bytes = $rankLookup; Label = 'Rank' },
            @{ Offset = 0x1BEDE1; Bytes = $bonusLookup; Label = 'bonus' })) {
        if ($bytes.Length -lt $signature.Offset + $signature.Bytes.Length) {
            throw "Origin.exe is too short for the audited $($signature.Label) lookup."
        }
        for ($index = 0; $index -lt $signature.Bytes.Length; $index++) {
            if ($bytes[$signature.Offset + $index] -ne
                $signature.Bytes[$index]) {
                throw "Origin.exe failed the audited $($signature.Label) " +
                    'control/null-guard signature.'
            }
        }
    }
}

function Assert-OriginClosed([string]$ClientExe) {
    $expected = [IO.Path]::GetFullPath($ClientExe)
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try {
            try { $path = $process.Path } catch { $path = $null }
            if (-not $path -or [string]::Equals(
                    [IO.Path]::GetFullPath($path),
                    $expected,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Close Origin.exe before changing the Soul Contract UI.'
            }
        }
        finally { $process.Dispose() }
    }
}

$resolvedRoot = [IO.Path]::GetFullPath($ClientRoot)
$clientExe = Join-Path $resolvedRoot 'Origin.exe'
Assert-NativeNullGuard $clientExe
$records = foreach ($locale in @('en_us', 'zh_cn')) {
    $path = Join-Path $resolvedRoot (
        "Localization\$locale\$relativeResource")
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Soul Contract resource is missing: $path"
    }
    $hash = Get-Sha256 $path
    $length = (Get-Item -LiteralPath $path).Length
    $state = if ($hash -eq $stockHash -and $length -eq $stockLength) {
        'Stock'
    }
    elseif ($hash -eq $patchedHash -and $length -eq $patchedLength) {
        'Patched'
    }
    else {
        throw "Unsupported Soul Contract resource (length $length, " +
            "SHA-256 $hash): $path"
    }
    $text = Read-StrictUtf8 $path
    Assert-ResourceShape $text $state $path
    [pscustomobject]@{
        Locale = $locale
        Path = $path
        Hash = $hash
        State = $state
        Text = $text
    }
}

$states = @($records.State | Select-Object -Unique)
if ($states.Count -ne 1) {
    throw 'Soul Contract locale resources are in a mixed state.'
}
$current = $states[0]
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Status = if ($current -eq 'Patched') { 'Patched' } else { 'Ready' }
        Resources = $records.Count
        NumericRankPreserved = $true
        RankBonusVisible = $current -eq 'Stock'
        AttributeBonusesPreserved = 6
        Hash = if ($current -eq 'Patched') { $patchedHash } else { $stockHash }
    }
    return
}

Assert-OriginClosed $clientExe
$target = if ($Mode -eq 'Apply') { 'Patched' } else { 'Stock' }
if ($current -eq $target) {
    [pscustomobject]@{
        Status = "Already $($target.ToLowerInvariant())"
        NumericRankPreserved = $true
        RankBonusVisible = $target -eq 'Stock'
        Hash = if ($target -eq 'Patched') { $patchedHash } else { $stockHash }
    }
    return
}

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'pet-soul-rank-annotation-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$staged = @()
try {
    foreach ($record in $records) {
        $backup = Join-Path $backupDirectory (
            "$($record.Locale)-PetIndentureUI.xml")
        $stage = "$($record.Path).$([Guid]::NewGuid().ToString('N')).stage"
        Copy-Item -LiteralPath $record.Path -Destination $backup
        if ((Get-Sha256 $backup) -ne $record.Hash) {
            throw "Soul Contract backup verification failed: $($record.Path)"
        }

        $from = if ($target -eq 'Patched') {
            $stockFragment
        }
        else { $patchedFragment }
        $to = if ($target -eq 'Patched') {
            $patchedFragment
        }
        else { $stockFragment }
        if ((Get-ExactCount $record.Text $from) -ne 1) {
            throw "Soul Contract Rank fragment is partial: $($record.Path)"
        }
        $output = $record.Text.Replace($from, $to)
        Assert-ResourceShape $output $target $record.Path
        Write-StrictUtf8 $stage $output
        $expectedHash = if ($target -eq 'Patched') {
            $patchedHash
        }
        else { $stockHash }
        if ((Get-Sha256 $stage) -ne $expectedHash) {
            throw "Staged Soul Contract resource hash is not exact: " +
                $record.Path
        }
        $staged += [pscustomobject]@{
            Path = $record.Path
            Backup = $backup
            Stage = $stage
            ExpectedHash = $expectedHash
        }
    }

    foreach ($record in $staged) {
        Move-Item -LiteralPath $record.Stage -Destination $record.Path -Force
    }
    foreach ($record in $staged) {
        if ((Get-Sha256 $record.Path) -ne $record.ExpectedHash) {
            throw "Installed Soul Contract resource hash is not exact: " +
                $record.Path
        }
    }
}
catch {
    $failure = $_
    foreach ($record in $staged) {
        if (Test-Path -LiteralPath $record.Backup -PathType Leaf) {
            Copy-Item -LiteralPath $record.Backup `
                -Destination $record.Path -Force
        }
        if (Test-Path -LiteralPath $record.Stage -PathType Leaf) {
            Remove-Item -LiteralPath $record.Stage -Force
        }
    }
    throw $failure
}

[pscustomobject]@{
    Status = if ($target -eq 'Patched') { 'Patched' } else { 'Reverted' }
    Resources = $records.Count
    NumericRankPreserved = $true
    RankBonusVisible = $target -eq 'Stock'
    AttributeBonusesPreserved = 6
    Hash = if ($target -eq 'Patched') { $patchedHash } else { $stockHash }
    Backup = $backupDirectory
}
