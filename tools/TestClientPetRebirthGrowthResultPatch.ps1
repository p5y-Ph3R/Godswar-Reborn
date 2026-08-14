[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot `
    'PatchClientPetRebirthGrowthResult.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'godswar-pet-rebirth-growth-result-' + [Guid]::NewGuid().ToString('N'))
$client = Join-Path $testRoot 'client'
$backups = Join-Path $testRoot 'backups'
$locales = @('en_us', 'zh_cn')
$contracts = @{
    en_us = @{
        StockHash =
            '35B9CD0BE68E240D286681A7804EA1DDC42984BFA8703BE9AB1E16FA7AAB2791'
        StockLength = 7703
        PatchedHash =
            '1EAA3CE171E1AEDE21DD45019FA1051DAA87441757EB13C8E5D14BF302EC3119'
        PatchedLength = 7668
    }
    zh_cn = @{
        StockHash =
            '1EC127A09AF40345B8E6E6DE82F7E3F5CA44F8EF52903EDCECB2AD75035244A3'
        StockLength = 7641
        PatchedHash =
            '891610CC00F5E27DCD3C90A0814A625D839176BD86929E047062108C86109A86'
        PatchedLength = 7606
    }
}
$assertions = 0

$stockFragment = @'
function Samsera_UpdateTraitBaseCol(state, id, value)
	pElement = uiapi:GetElement(id);
	if 1 == state then
		pElement:SetText(str_random_upate);
	elseif 2 == state then
		if 1 == value then
			pElement:SetText(str_update_1);
		elseif 2 == value then
			pElement:SetText(str_update_2);
		elseif 3 == value then
			pElement:SetText(str_update_3);
		elseif 4 == value then
			pElement:SetText(str_update_4);
		elseif 5 == value then
			pElement:SetText(str_update_5);
		end
	end
end
'@ -replace "`n", "`r`n"

$patchedFragment = @'
function Samsera_UpdateTraitBaseCol(state, id, value)
	pElement = uiapi:GetElement(id);
	if 1 == state then
		pElement:SetText(str_random_upate);
	elseif 2 == state then
		-- 10273 carries exact hundredths; 1..5 mean +0.01..+0.05.
		if type(value) == "number" and value >= 0 and value <= 255 and
			value == math.floor(value) then
			pElement:SetText(string.format("+%.2f", value / 100));
		else
			pElement:SetText(str_random_upate);
		end
	end
end
'@ -replace "`n", "`r`n"

function Assert-True([bool]$Value, [string]$Label) {
    if (-not $Value) { throw "Assertion failed: $Label" }
    $script:assertions++
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Assert-SameBytes(
    [byte[]]$Actual,
    [byte[]]$Expected,
    [string]$Label
) {
    Assert-True (
        [Linq.Enumerable]::SequenceEqual($Actual, $Expected)) $Label
}

function Lua-Path([string]$Locale) {
    Join-Path $client "Localization\$Locale\UI\XML\PetSamsaraUI.lua"
}

function Xml-Path([string]$Locale) {
    Join-Path $client "Localization\$Locale\UI\XML\PetSamsaraUI.xml"
}

function Read-LuaText([byte[]]$Bytes) {
    Assert-True ($Bytes.Length -ge 3 -and
        $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and
        $Bytes[2] -eq 0xBF) 'Lua UTF-8 BOM'
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $encoding.GetString($Bytes, 3, $Bytes.Length - 3)
}

function Utf8Bom([string]$Text) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    [byte[]]$body = $encoding.GetBytes($Text)
    [byte[]]$bytes = [byte[]]::new($body.Length + 3)
    $bytes[0] = 0xEF
    $bytes[1] = 0xBB
    $bytes[2] = 0xBF
    [Array]::Copy($body, 0, $bytes, 3, $body.Length)
    return ,$bytes
}

function Display-Hundredths([int]$Value) {
    '+' + ($Value / 100).ToString(
        '0.00', [Globalization.CultureInfo]::InvariantCulture)
}

try {
    if (-not (Test-Path -LiteralPath $FixtureRoot -PathType Container)) {
        throw "Client fixture root not found: $FixtureRoot"
    }
    [IO.Directory]::CreateDirectory($client) | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'Origin.exe') `
        -Destination (Join-Path $client 'Origin.exe')
    $fixtureHashes = @{}
    foreach ($locale in $locales) {
        $targetRoot = Join-Path $client "Localization\$locale\UI\XML"
        [IO.Directory]::CreateDirectory($targetRoot) | Out-Null
        foreach ($name in @('PetSamsaraUI.lua', 'PetSamsaraUI.xml')) {
            $source = Join-Path $FixtureRoot (
                "Localization\$locale\UI\XML\$name")
            $fixtureHashes["$locale/$name"] =
                (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
            Copy-Item -LiteralPath $source -Destination (
                Join-Path $targetRoot $name)
        }
    }

    $initial = & $patcher -ClientRoot $client -Mode Status
    if ($initial.Status -eq 'Patched') {
        & $patcher -ClientRoot $client -Mode Revert `
            -BackupRoot $backups | Out-Null
    }

    $stockBytes = @{}
    foreach ($locale in $locales) {
        $path = Lua-Path $locale
        $contract = $contracts[$locale]
        $stockBytes[$locale] = [IO.File]::ReadAllBytes($path)
        Assert-Equal (Get-FileHash $path -Algorithm SHA256).Hash `
            $contract.StockHash "$locale exact predecessor hash"
        Assert-Equal $stockBytes[$locale].Length $contract.StockLength `
            "$locale predecessor length"
        $text = Read-LuaText $stockBytes[$locale]
        Assert-Equal ([regex]::Matches(
            $text, [regex]::Escape($stockFragment)).Count) 1 `
            "$locale exact stock function"
        Assert-Equal ([regex]::Matches($text, "(?<!`r)`n").Count) 0 `
            "$locale predecessor retains CRLF"
    }

    $ready = & $patcher -ClientRoot $client -Mode Status
    Assert-Equal $ready.Status 'Ready' 'stock status'
    Assert-Equal $ready.Locales 'en_us, zh_cn' 'audited locale set'
    Assert-Equal $ready.ResultControls 6 'six result labels'
    Assert-Equal $ready.ExactHundredthsVisible $false `
        'stock labels are qualitative'

    $applied = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $applied.Status 'Patched' 'apply status'
    Assert-Equal $applied.ExactHundredthsVisible $true `
        'apply enables exact Growth display'
    Assert-Equal $applied.ValueRange '0..255 => +0.00..+2.55' `
        'declared byte display range'

    $patchedBytes = @{}
    foreach ($locale in $locales) {
        $path = Lua-Path $locale
        $contract = $contracts[$locale]
        $patchedBytes[$locale] = [IO.File]::ReadAllBytes($path)
        Assert-Equal (Get-FileHash $path -Algorithm SHA256).Hash `
            $contract.PatchedHash "$locale exact successor hash"
        Assert-Equal $patchedBytes[$locale].Length $contract.PatchedLength `
            "$locale successor length"
        $stockText = Read-LuaText $stockBytes[$locale]
        $expected = Utf8Bom $stockText.Replace(
            $stockFragment, $patchedFragment)
        Assert-SameBytes $patchedBytes[$locale] $expected `
            "$locale only the exact result function changed"
        $text = Read-LuaText $patchedBytes[$locale]
        Assert-Equal ([regex]::Matches(
            $text, [regex]::Escape($patchedFragment)).Count) 1 `
            "$locale exact patched function"
        Assert-True ($text.Contains(
            'string.format("+%.2f", value / 100)')) `
            "$locale renders exact hundredths"
        Assert-True ($text.Contains('value >= 0 and value <= 255')) `
            "$locale guards the full unsigned-byte range"
        Assert-True ($text.Contains('value == math.floor(value)')) `
            "$locale rejects fractional callback values"
        Assert-Equal ([regex]::Matches($text, "(?<!`r)`n").Count) 0 `
            "$locale successor retains CRLF"
    }

    foreach ($case in @(
            @(0, '+0.00'), @(1, '+0.01'), @(5, '+0.05'),
            @(10, '+0.10'), @(20, '+0.20'), @(99, '+0.99'),
            @(100, '+1.00'), @(255, '+2.55'))) {
        Assert-Equal (Display-Hundredths $case[0]) $case[1] `
            "byte $($case[0]) exact display"
    }

    $backupFiles = @(Get-ChildItem -LiteralPath $applied.Backup -File)
    Assert-Equal $backupFiles.Count 2 'apply creates two backups'
    foreach ($backup in $backupFiles) {
        $locale = $backup.BaseName.Split('-')[0]
        Assert-Equal (Get-FileHash $backup.FullName -Algorithm SHA256).Hash `
            $contracts[$locale].StockHash "$locale exact stock backup"
    }

    $again = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $again.Status 'Already patched' 'idempotent apply'

    [IO.File]::WriteAllBytes(
        (Lua-Path 'en_us'), $stockBytes['en_us'])
    $mixedRefused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch { $mixedRefused = $_.Exception.Message.Contains('mixed state') }
    Assert-True $mixedRefused 'mixed locale state is refused'
    [IO.File]::WriteAllBytes(
        (Lua-Path 'en_us'), $patchedBytes['en_us'])

    $zhPath = Lua-Path 'zh_cn'
    [byte[]]$corrupt = $patchedBytes['zh_cn'].Clone()
    $corrupt[300] = $corrupt[300] -bxor 1
    [IO.File]::WriteAllBytes($zhPath, $corrupt)
    $unknownRefused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch { $unknownRefused = $_.Exception.Message.Contains('Unsupported') }
    Assert-True $unknownRefused 'unknown Lua bytes are refused'
    [IO.File]::WriteAllBytes($zhPath, $patchedBytes['zh_cn'])

    $xmlPath = Xml-Path 'en_us'
    [byte[]]$xml = [IO.File]::ReadAllBytes($xmlPath)
    $xmlOriginal = $xml[400]
    $xml[400] = $xmlOriginal -bxor 1
    [IO.File]::WriteAllBytes($xmlPath, $xml)
    $xmlRefused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch { $xmlRefused = $_.Exception.Message.Contains('UI layout') }
    Assert-True $xmlRefused 'unknown result-label layout is refused'
    $xml[400] = $xmlOriginal
    [IO.File]::WriteAllBytes($xmlPath, $xml)

    $exePath = Join-Path $client 'Origin.exe'
    [byte[]]$exe = [IO.File]::ReadAllBytes($exePath)
    $nativeOriginal = $exe[0x1C21F6]
    $exe[0x1C21F6] = $nativeOriginal -bxor 1
    [IO.File]::WriteAllBytes($exePath, $exe)
    $nativeRefused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch { $nativeRefused = $_.Exception.Message.Contains('native') -or
        $_.Exception.Message.Contains('raw result-byte') }
    Assert-True $nativeRefused 'unknown native renderer is refused'
    $exe[0x1C21F6] = $nativeOriginal
    [IO.File]::WriteAllBytes($exePath, $exe)

    $reverted = & $patcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups
    Assert-Equal $reverted.Status 'Reverted' 'revert status'
    Assert-Equal $reverted.ExactHundredthsVisible $false `
        'revert restores qualitative labels'
    foreach ($locale in $locales) {
        Assert-SameBytes ([IO.File]::ReadAllBytes((Lua-Path $locale))) `
            $stockBytes[$locale] "$locale byte-exact round trip"
    }
    $againReverted = & $patcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups
    Assert-Equal $againReverted.Status 'Already stock' 'idempotent revert'

    foreach ($locale in $locales) {
        foreach ($name in @('PetSamsaraUI.lua', 'PetSamsaraUI.xml')) {
            $source = Join-Path $FixtureRoot (
                "Localization\$locale\UI\XML\$name")
            Assert-Equal (Get-FileHash $source -Algorithm SHA256).Hash `
                $fixtureHashes["$locale/$name"] `
                "$locale live fixture $name remains untouched"
        }
    }

    Write-Host (
        'Pet Rebirth exact Growth result checks passed: ' +
        "$assertions assertions.")
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTestRoot.StartsWith(
            $resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTestRoot).StartsWith(
            'godswar-pet-rebirth-growth-result-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force `
            -ErrorAction SilentlyContinue
    }
}
