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

$definitions = @(
    [pscustomobject]@{
        Locale = 'en_us'; Relative = 'Settings\Sys\Pet_Alter.xml'
        Kind = 'xml'
    },
    [pscustomobject]@{
        Locale = 'zh_cn'; Relative = 'Settings\Sys\Pet_Alter.xml'
        Kind = 'xml'
    },
    [pscustomobject]@{
        Locale = 'en_us'; Relative = 'UI\Base\LuaText.lua'
        Stock = 'BC3CDE8114EC6F94541767E7B3E9DE52E177E57A4A7697B1D2282D84354BEEB3'
        Patched = '6D1583BD8A8FAEE5F7609D05717D436D6354414CE4C5C0F58E84EE43EF0ECC33'
        AlternateStock = '2C66C4BDA2604CB211095753BDD1DA1AEB169928FB1B6330B3AB23B4D41465D9'
        AlternatePatched = '7EE7DF86612E8786E1162CA2F71E7445B1A290A0CC540209565DCDEB586842AE'
        Kind = 'en_lua'
    },
    [pscustomobject]@{
        Locale = 'zh_cn'; Relative = 'UI\Base\LuaText.lua'
        Stock = 'ED52897D10595EC04F196D823CDA716040A2B46B5DF2814C317616CE47280DB4'
        Patched = 'EECE12008019B9F0CC1AABB54214EDADC48471B3BF7932E8A82B0E71A5CD6A12'
        AlternateStock = 'E9E3C0887B7F26D76F6070A3221FFD78F15857B0C0615A39FD95855F859B98E7'
        AlternatePatched = '1E3AE6AD950CBA8FB33F075ECA017E44CBEEA67BA9E5AD6C27EBCE6C02A96F8B'
        Kind = 'zh_lua'
    },
    [pscustomobject]@{
        Locale = 'en_us'; Relative = 'UI\XML\HelpSystemSkillConfig.lua'
        Stock = '15E76D6B11F1BD8078A38CA0B125285330E8671672A1E5472A7EFC23CEB890AE'
        Patched = '521687B1D5BFE70077755FEB04BA5B4520E9E7904382E0BE9E366F21BEF4B5C4'
        Kind = 'help_lua'
    },
    [pscustomobject]@{
        Locale = 'zh_cn'; Relative = 'UI\XML\HelpSystemSkillConfig.lua'
        Stock = '15E76D6B11F1BD8078A38CA0B125285330E8671672A1E5472A7EFC23CEB890AE'
        Patched = '521687B1D5BFE70077755FEB04BA5B4520E9E7904382E0BE9E366F21BEF4B5C4'
        Kind = 'help_lua'
    }
)

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

function Replace-Exact(
    [string]$Text,
    [string]$Before,
    [string]$After,
    [int]$Count,
    [string]$Label
) {
    $actual = [regex]::Matches(
        $Text,
        [regex]::Escape($Before)).Count
    $afterCount = [regex]::Matches(
        $Text,
        [regex]::Escape($After)).Count
    if ($actual -ne $Count -or $afterCount -ne 0) {
        throw "$Label is missing, duplicated, or partially patched " +
            "(source=$actual target=$afterCount expected=$Count)."
    }
    $Text.Replace($Before, $After)
}

function Convert-Resource(
    [string]$Text,
    [string]$Kind,
    [string]$TargetState
) {
    $forward = $TargetState -eq 'Patched'
    if ($Kind -eq 'xml') {
        $stock = 'PetLv="50,80,100,110,120,120,120,120,120,120"'
        $patched = 'PetLv="30,80,100,110,120,120,120,120,120,120"'
        return Replace-Exact $Text `
            $(if ($forward) { $stock } else { $patched }) `
            $(if ($forward) { $patched } else { $stock }) `
            1 'rebirth XML ladder'
    }

    if ($Kind -eq 'zh_lua') {
        $stock = [regex]::Unescape(
            '\u5ba0\u7269\u7b2c\u4e00\u6b21\u8f6c\u751f' +
            '\uff0c\u9700\u8981\u8fbe\u523050\u7ea7')
        $patched = [regex]::Unescape(
            '\u5ba0\u7269\u7b2c\u4e00\u6b21\u8f6c\u751f' +
            '\uff0c\u9700\u8981\u8fbe\u523030\u7ea7')
        return Replace-Exact $Text `
            $(if ($forward) { $stock } else { $patched }) `
            $(if ($forward) { $patched } else { $stock }) `
            3 'Chinese rebirth instructions'
    }

    if ($Kind -eq 'help_lua') {
        $stock = 'The first rebirth requires lvl 50.'
        $patched = 'The first rebirth requires lvl 30.'
        return Replace-Exact $Text `
            $(if ($forward) { $stock } else { $patched }) `
            $(if ($forward) { $patched } else { $stock }) `
            1 'rebirth help instructions'
    }

    $pairs = @(
        @(
            'The first rebirth requires lvl 50.',
            'The first rebirth requires lvl 30.'),
        @(
            'The first rebirth requires Level 50.',
            'The first rebirth requires Level 30.'),
        @(
            'The first rebirth for your pet requires Level 50!',
            'The first rebirth for your pet requires Level 30!')
    )
    foreach ($pair in $pairs) {
        $Text = Replace-Exact $Text `
            $(if ($forward) { $pair[0] } else { $pair[1] }) `
            $(if ($forward) { $pair[1] } else { $pair[0] }) `
            1 'English rebirth instructions'
    }
    $Text
}

function Assert-ClientClosed([string]$ExecutablePath) {
    $resolved = [IO.Path]::GetFullPath($ExecutablePath)
    $liveDefault = [IO.Path]::GetFullPath('C:\Godswar Origin\Origin.exe')
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
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
                throw 'Close Origin.exe before changing the rebirth policy.'
            }
        }
        finally { $process.Dispose() }
    }
}

$records = foreach ($definition in $definitions) {
    $path = Join-Path $ClientRoot (
        "Localization\$($definition.Locale)\$($definition.Relative)")
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required rebirth resource is missing: $path"
    }
    $hash = Get-Sha256 $path
    $petAlterState = if ($definition.Kind -eq 'xml') {
        Resolve-RebornPetAlterState $path
    }
    else { $null }
    $resourceVariants = @(if ($definition.Kind -ne 'xml') {
        @([pscustomobject]@{
            Stock = $definition.Stock; Patched = $definition.Patched
        })
    }
    else { @() })
    if ($definition.PSObject.Properties['AlternateStock']) {
        $resourceVariants += [pscustomobject]@{
            Stock = $definition.AlternateStock
            Patched = $definition.AlternatePatched
        }
    }
    $resourceVariant = @($resourceVariants | Where-Object {
        $_.Stock -ceq $hash -or $_.Patched -ceq $hash
    })
    $state = if ($definition.Kind -eq 'xml') {
        if ($petAlterState.Rebirth -eq 'Level30') { 'Patched' }
        else { 'Stock' }
    }
    else {
        if ($resourceVariant.Count -ne 1) {
            throw "Unsupported rebirth resource (SHA-256 $hash): $path"
        }
        if ($hash -ceq $resourceVariant[0].Stock) { 'Stock' }
        else { 'Patched' }
    }
    [pscustomobject]@{
        Definition = $definition; Path = $path; State = $state
        PetAlterState = $petAlterState; Original = $hash
        ResourceVariant = if ($resourceVariant.Count -eq 1) {
            $resourceVariant[0]
        }
        else { $null }
    }
}

$petAlterRecords = @($records | Where-Object {
    $null -ne $_.PetAlterState
})
if (@($petAlterRecords.PetAlterState.Sha256 |
        Select-Object -Unique).Count -ne 1) {
    throw 'Pet_Alter.xml locales are in a mixed policy state.'
}
$currentPetAlter = $petAlterRecords[0].PetAlterState
$states = @($records.State | Select-Object -Unique)
$previousPartial = $records.Count -eq 6 -and
    @($records[0..3] | Where-Object State -ne 'Patched').Count -eq 0 -and
    @($records[4..5] | Where-Object State -ne 'Stock').Count -eq 0
if ($states.Count -ne 1 -and -not $previousPartial) {
    throw 'Rebirth client resources are in a mixed state.'
}
$current = if ($previousPartial) { 'PreviousPartial' } else { $states[0] }
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Status = if ($current -eq 'Patched') {
            'Patched'
        }
        elseif ($current -eq 'PreviousPartial') {
            'PreviousPartial'
        }
        else { 'Ready' }
        FirstRebirthLevel = if ($current -in @(
            'Patched', 'PreviousPartial')) { 30 } else { 50 }
        Factors = $currentPetAlter.Factors
        AgilityDamageRebound = $currentPetAlter.AgilityRebound
        Resources = $records.Count
    }
    return
}

Assert-ClientClosed (Join-Path $ClientRoot 'Origin.exe')
$target = if ($Mode -eq 'Apply') { 'Patched' } else { 'Stock' }
if ($current -eq $target) {
    [pscustomobject]@{
        Status = "Already $($target.ToLowerInvariant())"
        FirstRebirthLevel = if ($target -eq 'Patched') { 30 } else { 50 }
    }
    return
}

$backupDirectory = Join-Path $BackupRoot (
    'pet-rebirth-policy-' + $Mode + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$staged = @()
try {
    foreach ($record in $records) {
        if ($record.State -eq $target) { continue }
        $name = "$($record.Definition.Locale)-" +
            ($record.Definition.Relative -replace '[\\/]', '-')
        $backup = Join-Path $backupDirectory $name
        $stage = "$($record.Path).stage-$([Guid]::NewGuid().ToString('N'))"
        Copy-Item -LiteralPath $record.Path -Destination $backup
        if ((Get-Sha256 $backup) -ne (Get-Sha256 $record.Path)) {
            throw "Backup verification failed: $($record.Path)"
        }
        $file = Read-Utf8 $record.Path
        $output = Convert-Resource `
            $file.Text $record.Definition.Kind $target
        Write-Utf8 $file $stage $output
        $expected = if ($record.Definition.Kind -eq 'xml') {
            $targetRebirth = if ($target -eq 'Patched') {
                'Level30'
            }
            else { 'Level50' }
            (Find-RebornPetAlterState `
                $record.PetAlterState.Factors $targetRebirth `
                $record.PetAlterState.AgilityRebound).Sha256
        }
        elseif ($target -eq 'Patched') {
            $record.ResourceVariant.Patched
        }
        else { $record.ResourceVariant.Stock }
        if ((Get-Sha256 $stage) -ne $expected) {
            throw "Staged rebirth resource hash is not exact: $($record.Path)"
        }
        $staged += [pscustomobject]@{
            Path = $record.Path; Backup = $backup; Stage = $stage
            Expected = $expected; Original = $record.Original
        }
    }
    foreach ($record in $staged) {
        Move-Item -LiteralPath $record.Stage -Destination $record.Path -Force
    }
    foreach ($record in $staged) {
        if ((Get-Sha256 $record.Path) -ne $record.Expected) {
            throw "Installed rebirth resource hash is not exact: $($record.Path)"
        }
    }
}
catch {
    $failure = $_
    foreach ($record in $staged) {
        if (Test-Path -LiteralPath $record.Backup -PathType Leaf) {
            Copy-Item -LiteralPath $record.Backup `
                -Destination $record.Path -Force
            if ((Get-Sha256 $record.Path) -cne $record.Original) {
                throw "Rollback hash mismatch: $($record.Path)"
            }
        }
        if (Test-Path -LiteralPath $record.Stage -PathType Leaf) {
            Remove-Item -LiteralPath $record.Stage -Force
        }
    }
    throw $failure
}

[pscustomobject]@{
    Status = if ($Mode -eq 'Apply') { 'Patched' } else { 'Reverted' }
    FirstRebirthLevel = if ($target -eq 'Patched') { 30 } else { 50 }
    Backup = $backupDirectory
}
