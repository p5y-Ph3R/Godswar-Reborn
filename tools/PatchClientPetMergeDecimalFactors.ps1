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
$supportedOriginSha256 = @(
    '9354BDB00376E16F5C2D1E682637790D90C3930B8F3655456F8F49F3314C6728',
    '31B4CE0E0445958C7814BCD2572381F9115DE194E0E13CB3ED7502F02C9FB9B2',
    'C642C3F9F4F3458BC4DBAD126E06C1661C7F1C418FB63BD037543CA1892D5656',
    '7B837397F5387186001B7CB155FBADD2B3AA2CA425B7568A21F9C66EDA90A8DA',
    # Current character-stat-display successor. That patch does not alter
    # Pet_Alter.xml or the Pet Unite resource parser used by this tool.
    'FB634307517770ED8C677503C7D6F9E0E51A5995AFAF1A9D19631F1EFE1B6683'
)

function Read-TextFile([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $reader = [IO.StreamReader]::new($Path, $true)
    try {
        $text = $reader.ReadToEnd()
        $preamble = $reader.CurrentEncoding.GetPreamble()
        $hasPreamble = $preamble.Length -gt 0 -and
            $bytes.Length -ge $preamble.Length
        for ($index = 0; $hasPreamble -and
            $index -lt $preamble.Length; $index++) {
            $hasPreamble = $bytes[$index] -eq $preamble[$index]
        }
        return [pscustomobject]@{
            Text = $text
            Encoding = $reader.CurrentEncoding
            HasPreamble = $hasPreamble
            NewLine = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        }
    }
    finally {
        $reader.Dispose()
    }
}

function Write-TextFile($File, [string]$Path, [string]$Text) {
    [byte[]]$preamble = if ($File.HasPreamble) {
        $File.Encoding.GetPreamble()
    }
    else {
        @()
    }
    [byte[]]$body = $File.Encoding.GetBytes($Text)
    [byte[]]$output = [byte[]]::new($preamble.Length + $body.Length)
    [Array]::Copy($preamble, 0, $output, 0, $preamble.Length)
    [Array]::Copy($body, 0, $output, $preamble.Length, $body.Length)
    [IO.File]::WriteAllBytes($Path, $output)
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Replace-Exact(
    [string]$Text,
    [string]$Before,
    [string]$After,
    [int]$ExpectedCount,
    [string]$Label
) {
    $beforeCount = [regex]::Matches(
        $Text, [regex]::Escape($Before)).Count
    $afterCount = [regex]::Matches(
        $Text, [regex]::Escape($After)).Count
    if ($beforeCount -ne $ExpectedCount -or $afterCount -ne 0) {
        throw "$Label is missing, duplicated, or partially patched."
    }
    return $Text.Replace($Before, $After)
}

function Convert-Factors([string]$Text, [string]$TargetState) {
    if ($TargetState -eq 'Patched') {
        $Text = Replace-Exact $Text 'Values="1.4"' `
            'Values="1.4001"' 2 '1.4 species factors'
        return Replace-Exact $Text 'Values="2.6"' `
            'Values="2.6001"' 39 '2.6 species factors'
    }
    $Text = Replace-Exact $Text 'Values="1.4001"' `
        'Values="1.4"' 2 '1.4001 compatibility factors'
    return Replace-Exact $Text 'Values="2.6001"' `
        'Values="2.6"' 39 '2.6001 compatibility factors'
}

function Assert-Structure([string]$Text, [string]$TargetState) {
    try { [xml]$document = $Text }
    catch { throw "Pet_Alter.xml is invalid XML: $($_.Exception.Message)" }
    $config = $document.SelectSingleNode(
        '/Alter/Inosculate/Config/Inosculate')
    $lookup = @($document.SelectNodes('/Alter/Inosculate/Restrict/*'))
    $factors = @($document.SelectNodes('/Alter/Inosculate/typePoint/*'))
    if ($null -eq $config -or $config.Modulus -ne '5' -or
        $config.Min_Alter -ne '10,20,30,40,50' -or
        $config.Max_Alter -ne '100,100,100,100,100' -or
        $lookup.Count -ne 200 -or $factors.Count -ne 45) {
        throw 'Pet_Alter.xml Merge structure is not the reviewed build.'
    }
    $expected14 = if ($TargetState -eq 'Patched') { '1.4001' } else { '1.4' }
    $expected26 = if ($TargetState -eq 'Patched') { '2.6001' } else { '2.6' }
    $values14 = @($factors | Where-Object { $_.Values -eq $expected14 })
    $values26 = @($factors | Where-Object { $_.Values -eq $expected26 })
    $values08 = @($factors | Where-Object { $_.Values -eq '0.8' })
    if ($values14.Count -ne 2 -or $values26.Count -ne 39 -or
        $values08.Count -ne 4) {
        throw 'Pet_Alter.xml species factors do not match the target state.'
    }
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
                throw 'Close Origin.exe before changing Pet Merge factors.'
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
foreach ($path in @($originPath) + $paths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Pet Merge file is missing: $path"
    }
}
$originHash = Get-Sha256 $originPath
if ($originHash -notin $supportedOriginSha256) {
    throw "Unsupported Origin.exe build (SHA-256 $originHash)."
}

$states = @($paths | ForEach-Object { Resolve-RebornPetAlterState $_ })
if (@($states.Sha256 | Select-Object -Unique).Count -ne 1) {
    throw 'Pet_Alter.xml locales are in a mixed state.'
}
$currentState = $states[0]
foreach ($path in $paths) {
    $file = Read-TextFile $path
    if ($file.Encoding.WebName -ne 'utf-8' -or
        -not $file.HasPreamble -or $file.NewLine -ne "`r`n") {
        throw "Pet_Alter.xml encoding/newlines are unsupported: $path"
    }
    Assert-Structure $file.Text $currentState.Factors
}

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Status = if ($currentState.Factors -eq 'Patched') {
            'Patched'
        } else { 'Ready' }
        Factors = if ($currentState.Factors -eq 'Patched') {
            'decimal-compatible'
        } else { 'stock-binary32' }
        Rebirth = $currentState.Rebirth
        AgilityDamageRebound = $currentState.AgilityRebound
        Locales = $locales -join ', '
    }
    return
}

Assert-ClientClosed $originPath
$targetState = if ($Mode -eq 'Apply') { 'Patched' } else { 'Stock' }
if ($currentState.Factors -eq $targetState) {
    [pscustomobject]@{
        Status = if ($Mode -eq 'Apply') { 'Already patched' } else {
            'Already reverted'
        }
        Locales = $locales -join ', '
    }
    return
}
$target = Find-RebornPetAlterState `
    $targetState $currentState.Rebirth $currentState.AgilityRebound

$stamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
$backupDirectory = Join-Path $BackupRoot (
    "pet-merge-decimal-factors-$Mode-$stamp-" +
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
        if ((Get-Sha256 $backup) -ne (Get-Sha256 $path)) {
            throw "Backup verification failed: $path"
        }
        $file = Read-TextFile $path
        $output = Convert-Factors $file.Text $targetState
        Assert-Structure $output $targetState
        Write-TextFile $file $stage $output
        $expected = $target.Sha256
        if ((Get-Sha256 $stage) -ne $expected) {
            throw "Staged Pet_Alter.xml hash is not exact: $path"
        }
        $records += [pscustomobject]@{
            Path = $path; Backup = $backup; Stage = $stage
            Expected = $expected
        }
    }
    foreach ($record in $records) {
        Move-Item -LiteralPath $record.Stage `
            -Destination $record.Path -Force
    }
    foreach ($record in $records) {
        if ((Get-Sha256 $record.Path) -ne $record.Expected) {
            throw "Installed Pet_Alter.xml hash is not exact: $($record.Path)"
        }
    }
}
catch {
    $installError = $_
    $rollbackErrors = @()
    foreach ($record in $records) {
        try {
            if (Test-Path -LiteralPath $record.Backup -PathType Leaf) {
                Copy-Item -LiteralPath $record.Backup `
                    -Destination $record.Path -Force
                if ((Get-Sha256 $record.Path) -ne $currentState.Sha256) {
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
        throw "$($installError.Exception.Message) Rollback failed: " +
            ($rollbackErrors -join '; ')
    }
    throw $installError
}

[pscustomobject]@{
    Status = if ($Mode -eq 'Apply') { 'Patched' } else { 'Reverted' }
    Backup = $backupDirectory
    Locales = $locales -join ', '
}
