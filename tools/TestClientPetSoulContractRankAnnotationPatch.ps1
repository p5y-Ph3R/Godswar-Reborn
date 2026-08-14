[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot `
    'PatchClientPetSoulContractRankAnnotation.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'godswar-pet-soul-rank-' + [Guid]::NewGuid().ToString('N'))
$client = Join-Path $testRoot 'client'
$backups = Join-Path $testRoot 'backups'
$stockHash =
    '90C5288452CA1B7B4944DD1FBE799FA3D828CE5C52381006B009607F4393CADD'
$patchedHash =
    'E302C6E340D16A1590C329E9E52DA300AF933696C6B945A973098C4A6966CCB4'
$bonusLine =
    "`t`t<PetPinjie2 Type=`"Text`" Texture=`"`" ID=`"871018`" " +
    "Rectangle=`"151,259,181,300`" Font=`"MainMap`" " +
    "FontColor=`"GWORed`" TextFormat=`"0`" Text=`"(8)`"/>`r`n"
$assertions = 0

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

function Read-Utf8([byte[]]$Bytes) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $encoding.GetString($Bytes)
}

function Utf8([string]$Text) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    return ,$encoding.GetBytes($Text)
}

function Resource-Path([string]$Locale) {
    Join-Path $client (
        "Localization\$Locale\UI\XML\PetIndentureUI.xml")
}

function Assert-PatchedShape([string]$Path, [string]$Locale) {
    [xml]$xml = Read-Utf8 ([IO.File]::ReadAllBytes($Path))
    Assert-Equal @($xml.SelectNodes('//*[@ID="871012"]')).Count 1 `
        "$Locale numeric Rank remains"
    Assert-Equal @($xml.SelectNodes('//*[@ID="871018"]')).Count 0 `
        "$Locale Rank annotation removed"
    foreach ($id in @(
            '872011', '872021', '872031',
            '872041', '872051', '872061')) {
        $nodes = @($xml.SelectNodes("//*[@ID='$id']"))
        Assert-Equal $nodes.Count 1 "$Locale attribute annotation $id"
        Assert-Equal $nodes[0].FontColor 'GWORed' `
            "$Locale attribute annotation $id color"
    }
}

try {
    if (-not (Test-Path -LiteralPath $FixtureRoot -PathType Container)) {
        throw "Client fixture root not found: $FixtureRoot"
    }
    [IO.Directory]::CreateDirectory($client) | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'Origin.exe') `
        -Destination (Join-Path $client 'Origin.exe')
    foreach ($locale in @('en_us', 'zh_cn')) {
        $target = Resource-Path $locale
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $FixtureRoot (
            "Localization\$locale\UI\XML\PetIndentureUI.xml")) `
            -Destination $target
    }

    $initial = & $patcher -ClientRoot $client -Mode Status
    if ($initial.Status -eq 'Patched') {
        & $patcher -ClientRoot $client -Mode Revert `
            -BackupRoot $backups | Out-Null
    }
    $stockBytes = @{}
    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Resource-Path $locale
        $stockBytes[$locale] = [IO.File]::ReadAllBytes($path)
        Assert-Equal (Get-FileHash $path -Algorithm SHA256).Hash `
            $stockHash "$locale exact predecessor hash"
        Assert-Equal $stockBytes[$locale].Length 11359 `
            "$locale predecessor length"
    }

    $ready = & $patcher -ClientRoot $client -Mode Status
    Assert-Equal $ready.Status 'Ready' 'stock status'
    Assert-Equal $ready.NumericRankPreserved $true `
        'stock numeric Rank contract'
    Assert-Equal $ready.RankBonusVisible $true `
        'stock Rank annotation status'
    Assert-Equal $ready.AttributeBonusesPreserved 6 `
        'six stock attribute annotations'

    $applied = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $applied.Status 'Patched' 'apply status'
    Assert-Equal $applied.Hash $patchedHash 'declared successor hash'
    Assert-Equal $applied.NumericRankPreserved $true `
        'apply preserves numeric Rank'
    Assert-Equal $applied.RankBonusVisible $false `
        'apply hides Rank annotation'
    Assert-Equal $applied.AttributeBonusesPreserved 6 `
        'apply preserves six attribute annotations'

    $patchedBytes = @{}
    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Resource-Path $locale
        $patchedBytes[$locale] = [IO.File]::ReadAllBytes($path)
        Assert-Equal (Get-FileHash $path -Algorithm SHA256).Hash `
            $patchedHash "$locale exact successor hash"
        Assert-Equal $patchedBytes[$locale].Length 11219 `
            "$locale successor length"
        $stockText = Read-Utf8 $stockBytes[$locale]
        Assert-Equal ([regex]::Matches(
            $stockText, [regex]::Escape($bonusLine)).Count) 1 `
            "$locale one exact source annotation"
        $expected = Utf8 $stockText.Replace($bonusLine, '')
        Assert-SameBytes $patchedBytes[$locale] $expected `
            "$locale only the exact Rank annotation line changed"
        Assert-PatchedShape $path $locale
    }
    Assert-SameBytes $patchedBytes['en_us'] $patchedBytes['zh_cn'] `
        'two locales have the same exact successor'

    $backupFiles = @(Get-ChildItem -LiteralPath $applied.Backup -File)
    Assert-Equal $backupFiles.Count 2 'apply creates two backups'
    foreach ($backup in $backupFiles) {
        Assert-Equal (Get-FileHash $backup.FullName -Algorithm SHA256).Hash `
            $stockHash "exact stock backup $($backup.Name)"
    }

    $status = & $patcher -ClientRoot $client -Mode Status
    Assert-Equal $status.Status 'Patched' 'successor status'
    Assert-Equal $status.RankBonusVisible $false `
        'successor annotation status'
    $again = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $again.Status 'Already patched' 'idempotent apply'

    [IO.File]::WriteAllBytes(
        (Resource-Path 'en_us'),
        $stockBytes['en_us'])
    $mixedRefused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch { $mixedRefused = $_.Exception.Message.Contains('mixed state') }
    Assert-True $mixedRefused 'mixed locale state is refused'
    [IO.File]::WriteAllBytes(
        (Resource-Path 'en_us'),
        $patchedBytes['en_us'])

    $zhPath = Resource-Path 'zh_cn'
    [byte[]]$corrupt = $patchedBytes['zh_cn'].Clone()
    $corrupt[250] = $corrupt[250] -bxor 1
    [IO.File]::WriteAllBytes($zhPath, $corrupt)
    $unknownRefused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch { $unknownRefused = $_.Exception.Message.Contains('Unsupported') }
    Assert-True $unknownRefused 'unknown resource bytes are refused'
    [IO.File]::WriteAllBytes($zhPath, $patchedBytes['zh_cn'])

    $exePath = Join-Path $client 'Origin.exe'
    [byte[]]$exe = [IO.File]::ReadAllBytes($exePath)
    $originalOpcode = $exe[0x1BEDF3]
    $exe[0x1BEDF3] = $originalOpcode -bxor 1
    [IO.File]::WriteAllBytes($exePath, $exe)
    $nativeRefused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch {
        $nativeRefused = $_.Exception.Message.Contains('null-guard')
    }
    Assert-True $nativeRefused 'missing native null guard is refused'
    $exe[0x1BEDF3] = $originalOpcode
    [IO.File]::WriteAllBytes($exePath, $exe)

    $reverted = & $patcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups
    Assert-Equal $reverted.Status 'Reverted' 'revert status'
    Assert-Equal $reverted.Hash $stockHash 'declared predecessor hash'
    foreach ($locale in @('en_us', 'zh_cn')) {
        Assert-SameBytes (
            [IO.File]::ReadAllBytes((Resource-Path $locale))) `
            $stockBytes[$locale] "$locale byte-exact round trip"
    }

    Write-Host (
        'Pet Soul Contract Rank annotation checks passed: ' +
        "$assertions assertions.")
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTestRoot.StartsWith(
            $resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTestRoot).StartsWith(
            'godswar-pet-soul-rank-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force `
            -ErrorAction SilentlyContinue
    }
}
