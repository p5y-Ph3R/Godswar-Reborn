[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

    [ValidateSet('Apply', 'Revert', 'Status')]
    [string]$Mode = 'Status',

    [string]$BackupRoot = (Join-Path $PSScriptRoot '..\backups')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'client_patch_helpers\PetAlter.States.ps1')

$locales = @('en_us', 'zh_cn')
$enabledElement = '<Effect3 Effect="38" Restrict="0,60,150,300,600" ' +
    'Values="1.5,1.2,1,0.8,0.7"/>'
$disabledElement = '<Effect3 Effect="38" Restrict="0,60,150,300,600" ' +
    'Values="0,0,0,0,0"/>'

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Read-Utf8([string]$Path) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    [pscustomobject]@{
        Text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
        HasBom = $hasBom
    }
}

function Write-Utf8($File, [string]$Path, [string]$Text) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    [byte[]]$body = $encoding.GetBytes($Text)
    $offset = if ($File.HasBom) { 3 } else { 0 }
    [byte[]]$bytes = [byte[]]::new($body.Length + $offset)
    if ($File.HasBom) {
        $bytes[0] = 0xEF; $bytes[1] = 0xBB; $bytes[2] = 0xBF
    }
    [Array]::Copy($body, 0, $bytes, $offset, $body.Length)
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Assert-UniteStructure(
    [string]$Text,
    [string]$AgilityRebound
) {
    try { [xml]$document = $Text }
    catch { throw "Pet_Alter.xml is invalid XML: $($_.Exception.Message)" }

    $traits = @($document.SelectNodes('/Alter/Unite/Trait'))
    $agility = @($document.SelectNodes(
        '/Alter/Unite/Trait[@Type="1"]/*[@Effect="38"]'))
    $luck = @($document.SelectNodes(
        '/Alter/Unite/Trait[@Type="6"]/*[@Effect="38"]'))
    $base = @($document.SelectNodes(
        '/Alter/Unite/Base/*[@Effect="38"]'))
    $agilityValues = if ($AgilityRebound -eq 'Disabled') {
        '0,0,0,0,0'
    }
    else { '1.5,1.2,1,0.8,0.7' }

    if ($traits.Count -ne 6 -or $agility.Count -ne 1 -or
        $agility[0].Name -cne 'Effect3' -or
        $agility[0].Restrict -cne '0,60,150,300,600' -or
        $agility[0].Values -cne $agilityValues) {
        throw 'Pet_Alter.xml Agility rebound curve is not the reviewed state.'
    }
    if ($luck.Count -ne 1 -or $luck[0].Name -cne 'Effect3' -or
        $luck[0].Restrict -cne '0,60,150,300,600' -or
        $luck[0].Values -cne '6,4.8,3.9,3.3,2.7') {
        throw 'Pet_Alter.xml Luck rebound curve changed unexpectedly.'
    }
    if ($base.Count -ne 1 -or $base[0].Values -cne '150') {
        throw 'Pet_Alter.xml base rebound value changed unexpectedly.'
    }
}

function Convert-AgilityRebound(
    [string]$Text,
    [string]$TargetState
) {
    $before = if ($TargetState -eq 'Disabled') {
        $enabledElement
    }
    else { $disabledElement }
    $after = if ($TargetState -eq 'Disabled') {
        $disabledElement
    }
    else { $enabledElement }
    $beforeCount = [regex]::Matches(
        $Text, [regex]::Escape($before)).Count
    $afterCount = [regex]::Matches(
        $Text, [regex]::Escape($after)).Count
    if ($beforeCount -ne 1 -or $afterCount -ne 0) {
        throw 'Agility rebound curve is missing, duplicated, or partially patched.'
    }
    $Text.Replace($before, $after)
}

function Assert-ClientClosed([string]$ExecutablePath) {
    $resolved = [IO.Path]::GetFullPath($ExecutablePath)
    $liveDefault = [IO.Path]::GetFullPath('C:\Godswar Origin\Origin.exe')
    foreach ($process in @(
            Get-Process -Name Origin -ErrorAction SilentlyContinue)) {
        try {
            $processPath = $null
            try { $processPath = $process.Path } catch {}
            $same = -not [string]::IsNullOrWhiteSpace($processPath) -and
                [string]::Equals(
                    [IO.Path]::GetFullPath($processPath), $resolved,
                    [StringComparison]::OrdinalIgnoreCase)
            $hiddenLive = [string]::IsNullOrWhiteSpace($processPath) -and
                [string]::Equals(
                    $resolved, $liveDefault,
                    [StringComparison]::OrdinalIgnoreCase)
            if ($same -or $hiddenLive) {
                throw 'Close Origin.exe before changing Pet Unite rebound.'
            }
        }
        finally { $process.Dispose() }
    }
}

$originPath = Join-Path $ClientRoot 'Origin.exe'
$paths = foreach ($locale in $locales) {
    Join-Path $ClientRoot (
        "Localization\$locale\Settings\Sys\Pet_Alter.xml")
}
foreach ($path in $paths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Pet Unite resource is missing: $path"
    }
}

$states = @($paths | ForEach-Object {
    Resolve-RebornPetAlterState $_
})
if (@($states.Sha256 | Select-Object -Unique).Count -ne 1) {
    throw 'Pet_Alter.xml locales are in a mixed state.'
}
$current = $states[0]
foreach ($path in $paths) {
    $file = Read-Utf8 $path
    if (-not $file.HasBom -or -not $file.Text.Contains("`r`n")) {
        throw "Pet_Alter.xml encoding/newlines are unsupported: $path"
    }
    Assert-UniteStructure $file.Text $current.AgilityRebound
}

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Status = if ($current.AgilityRebound -eq 'Disabled') {
            'Patched'
        }
        else { 'Ready' }
        AgilityDamageRebound = $current.AgilityRebound
        LuckDamageRebound = 'Enabled'
        Factors = $current.Factors
        Rebirth = $current.Rebirth
        Locales = $locales -join ', '
    }
    return
}

Assert-ClientClosed $originPath
$targetRebound = if ($Mode -eq 'Apply') { 'Disabled' } else { 'Enabled' }
if ($current.AgilityRebound -eq $targetRebound) {
    [pscustomobject]@{
        Status = if ($Mode -eq 'Apply') {
            'Already patched'
        }
        else { 'Already reverted' }
        AgilityDamageRebound = $targetRebound
        LuckDamageRebound = 'Enabled'
        Locales = $locales -join ', '
    }
    return
}

$target = Find-RebornPetAlterState `
    $current.Factors $current.Rebirth $targetRebound
$backupDirectory = Join-Path $BackupRoot (
    'pet-merge-agility-rebound-' + $Mode + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$records = @()
try {
    foreach ($path in $paths) {
        $locale = Split-Path (Split-Path (Split-Path (
            Split-Path $path -Parent) -Parent) -Parent) -Leaf
        $backup = Join-Path $backupDirectory "$locale-Pet_Alter.xml"
        $stage = "$path.stage-$([Guid]::NewGuid().ToString('N'))"
        Copy-Item -LiteralPath $path -Destination $backup
        if ((Get-Sha256 $backup) -cne $current.Sha256) {
            throw "Backup verification failed: $path"
        }
        $record = [pscustomobject]@{
            Path = $path; Backup = $backup; Stage = $stage
            Original = $current.Sha256; Expected = $target.Sha256
        }
        $records += $record
        $file = Read-Utf8 $path
        $output = Convert-AgilityRebound $file.Text $targetRebound
        Assert-UniteStructure $output $targetRebound
        Write-Utf8 $file $stage $output
        if ((Get-Sha256 $stage) -cne $target.Sha256) {
            throw "Staged Pet_Alter.xml hash is not exact: $path"
        }
    }
    foreach ($record in $records) {
        if ((Get-Sha256 $record.Path) -cne $record.Original) {
            throw "Pet_Alter.xml changed while the patch was staged: $($record.Path)"
        }
    }
    foreach ($record in $records) {
        Move-Item -LiteralPath $record.Stage -Destination $record.Path -Force
    }
    foreach ($record in $records) {
        if ((Get-Sha256 $record.Path) -cne $record.Expected) {
            throw "Installed Pet_Alter.xml hash is not exact: $($record.Path)"
        }
    }
}
catch {
    $failure = $_
    $rollbackErrors = @()
    foreach ($record in $records) {
        try {
            if (Test-Path -LiteralPath $record.Backup -PathType Leaf) {
                Copy-Item -LiteralPath $record.Backup `
                    -Destination $record.Path -Force
                if ((Get-Sha256 $record.Path) -cne $record.Original) {
                    throw 'restored hash mismatch'
                }
            }
        }
        catch { $rollbackErrors += "$($record.Path): $($_.Exception.Message)" }
        if (Test-Path -LiteralPath $record.Stage -PathType Leaf) {
            Remove-Item -LiteralPath $record.Stage -Force
        }
    }
    if ($rollbackErrors.Count -gt 0) {
        throw "$($failure.Exception.Message) Rollback failed: " +
            ($rollbackErrors -join '; ')
    }
    throw $failure
}

[pscustomobject]@{
    Status = if ($Mode -eq 'Apply') { 'Patched' } else { 'Reverted' }
    AgilityDamageRebound = $targetRebound
    LuckDamageRebound = 'Enabled'
    Backup = $backupDirectory
    Locales = $locales -join ', '
}
