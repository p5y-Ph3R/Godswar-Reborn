[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$BackupRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$locales = @('en_us', 'zh_cn')
$sourceHash =
    '6A6F17DF922B1D32A298156105A198141506C44E40FB79464524653235A55B4F'
$patchedHash =
    '4D5E1B152FAC41BBE5527D8A0DBDFB4AFC8BC589BEDA418E9B784C755EFD4E69'
$utf8Bom = [Text.UTF8Encoding]::new($true, $true)
$utf8NoBom = [Text.UTF8Encoding]::new($false, $true)
if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path $PSScriptRoot '..\backups'
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-UniqueText(
    [string]$Text,
    [string]$Needle,
    [string]$Label
) {
    $count = [regex]::Matches($Text, [regex]::Escape($Needle)).Count
    if ($count -ne 1) {
        throw "$Label must occur exactly once; found $count."
    }
}

function Replace-UniqueText(
    [string]$Text,
    [string]$Before,
    [string]$After,
    [string]$Label
) {
    Assert-UniqueText $Text $Before $Label
    return $Text.Replace($Before, $After)
}

function Convert-ZodiacTooltipText(
    [string]$Text,
    [bool]$ToPatched
) {
    $newline = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $originalPrelude = @(
        'local Max_Lev = 50',
        'function SkillTrain_OnHov(index)'
    ) -join $newline
    $patchedPrelude = @(
        'local Max_Lev = 50',
        '-- REBORN_ZODIAC_SKILL_TOOLTIP_BEGIN',
        'local Type2_Max_MP_Lev = 45',
        'local function SkillTrain_GetDisplayedMP(gird,index,lev)',
        "`tif index >=4 and index <=7 and lev > Type2_Max_MP_Lev then",
        "`t`tlev = Type2_Max_MP_Lev",
        "`tend",
        "`treturn gird.MP[lev]",
        'end',
        '-- REBORN_ZODIAC_SKILL_TOOLTIP_END',
        'function SkillTrain_OnHov(index)'
    ) -join $newline

    if ($ToPatched) {
        $result = Replace-UniqueText $Text 'gird.MP[lev+1]' (
            'SkillTrain_GetDisplayedMP(gird,index,lev+1)') (
            'next-level MP lookup')
        $result = Replace-UniqueText $result 'ST_X0_13..gird.MP[lev]' (
            'ST_X0_13..SkillTrain_GetDisplayedMP(gird,index,lev)') (
            'current-level MP lookup')
        $result = Replace-UniqueText $result 'ST_X0_12..lev.."/40"' (
            'ST_X0_12..lev.."/50"') 'grid-level denominator'
        return Replace-UniqueText $result $originalPrelude $patchedPrelude (
            'Zodiac tooltip prelude')
    }

    $result = Replace-UniqueText $Text $patchedPrelude $originalPrelude (
        'patched Zodiac tooltip prelude')
    $result = Replace-UniqueText $result (
        'SkillTrain_GetDisplayedMP(gird,index,lev+1)') 'gird.MP[lev+1]' (
        'patched next-level MP lookup')
    $result = Replace-UniqueText $result (
        'ST_X0_13..SkillTrain_GetDisplayedMP(gird,index,lev)') (
        'ST_X0_13..gird.MP[lev]') 'patched current-level MP lookup'
    return Replace-UniqueText $result 'ST_X0_12..lev.."/50"' (
        'ST_X0_12..lev.."/40"') 'patched grid-level denominator'
}

function Assert-Utf8BomCrlf([byte[]]$Bytes, [string]$Locale) {
    if ($Bytes.Length -lt 3 -or
        $Bytes[0] -ne 0xEF -or $Bytes[1] -ne 0xBB -or
        $Bytes[2] -ne 0xBF) {
        throw "$Locale SkillTrainProc.lua must be UTF-8 with BOM."
    }
    for ($index = 3; $index -lt $Bytes.Length; $index++) {
        if ($Bytes[$index] -eq 0x0A -and
            ($index -eq 3 -or $Bytes[$index - 1] -ne 0x0D)) {
            throw "$Locale SkillTrainProc.lua must use CRLF newlines."
        }
    }
}

function Read-ZodiacTooltipAsset([string]$Path, [string]$Locale) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing $Locale Zodiac tooltip script: $Path"
    }
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    Assert-Utf8BomCrlf $bytes $Locale
    $hash = Get-Sha256 $Path
    $state = if ($hash -eq $sourceHash) {
        'Original'
    }
    elseif ($hash -eq $patchedHash) {
        'Patched'
    }
    else {
        throw "Unsupported $Locale SkillTrainProc.lua SHA-256 $hash."
    }
    $text = [IO.File]::ReadAllText($Path, $utf8Bom)
    $converted = Convert-ZodiacTooltipText $text ($state -eq 'Original')
    if ([string]::IsNullOrEmpty($converted)) {
        throw "$Locale Zodiac tooltip transformation produced no text."
    }
    return [pscustomobject]@{
        Locale = $Locale
        Path = $Path
        Hash = $hash
        State = $state
        Text = $text
    }
}

function Assert-ClientClosed([string]$Root) {
    $executables = @(
        'Origin.exe',
        'Origin_sixsocket.exe',
        'Launch.exe',
        'patcher.exe'
    )
    $paths = @($executables | ForEach-Object {
            [IO.Path]::GetFullPath((Join-Path $Root $_))
        })
    $names = @($executables | ForEach-Object {
            [IO.Path]::GetFileNameWithoutExtension($_)
        })
    foreach ($process in @(
            Get-Process -Name $names -ErrorAction SilentlyContinue)) {
        try {
            try { $path = $process.Path } catch { $path = $null }
            if ($path -and @($paths | Where-Object {
                        [string]::Equals(
                            [IO.Path]::GetFullPath($path),
                            $_,
                            [StringComparison]::OrdinalIgnoreCase)
                    }).Count -gt 0) {
                throw ('Close the GodsWar client, launcher, and patcher ' +
                    'before changing Zodiac tooltips.')
            }
        }
        finally {
            $process.Dispose()
        }
    }
}

$root = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\', '/')
if ($root -eq [IO.Path]::GetPathRoot($root)) {
    throw 'ClientRoot cannot be a filesystem root.'
}

$assets = @($locales | ForEach-Object {
        $relative = "Localization\$_\UI\XML\SkillTrainProc.lua"
        Read-ZodiacTooltipAsset (Join-Path $root $relative) $_
    })
$states = @($assets.State | Sort-Object -Unique)
if ($states.Count -ne 1) {
    throw 'Zodiac tooltip locale scripts are in different states.'
}
$isPatched = $states[0] -eq 'Patched'

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) { 'Patched' } else { 'Ready to apply' }
        LevelDisplay = if ($isPatched) { '/50' } else { '/40' }
        Type2MpDisplay = if ($isPatched) {
            'Levels 45-50 cap at 300%'
        }
        else {
            'Levels 45-50 can fail because MP data ends at level 45'
        }
        Locales = $locales -join ', '
    }
    return
}

Assert-ClientClosed $root
$wantPatched = $Mode -eq 'Apply'
if ($wantPatched -eq $isPatched) {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) {
            'Already patched'
        }
        else {
            'Already reverted'
        }
        Locales = $locales -join ', '
    }
    return
}

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-zodiac-skill-tooltip-' + $Mode + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$entries = [Collections.Generic.List[object]]::new()
$stages = [Collections.Generic.List[string]]::new()

try {
    foreach ($asset in $assets) {
        $relative = "Localization\$($asset.Locale)\UI\XML\SkillTrainProc.lua"
        $backup = Join-Path $backupDirectory $relative
        [IO.Directory]::CreateDirectory((Split-Path $backup -Parent)) |
            Out-Null
        Copy-Item -LiteralPath $asset.Path -Destination $backup
        if ((Get-Sha256 $backup) -ne $asset.Hash) {
            throw "Backup verification failed for $($asset.Locale)."
        }

        $targetText = Convert-ZodiacTooltipText $asset.Text $wantPatched
        $targetHash = if ($wantPatched) { $patchedHash } else { $sourceHash }
        $stage = "$($asset.Path).$([guid]::NewGuid().ToString('N')).stage"
        $stages.Add($stage)
        [IO.File]::WriteAllText($stage, $targetText, $utf8Bom)
        if ((Get-Sha256 $stage) -ne $targetHash) {
            throw "Staged hash failed for $($asset.Locale)."
        }
        [void](Read-ZodiacTooltipAsset $stage $asset.Locale)
        $entries.Add([pscustomobject]@{
                Asset = $asset
                RelativePath = $relative
                Backup = $backup
                Stage = $stage
                TargetHash = $targetHash
            })
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        patch = 'client-zodiac-skill-tooltip'
        mode = $Mode
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        clientRoot = $root
        files = @($entries | ForEach-Object {
                [ordered]@{
                    relativePath = $_.RelativePath.Replace('\', '/')
                    beforeSha256 = $_.Asset.Hash
                    afterSha256 = $_.TargetHash
                }
            })
    }
    [IO.File]::WriteAllText(
        (Join-Path $backupDirectory 'manifest.json'),
        ($manifest | ConvertTo-Json -Depth 5),
        $utf8NoBom)

    Assert-ClientClosed $root
    foreach ($entry in $entries) {
        [IO.File]::Copy($entry.Stage, $entry.Asset.Path, $true)
        $installed = Read-ZodiacTooltipAsset (
            $entry.Asset.Path) $entry.Asset.Locale
        if ($installed.Hash -ne $entry.TargetHash) {
            throw "Installed hash failed for $($entry.Asset.Locale)."
        }
    }
}
catch {
    $failure = $_.Exception.Message
    $rollbackFailures = @()
    foreach ($entry in $entries) {
        try {
            if (Test-Path -LiteralPath $entry.Backup -PathType Leaf) {
                [IO.File]::Copy($entry.Backup, $entry.Asset.Path, $true)
                if ((Get-Sha256 $entry.Asset.Path) -ne $entry.Asset.Hash) {
                    throw 'restored hash did not match'
                }
            }
        }
        catch {
            $rollbackFailures += "$($entry.Asset.Locale): $($_.Exception.Message)"
        }
    }
    if ($rollbackFailures.Count -gt 0) {
        throw "Zodiac tooltip install failed: $failure; rollback failed: " +
            ($rollbackFailures -join '; ')
    }
    throw "Zodiac tooltip install failed; predecessors restored: $failure"
}
finally {
    foreach ($stage in $stages) {
        Remove-Item -LiteralPath $stage -Force -ErrorAction SilentlyContinue
    }
}

[pscustomobject]@{
    Mode = $Mode
    Status = if ($wantPatched) { 'Patched' } else { 'Reverted' }
    Backup = $backupDirectory
    LevelDisplay = if ($wantPatched) { '/50' } else { '/40' }
    Type2MpDisplay = if ($wantPatched) {
        'Levels 45-50 cap at 300%'
    }
    else {
        'Original behavior restored'
    }
    Locales = $locales -join ', '
}
