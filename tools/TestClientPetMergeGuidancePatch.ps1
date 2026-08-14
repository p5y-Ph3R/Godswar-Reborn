[CmdletBinding()]
param(
    [string]$FixtureRoot = 'C:\Godswar Origin'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientPetMergeGuidance.ps1'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repoRoot (
    'artifacts\pet-merge-guidance-test-' + [Guid]::NewGuid().ToString('N'))
$clientRoot = Join-Path $testRoot 'client'
$locales = @('en_us', 'zh_cn')
$backupRoot = Join-Path $testRoot 'backups'
$nativePatcher = Join-Path $PSScriptRoot `
    'PatchClientPetMergeRemainingSavvy.ps1'
$predecessorOriginSha256 =
    '39CC2ECEF6F7428A5870AABB1F16567BC31B9AC671CC5189DD9F790D8FBFF89B'
$successorOriginSha256 =
    'F8D832D97A1C910AF31645DBD8B6FC2BDADF4AD30196470553A8668DB81A1D17'
$assertions = 0

function Assert-True([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "Assertion failed: $Label" }
    $script:assertions++
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

try {
    [IO.Directory]::CreateDirectory($clientRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'Origin.exe') `
        -Destination (Join-Path $clientRoot 'Origin.exe')
    $clientOrigin = Join-Path $clientRoot 'Origin.exe'
    $resources = foreach ($locale in $locales) {
        $resourceRoot = Join-Path $clientRoot (
            "Localization\$locale\UI\XML")
        [IO.Directory]::CreateDirectory($resourceRoot) | Out-Null
        foreach ($name in @('PetInosculateUI.xml', 'PetInosculateUI.lua')) {
            Copy-Item -LiteralPath (Join-Path $FixtureRoot (
                "Localization\$locale\UI\XML\$name")) `
                -Destination (Join-Path $resourceRoot $name)
        }
        [pscustomobject]@{
            Locale = $locale
            XmlPath = Join-Path $resourceRoot 'PetInosculateUI.xml'
            LuaPath = Join-Path $resourceRoot 'PetInosculateUI.lua'
        }
    }

    $initial = & $patcher -ClientRoot $clientRoot -Mode Status
    if ($initial.Status -ne 'Ready to apply') {
        & $patcher -ClientRoot $clientRoot -Mode Revert `
            -BackupRoot $backupRoot | Out-Null
    }
    $copiedOriginSha256 = (
        Get-FileHash -Algorithm SHA256 -LiteralPath $clientOrigin).Hash
    if ($copiedOriginSha256 -eq $successorOriginSha256) {
        & $nativePatcher -ClientExe $clientOrigin -Mode Revert `
            -BackupRoot $backupRoot | Out-Null
    }
    foreach ($resource in $resources) {
        $resource | Add-Member StockXml (
            [IO.File]::ReadAllBytes($resource.XmlPath))
        $resource | Add-Member StockLua (
            [IO.File]::ReadAllBytes($resource.LuaPath))
    }

    $status = & $patcher -ClientRoot $clientRoot -Mode Status
    Assert-Equal $status.Status 'Ready to apply' 'stock status'
    Assert-Equal $status.Width 385 'stock width'
    Assert-Equal $status.Locales 'en_us, zh_cn' 'audited locale set'
    Assert-Equal $status.OriginSha256 $predecessorOriginSha256 `
        'remaining-Savvy bridge predecessor is accepted'

    $apply = & $patcher -ClientRoot $clientRoot -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $apply.Status 'Patched' 'apply status'
    Assert-Equal $apply.Width 465 'patched width'
    Assert-Equal $apply.Locales 'en_us, zh_cn' 'apply covers both locales'
    foreach ($resource in $resources) {
        $xml = Get-Content -LiteralPath $resource.XmlPath -Raw
        $lua = Get-Content -LiteralPath $resource.LuaPath -Raw
        [xml]$parsed = $xml
        Assert-True (
            $parsed.UIConfig.BondWin.Rectangle -eq '100,150,565,768') `
            "$($resource.Locale) native window is widened by 80 pixels"
        Assert-True ($xml.Contains('Rectangle="241,5,421,35"')) `
            "$($resource.Locale) Savvy result column receives extra width"
        Assert-True ($xml.Contains('Rectangle="8,5,298,10"')) `
            "$($resource.Locale) merge note uses the widened space"
        Assert-True ($xml.Contains('Rectangle="190,135,300,169"')) `
            "$($resource.Locale) merge button is centered in the widened panel"
        Assert-True ($lua.Contains(
                'function set_cantUp(eleID, remainingHundredths)')) `
            "$($resource.Locale) Lua accepts the native exact shortfall"
        Assert-True ($lua.Contains('remaining / 100')) `
            "$($resource.Locale) native hundredths become display Savvy"
        Assert-True ($lua.Contains(
                'remaining == nil and resultID ~= nil')) `
            "$($resource.Locale) one-argument native callback is decoded"
        Assert-True ($lua.Contains('encoded = -resultID')) `
            "$($resource.Locale) signed callback payload stays supported"
        Assert-True ($lua.Contains('resultID > 2147483647')) `
            "$($resource.Locale) unsigned formatter wrap is detected"
        Assert-True ($lua.Contains(
                'encoded = 4294967296 - resultID')) `
            "$($resource.Locale) unsigned callback payload is normalized"
        Assert-True ($lua.Contains('local stat = encoded % 10')) `
            "$($resource.Locale) encoded callback retains its stat marker"
        Assert-True ($lua.Contains('[6] = 861064')) `
            "$($resource.Locale) encoded callback maps all six result rows"
        Assert-True ($lua.Contains(
                '"Need " .. string.format("%.2f", remaining / 100) .. " more"')) `
            "$($resource.Locale) failed row labels the remaining amount"
        Assert-True ($lua.Contains('base - 39.90')) `
            "$($resource.Locale) omitted native argument has a safe fallback"
        Assert-True ($lua.Contains(
                'string.match(raw, "(%d+%.?%d*)%s*$")')) `
            "$($resource.Locale) rich-text Savvy is parsed from its numeric tail"
        Assert-True ($lua.Contains('"Need deputy "')) `
            "$($resource.Locale) legacy callback retains conservative guidance"
        Assert-True ($lua.Contains('"Need stronger deputy"')) `
            "$($resource.Locale) parse fallback never restores stock-only text"
        Assert-True ($lua.Contains('[861064] = 861063')) `
            "$($resource.Locale) maps all six result rows"
    }

    [xml]$petAlter = Get-Content -LiteralPath (Join-Path $FixtureRoot `
        'Localization\en_us\Settings\Sys\Pet_Alter.xml') -Raw
    $inosculate = $petAlter.Alter.Inosculate
    Assert-Equal ([int]$inosculate.Config.Inosculate.Modulus) 5 `
        'native Added-Savvy divisor'
    Assert-Equal ([int]$inosculate.Restrict.Val1.Restrict) (-4000) `
        'first fixed-hundredth preview boundary'
    Assert-Equal ([int]$inosculate.Restrict.Val1.Values) 1 `
        'first preview lookup value'
    Assert-Equal ([int]$inosculate.Restrict.Val2.Restrict) (-3990) `
        'second fixed-hundredth preview boundary'
    Assert-Equal ([decimal]$inosculate.typePoint.Type2.Values) ([decimal]0.8) `
        'stock low species multiplier'
    $joloFactor = [decimal]$inosculate.typePoint.Type7.Values
    Assert-True ($joloFactor -in @([decimal]1.4, [decimal]1.4001)) `
        'Jolo stock or binary32-corrected multiplier'
    Assert-Equal ([decimal]100.00 - [decimal]39.90) ([decimal]60.10) `
        'safe deputy suggestion remains fixed-hundredth exact'
    $primaryStrength = 15782
    $deputyStrengthBasic = 2205
    $deputyStrengthAdded = 10438
    $strengthQ = [Math]::Floor($deputyStrengthAdded / 5) -
        $primaryStrength + $deputyStrengthBasic
    $strengthRemaining = -4000 - $strengthQ
    Assert-Equal $strengthQ (-11490) `
        'Strength eligibility uses only one fifth of deputy Added Savvy'
    Assert-Equal $strengthRemaining 7490 `
        'Strength native callback reports exact remaining hundredths'
    $encodedStrengthCallback = -($strengthRemaining * 10 + 2)
    $decodedStrengthRaw = -$encodedStrengthCallback
    Assert-Equal ($decodedStrengthRaw % 10) 2 `
        'Strength callback preserves its second-stat marker'
    Assert-Equal ([Math]::Floor($decodedStrengthRaw / 10)) 7490 `
        'one-argument callback recovers exact Strength remaining hundredths'
    $unsignedStrengthCallback = 4294967296 + $encodedStrengthCallback
    Assert-Equal $unsignedStrengthCallback 4294892394 `
        'client unsigned formatter reproduces the observed wrapped callback'
    $decodedUnsignedRaw = 4294967296 - $unsignedStrengthCallback
    Assert-Equal ($decodedUnsignedRaw % 10) 2 `
        'wrapped callback recovers the Strength row marker'
    Assert-Equal ([Math]::Floor($decodedUnsignedRaw / 10)) 7490 `
        'wrapped callback recovers exact remaining hundredths'
    Assert-Equal ('Need {0:F2} more' -f ($strengthRemaining / 100)) `
        'Need 74.90 more' 'Strength callback renders the requested label'
    Assert-Equal ($deputyStrengthBasic +
            [Math]::Floor($deputyStrengthAdded / 5)) 4292 `
        'deputy effective Strength is 42.92 rather than raw total 126.43'
    $richText = '|cFFFFFFFF2658.65'
    $numericTail = [regex]::Match(
        $richText, '(\d+\.?\d*)\s*$').Groups[1].Value
    Assert-Equal ([decimal]$numericTail) ([decimal]2658.65) `
        'native color-marked Savvy retains a parseable numeric tail'

    $again = & $patcher -ClientRoot $clientRoot -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $again.Status 'Already patched' 'idempotent apply'

    . (Join-Path $PSScriptRoot `
        'client_patch_helpers\PetMergeGuidance.Resources.ps1')
    foreach ($resource in $resources) {
        $luaFile = Get-Content -LiteralPath $resource.LuaPath -Raw
        $newLine = if ($luaFile.Contains("`r`n")) { "`r`n" } else { "`n" }
        $functions = New-PetMergeGuidanceLuaFunctions $newLine
        $v3Lua = $luaFile.Replace($functions.Patched, $functions.PatchedV3)
        $encoding = [Text.UTF8Encoding]::new($true)
        [IO.File]::WriteAllText($resource.LuaPath, $v3Lua, $encoding)
    }
    $v3Status = & $patcher -ClientRoot $clientRoot -Mode Status
    Assert-Equal $v3Status.Status 'Ready to upgrade' `
        'v3 signed-only callback status'
    $v3Upgrade = & $patcher -ClientRoot $clientRoot -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $v3Upgrade.Status 'Patched' `
        'installed v3 callback upgrades to unsigned-aware guidance'
    foreach ($resource in $resources) {
        $lua = Get-Content -LiteralPath $resource.LuaPath -Raw
        Assert-True ($lua.Contains(
                'encoded = 4294967296 - resultID')) `
            "$($resource.Locale) v3 upgrade installs uint32 normalization"
    }

    foreach ($resource in $resources) {
        $luaFile = Get-Content -LiteralPath $resource.LuaPath -Raw
        $newLine = if ($luaFile.Contains("`r`n")) { "`r`n" } else { "`n" }
        $functions = New-PetMergeGuidanceLuaFunctions $newLine
        $v2Lua = $luaFile.Replace($functions.Patched, $functions.PatchedV2)
        $encoding = [Text.UTF8Encoding]::new($true)
        [IO.File]::WriteAllText($resource.LuaPath, $v2Lua, $encoding)
    }
    $v2Status = & $patcher -ClientRoot $clientRoot -Mode Status
    Assert-Equal $v2Status.Status 'Ready to upgrade' `
        'v2 conservative guidance status'
    $upgrade = & $patcher -ClientRoot $clientRoot -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $upgrade.Status 'Patched' `
        'v2 guidance upgrades to exact remaining guidance'
    foreach ($resource in $resources) {
        $lua = Get-Content -LiteralPath $resource.LuaPath -Raw
        Assert-True ($lua.Contains(
                'function set_cantUp(eleID, remainingHundredths)')) `
            "$($resource.Locale) v2 upgrade installs the callback contract"
    }

    foreach ($resource in $resources) {
        $luaFile = Get-Content -LiteralPath $resource.LuaPath -Raw
        $newLine = if ($luaFile.Contains("`r`n")) { "`r`n" } else { "`n" }
        $functions = New-PetMergeGuidanceLuaFunctions $newLine
        $v1Lua = $luaFile.Replace($functions.Patched, $functions.PatchedV1)
        $encoding = [Text.UTF8Encoding]::new($true)
        [IO.File]::WriteAllText($resource.LuaPath, $v1Lua, $encoding)
        $xml = Get-Content -LiteralPath $resource.XmlPath -Raw
        $xml = $xml.Replace(
            'Rectangle="8,5,298,10"', 'Rectangle="8,5,218,10"')
        $xml = $xml.Replace(
            'Rectangle="190,135,300,169"', 'Rectangle="110,135,220,169"')
        [IO.File]::WriteAllText($resource.XmlPath, $xml, $encoding)
    }
    $v1Status = & $patcher -ClientRoot $clientRoot -Mode Status
    Assert-Equal $v1Status.Status 'Ready to upgrade' `
        'v1 guidance status'
    $v1Upgrade = & $patcher -ClientRoot $clientRoot -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $v1Upgrade.Status 'Patched' `
        'v1 guidance and layout upgrade to exact remaining guidance'

    $revert = & $patcher -ClientRoot $clientRoot -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Equal $revert.Status 'Reverted' 'revert status'
    Assert-Equal $revert.Locales 'en_us, zh_cn' 'revert covers both locales'
    foreach ($resource in $resources) {
        Assert-True (
            [Linq.Enumerable]::SequenceEqual(
                $resource.StockXml,
                [IO.File]::ReadAllBytes($resource.XmlPath))) `
            "$($resource.Locale) XML round-trips byte exactly"
        Assert-True (
            [Linq.Enumerable]::SequenceEqual(
                $resource.StockLua,
                [IO.File]::ReadAllBytes($resource.LuaPath))) `
            "$($resource.Locale) Lua round-trips byte exactly"
    }

    $nativeApply = & $nativePatcher `
        -ClientExe $clientOrigin `
        -Mode Apply -BackupRoot $backupRoot
    Assert-Equal $nativeApply.Hash $successorOriginSha256 `
        'native remaining-Savvy bridge creates audited successor'
    $successorStatus = & $patcher -ClientRoot $clientRoot -Mode Status
    Assert-Equal $successorStatus.Status 'Ready to apply' `
        'resource guard accepts native remaining-Savvy successor'
    Assert-Equal $successorStatus.OriginSha256 $successorOriginSha256 `
        'resource guard reports native remaining-Savvy successor'

    $originBytes = [IO.File]::ReadAllBytes($clientOrigin)
    $originBytes[0] = $originBytes[0] -bxor 1
    [IO.File]::WriteAllBytes($clientOrigin, $originBytes)
    $unsupportedRefused = $false
    try {
        & $patcher -ClientRoot $clientRoot -Mode Status | Out-Null
    }
    catch {
        $unsupportedRefused = $_.Exception.Message.Contains(
            'Unsupported Origin.exe build')
    }
    Assert-True $unsupportedRefused 'unknown executable build is refused'

    Write-Host "Pet Merge guidance patch checks passed: $assertions assertions."
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
