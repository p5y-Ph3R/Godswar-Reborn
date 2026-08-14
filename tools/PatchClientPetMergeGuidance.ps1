[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

    [ValidateSet('Apply', 'Revert', 'Status')]
    [string]$Mode = 'Status',

    [string]$BackupRoot = (Join-Path $PSScriptRoot '..\backups')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot `
    'client_patch_helpers\PetMergeGuidance.Resources.ps1')

$locales = @('en_us', 'zh_cn')
$supportedOriginSha256 = @(
    '9354BDB00376E16F5C2D1E682637790D90C3930B8F3655456F8F49F3314C6728',
    '31B4CE0E0445958C7814BCD2572381F9115DE194E0E13CB3ED7502F02C9FB9B2',
    'C642C3F9F4F3458BC4DBAD126E06C1661C7F1C418FB63BD037543CA1892D5656',
    # Supported successor produced by PatchClientPetSavvyGrowthRefresh.ps1.
    # That patch only extends S2C 10286 redraw behavior; it does not alter
    # the Pet Merge XML/Lua resources guarded by this tool.
    '7B837397F5387186001B7CB155FBADD2B3AA2CA425B7568A21F9C66EDA90A8DA',
    # Hidden-dialog Merge refresh successor from the same guarded patcher.
    '39CC2ECEF6F7428A5870AABB1F16567BC31B9AC671CC5189DD9F790D8FBFF89B',
    # Audited successor produced by the exact remaining-Savvy native bridge.
    'F8D832D97A1C910AF31645DBD8B6FC2BDADF4AD30196470553A8668DB81A1D17'
)
$stockResourceSha256 = @{
    Xml = 'B46C0745111009A8EAB083644C54949AAE72751D03C12CD0394DF7D46C7C1860'
    Lua = 'E364F8405056C17EA289A2088D77FD4FE1BAB126BEC5D33DA1A3CC570E1EDEF7'
}
$patchedV1ResourceSha256 = @{
    Xml = '694845F38F1C0EBA43AE7397238581A4183B9F1556C5285B2D3EF3EFBFBBCC40'
    Lua = 'D685FD3B2E935813BE21D36EBF1000CFBF4ABB6CB0D1CE2AE537414F878EFFD8'
}
$patchedV2ResourceSha256 = @{
    Xml = '805A2D16C8E681172F1A563DC5A765E36DBB64972CD33CDA7108B79B2492639A'
    Lua = 'B6295CCBFA5B84DFCED8EAFADEC9FBCA05F63A356FF28868343B72C055975201'
}
$patchedV3ResourceSha256 = @{
    Xml = '805A2D16C8E681172F1A563DC5A765E36DBB64972CD33CDA7108B79B2492639A'
    Lua = '1DD85FF64639BDD08B8BB22C12A1ED804FA09529AB89676C0EB1D0A29F79A155'
}
$patchedResourceSha256 = @{
    Xml = '805A2D16C8E681172F1A563DC5A765E36DBB64972CD33CDA7108B79B2492639A'
    Lua = '992EB5304F4A529CE170648ED11AFAD671CCA14ADE7A0AC16131E86E3DFF588F'
}

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

function Replace-Exact(
    [string]$Text,
    [string]$Before,
    [string]$After,
    [string]$Label
) {
    $beforeCount = [regex]::Matches(
        $Text, [regex]::Escape($Before)).Count
    $afterCount = [regex]::Matches(
        $Text, [regex]::Escape($After)).Count
    if ($beforeCount -ne 1 -or $afterCount -ne 0) {
        throw "$Label is missing, duplicated, or partially patched."
    }
    return $Text.Replace($Before, $After)
}

function Test-BytesEqual([string]$Left, [string]$Right) {
    return [Linq.Enumerable]::SequenceEqual(
        [IO.File]::ReadAllBytes($Left),
        [IO.File]::ReadAllBytes($Right))
}

function Assert-ClientClosed([string]$ExecutablePath) {
    $resolvedExecutable = [IO.Path]::GetFullPath($ExecutablePath)
    $liveDefault = [IO.Path]::GetFullPath('C:\Godswar Origin\Origin.exe')
    foreach ($process in @(
            Get-Process -Name Origin -ErrorAction SilentlyContinue)) {
        try {
            $processPath = $null
            try {
                $processPath = $process.Path
            }
            catch {
                # Elevated clients can hide their executable path.
            }
            $isTarget = -not [string]::IsNullOrWhiteSpace($processPath) -and
                [string]::Equals(
                    [IO.Path]::GetFullPath($processPath),
                    $resolvedExecutable,
                    [StringComparison]::OrdinalIgnoreCase)
            $isInaccessibleLiveTarget =
                [string]::IsNullOrWhiteSpace($processPath) -and
                [string]::Equals(
                    $resolvedExecutable,
                    $liveDefault,
                    [StringComparison]::OrdinalIgnoreCase)
            if ($isTarget -or $isInaccessibleLiveTarget) {
                throw 'Close Origin.exe before changing Pet Merge resources.'
            }
        }
        finally {
            $process.Dispose()
        }
    }
}

$xmlPairs = @(
    @('Rectangle="100,150,485,768"', 'Rectangle="100,150,565,768"'),
    @('BtnRect="315,13,352,50"', 'BtnRect="395,13,432,50"'),
    @('Rectangle="20,100,365,158"', 'Rectangle="20,100,445,158"'),
    @('Rectangle="25,160,370,218"', 'Rectangle="25,160,450,218"'),
    @('Rectangle="251,275,351,307"', 'Rectangle="251,275,431,307"'),
    @('Rectangle="20,300,365,433"', 'Rectangle="20,300,445,433"'),
    @('Rectangle="125,138,345,307"', 'Rectangle="125,138,425,307"')
)
foreach ($top in @(5, 25, 45, 65, 85, 105)) {
    $bottom = $top + 30
    $xmlPairs += ,@(
        "Rectangle=`"241,$top,341,$bottom`"",
        "Rectangle=`"241,$top,421,$bottom`"")
}
$alignmentXmlPairs = @(
    @('Rectangle="8,5,218,10"', 'Rectangle="8,5,298,10"'),
    @('Rectangle="110,135,220,169"', 'Rectangle="190,135,300,169"')
)

$originPath = Join-Path $ClientRoot 'Origin.exe'
$resourcePaths = foreach ($locale in $locales) {
    $directory = Join-Path $ClientRoot (
        "Localization\$locale\UI\XML")
    [pscustomobject]@{
        Locale = $locale
        XmlPath = Join-Path $directory 'PetInosculateUI.xml'
        LuaPath = Join-Path $directory 'PetInosculateUI.lua'
    }
}
foreach ($path in @($originPath) + @(
        $resourcePaths | ForEach-Object { $_.XmlPath; $_.LuaPath })) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Pet Merge client resource is missing: $path"
    }
}
$originHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $originPath).Hash
if ($originHash -notin $supportedOriginSha256) {
    throw "Unsupported Origin.exe build (SHA-256 $originHash)."
}
if ($Mode -ne 'Status') {
    Assert-ClientClosed $originPath
}

$states = foreach ($resource in $resourcePaths) {
    $xmlFile = Read-TextFile $resource.XmlPath
    $luaFile = Read-TextFile $resource.LuaPath
    $functions = New-PetMergeGuidanceLuaFunctions $luaFile.NewLine
    $stockXmlCount = 0
    $patchedXmlCount = 0
    foreach ($pair in $xmlPairs) {
        $stockXmlCount += [regex]::Matches(
            $xmlFile.Text, [regex]::Escape($pair[0])).Count
        $patchedXmlCount += [regex]::Matches(
            $xmlFile.Text, [regex]::Escape($pair[1])).Count
    }
    $stockLuaCount = [regex]::Matches(
        $luaFile.Text, [regex]::Escape($functions.Stock)).Count
    $patchedV1LuaCount = [regex]::Matches(
        $luaFile.Text, [regex]::Escape($functions.PatchedV1)).Count
    $patchedV2LuaCount = [regex]::Matches(
        $luaFile.Text, [regex]::Escape($functions.PatchedV2)).Count
    $patchedV3LuaCount = [regex]::Matches(
        $luaFile.Text, [regex]::Escape($functions.PatchedV3)).Count
    $patchedLuaCount = [regex]::Matches(
        $luaFile.Text, [regex]::Escape($functions.Patched)).Count
    $stockAlignmentCount = 0
    $patchedAlignmentCount = 0
    foreach ($pair in $alignmentXmlPairs) {
        $stockAlignmentCount += [regex]::Matches(
            $xmlFile.Text, [regex]::Escape($pair[0])).Count
        $patchedAlignmentCount += [regex]::Matches(
            $xmlFile.Text, [regex]::Escape($pair[1])).Count
    }
    $isStock = $stockXmlCount -eq $xmlPairs.Count -and
        $patchedXmlCount -eq 0 -and $stockLuaCount -eq 1 -and
        $patchedV1LuaCount -eq 0 -and $patchedV2LuaCount -eq 0 -and
        $patchedV3LuaCount -eq 0 -and $patchedLuaCount -eq 0 -and
        $stockAlignmentCount -eq $alignmentXmlPairs.Count -and
        $patchedAlignmentCount -eq 0
    $isPatchedV1 = $stockXmlCount -eq 0 -and
        $patchedXmlCount -eq $xmlPairs.Count -and $stockLuaCount -eq 0 -and
        $patchedV1LuaCount -eq 1 -and $patchedV2LuaCount -eq 0 -and
        $patchedV3LuaCount -eq 0 -and $patchedLuaCount -eq 0 -and
        $stockAlignmentCount -eq $alignmentXmlPairs.Count -and
        $patchedAlignmentCount -eq 0
    $isPatchedV2 = $stockXmlCount -eq 0 -and
        $patchedXmlCount -eq $xmlPairs.Count -and $stockLuaCount -eq 0 -and
        $patchedV1LuaCount -eq 0 -and $patchedV2LuaCount -eq 1 -and
        $patchedV3LuaCount -eq 0 -and $patchedLuaCount -eq 0 -and
        $stockAlignmentCount -eq 0 -and
        $patchedAlignmentCount -eq $alignmentXmlPairs.Count
    $isPatchedV3 = $stockXmlCount -eq 0 -and
        $patchedXmlCount -eq $xmlPairs.Count -and $stockLuaCount -eq 0 -and
        $patchedV1LuaCount -eq 0 -and $patchedV2LuaCount -eq 0 -and
        $patchedV3LuaCount -eq 1 -and $patchedLuaCount -eq 0 -and
        $stockAlignmentCount -eq 0 -and
        $patchedAlignmentCount -eq $alignmentXmlPairs.Count
    $isPatched = $stockXmlCount -eq 0 -and
        $patchedXmlCount -eq $xmlPairs.Count -and $stockLuaCount -eq 0 -and
        $patchedV1LuaCount -eq 0 -and $patchedV2LuaCount -eq 0 -and
        $patchedV3LuaCount -eq 0 -and $patchedLuaCount -eq 1 -and
        $stockAlignmentCount -eq 0 -and
        $patchedAlignmentCount -eq $alignmentXmlPairs.Count
    if (-not $isStock -and -not $isPatchedV1 -and
        -not $isPatchedV2 -and -not $isPatchedV3 -and -not $isPatched) {
        throw "$($resource.Locale) Pet Merge resources are unknown or partial."
    }
    $expectedHashes = if ($isPatched) {
        $patchedResourceSha256
    }
    elseif ($isPatchedV1) {
        $patchedV1ResourceSha256
    }
    elseif ($isPatchedV2) {
        $patchedV2ResourceSha256
    }
    elseif ($isPatchedV3) {
        $patchedV3ResourceSha256
    }
    else {
        $stockResourceSha256
    }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $resource.XmlPath).Hash -ne
            $expectedHashes.Xml -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $resource.LuaPath).Hash -ne
            $expectedHashes.Lua) {
        throw "Unsupported $($resource.Locale) Pet Merge resource SHA-256."
    }
    [pscustomobject]@{
        Locale = $resource.Locale
        XmlPath = $resource.XmlPath
        LuaPath = $resource.LuaPath
        XmlFile = $xmlFile
        LuaFile = $luaFile
        Functions = $functions
        State = if ($isPatched) {
            'Patched'
        }
        elseif ($isPatchedV1) {
            'PatchedV1'
        }
        elseif ($isPatchedV2) {
            'PatchedV2'
        }
        elseif ($isPatchedV3) {
            'PatchedV3'
        }
        else {
            'Stock'
        }
    }
}

$distinctStates = @($states.State | Sort-Object -Unique)
if ($distinctStates.Count -ne 1) {
    throw 'Pet Merge locales are not in the same stock/patched state.'
}
$isPatched = $distinctStates[0] -eq 'Patched'
$isPatchedV1 = $distinctStates[0] -eq 'PatchedV1'
$isPatchedV2 = $distinctStates[0] -eq 'PatchedV2'
$isPatchedV3 = $distinctStates[0] -eq 'PatchedV3'
$isStock = $distinctStates[0] -eq 'Stock'
$localeLabel = $locales -join ', '
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) {
            'Patched'
        }
        elseif ($isPatchedV1 -or $isPatchedV2 -or $isPatchedV3) {
            'Ready to upgrade'
        }
        else {
            'Ready to apply'
        }
        Width = if (-not $isStock) { 465 } else { 385 }
        Rule = 'exact remaining effective deputy Savvy in hundredths'
        OriginSha256 = $originHash
        Locales = $localeLabel
    }
    return
}
if (($Mode -eq 'Apply' -and $isPatched) -or
    ($Mode -eq 'Revert' -and $isStock)) {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) { 'Already patched' } else { 'Already reverted' }
        Locales = $localeLabel
    }
    return
}

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-pet-merge-guidance-' + $Mode + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
foreach ($state in $states) {
    $localeBackup = Join-Path $backupDirectory $state.Locale
    [IO.Directory]::CreateDirectory($localeBackup) | Out-Null
    $state | Add-Member BackupXml (
        Join-Path $localeBackup 'PetInosculateUI.xml')
    $state | Add-Member BackupLua (
        Join-Path $localeBackup 'PetInosculateUI.lua')
    Copy-Item -LiteralPath $state.XmlPath -Destination $state.BackupXml
    Copy-Item -LiteralPath $state.LuaPath -Destination $state.BackupLua
    if (-not (Test-BytesEqual $state.XmlPath $state.BackupXml) -or
        -not (Test-BytesEqual $state.LuaPath $state.BackupLua)) {
        throw "$($state.Locale) Pet Merge backup verification failed."
    }
}

$operations = @()
try {
    foreach ($state in $states) {
        $xml = $state.XmlFile.Text
        if (($Mode -eq 'Apply' -and $state.State -eq 'Stock') -or
            $Mode -eq 'Revert') {
            foreach ($pair in $xmlPairs) {
                $xml = if ($Mode -eq 'Apply') {
                    Replace-Exact $xml $pair[0] $pair[1] (
                        "$($state.Locale) Pet Merge XML")
                }
                else {
                    Replace-Exact $xml $pair[1] $pair[0] (
                        "$($state.Locale) Pet Merge XML")
                }
            }
        }
        $alignmentIsPatched =
            $state.State -in @('PatchedV2', 'PatchedV3', 'Patched')
        if (($Mode -eq 'Apply' -and -not $alignmentIsPatched) -or
            ($Mode -eq 'Revert' -and $alignmentIsPatched)) {
            foreach ($pair in $alignmentXmlPairs) {
                $xml = if ($Mode -eq 'Apply') {
                    Replace-Exact $xml $pair[0] $pair[1] (
                        "$($state.Locale) Pet Merge alignment XML")
                }
                else {
                    Replace-Exact $xml $pair[1] $pair[0] (
                        "$($state.Locale) Pet Merge alignment XML")
                }
            }
        }
        $lua = if ($Mode -eq 'Apply') {
            $beforeLua = switch ($state.State) {
                'Stock' { $state.Functions.Stock }
                'PatchedV1' { $state.Functions.PatchedV1 }
                'PatchedV2' { $state.Functions.PatchedV2 }
                'PatchedV3' { $state.Functions.PatchedV3 }
                default { throw "Unexpected Apply state $($state.State)." }
            }
            Replace-Exact $state.LuaFile.Text $beforeLua `
                $state.Functions.Patched "$($state.Locale) Pet Merge Lua"
        }
        else {
            $beforeLua = switch ($state.State) {
                'Patched' { $state.Functions.Patched }
                'PatchedV3' { $state.Functions.PatchedV3 }
                'PatchedV2' { $state.Functions.PatchedV2 }
                'PatchedV1' { $state.Functions.PatchedV1 }
                default { throw "Unexpected Revert state $($state.State)." }
            }
            Replace-Exact $state.LuaFile.Text $beforeLua `
                $state.Functions.Stock "$($state.Locale) Pet Merge Lua"
        }
        $stageXml = "$($state.XmlPath).$([Guid]::NewGuid().ToString('N')).stage"
        $stageLua = "$($state.LuaPath).$([Guid]::NewGuid().ToString('N')).stage"
        Write-TextFile $state.XmlFile $stageXml $xml
        Write-TextFile $state.LuaFile $stageLua $lua
        [xml](Get-Content -LiteralPath $stageXml -Raw) | Out-Null
        $operations += [pscustomobject]@{
            State = $state
            StageXml = $stageXml
            StageLua = $stageLua
        }
    }

    foreach ($operation in $operations) {
        Move-Item -LiteralPath $operation.StageXml `
            -Destination $operation.State.XmlPath -Force
        Move-Item -LiteralPath $operation.StageLua `
            -Destination $operation.State.LuaPath -Force
    }

    $expectedRectangle = if ($Mode -eq 'Apply') {
        '100,150,565,768'
    }
    else {
        '100,150,485,768'
    }
    $targetHashes = if ($Mode -eq 'Apply') {
        $patchedResourceSha256
    }
    else {
        $stockResourceSha256
    }
    foreach ($operation in $operations) {
        $state = $operation.State
        $installedXml = (Read-TextFile $state.XmlPath).Text
        $installedLua = (Read-TextFile $state.LuaPath).Text
        [xml]$parsed = $installedXml
        $expectedFunction = if ($Mode -eq 'Apply') {
            $state.Functions.Patched
        }
        else {
            $state.Functions.Stock
        }
        if ($parsed.UIConfig.BondWin.Rectangle -ne $expectedRectangle -or
            [regex]::Matches(
                $installedLua,
                [regex]::Escape($expectedFunction)).Count -ne 1) {
            throw "$($state.Locale) Pet Merge post-write verification failed."
        }
        foreach ($pair in @($xmlPairs) + @($alignmentXmlPairs)) {
            $expected = if ($Mode -eq 'Apply') { $pair[1] } else { $pair[0] }
            $other = if ($Mode -eq 'Apply') { $pair[0] } else { $pair[1] }
            if ([regex]::Matches(
                    $installedXml, [regex]::Escape($expected)).Count -ne 1 -or
                [regex]::Matches(
                    $installedXml, [regex]::Escape($other)).Count -ne 0) {
                throw "$($state.Locale) Pet Merge geometry verification failed."
            }
        }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $state.XmlPath).Hash -ne
                $targetHashes.Xml -or
            (Get-FileHash -Algorithm SHA256 -LiteralPath $state.LuaPath).Hash -ne
                $targetHashes.Lua) {
            throw "$($state.Locale) Pet Merge SHA-256 verification failed."
        }
    }
}
catch {
    $installError = $_
    try {
        foreach ($state in $states) {
            Copy-Item -LiteralPath $state.BackupXml `
                -Destination $state.XmlPath -Force
            Copy-Item -LiteralPath $state.BackupLua `
                -Destination $state.LuaPath -Force
        }
        foreach ($state in $states) {
            if (-not (Test-BytesEqual $state.XmlPath $state.BackupXml) -or
                -not (Test-BytesEqual $state.LuaPath $state.BackupLua)) {
                throw "$($state.Locale) restored files differ from backups"
            }
        }
    }
    catch {
        throw "Pet Merge install and four-file rollback failed: $installError; $_"
    }
    throw "Pet Merge install failed; all four originals restored: $installError"
}
finally {
    foreach ($operation in $operations) {
        Remove-Item -LiteralPath $operation.StageXml, $operation.StageLua `
            -Force -ErrorAction SilentlyContinue
    }
}

[pscustomobject]@{
    Mode = $Mode
    Status = if ($Mode -eq 'Apply') { 'Patched' } else { 'Reverted' }
    Width = if ($Mode -eq 'Apply') { 465 } else { 385 }
    Backup = $backupDirectory
    Rule = 'exact remaining effective deputy Savvy in hundredths'
    OriginSha256 = $originHash
    Locales = $localeLabel
}
