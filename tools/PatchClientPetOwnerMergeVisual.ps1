[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$BackupRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'client_patch_helpers\RealmComposite.States.ps1')
. (Join-Path $PSScriptRoot 'client_patch_helpers\PetOwnerMergeVisual.Composite.ps1')
if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path $PSScriptRoot '..\backups'
}

function Convert-HexBytes([string]$Hex) {
    $value = $Hex -replace '[^0-9A-Fa-f]', ''
    if (($value.Length % 2) -ne 0) {
        throw 'Malformed owner-Merge visual hex.'
    }
    [byte[]]$result = for ($index = 0; $index -lt $value.Length;
        $index += 2) {
        [Convert]::ToByte($value.Substring($index, 2), 16)
    }
    return ,$result
}

function Test-Bytes([byte[]]$Data, [int]$Offset, [byte[]]$Expected) {
    if ($Offset -lt 0 -or $Offset + $Expected.Length -gt $Data.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) { return $false }
    }
    return $true
}

function Copy-Bytes([byte[]]$Source, [byte[]]$Destination, [int]$Offset) {
    [Array]::Copy($Source, 0, $Destination, $Offset, $Source.Length)
}

function Get-BytesSha256([byte[]]$Data) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($Data)).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-PeMetadata([byte[]]$Data) {
    if ($Data.Length -lt 0x100 -or $Data[0] -ne 0x4D -or
        $Data[1] -ne 0x5A) {
        throw 'Origin.exe does not have a valid DOS header.'
    }
    $peOffset = [BitConverter]::ToInt32($Data, 0x3C)
    if ($peOffset -lt 0x40 -or $peOffset + 24 -gt $Data.Length -or
        [BitConverter]::ToUInt32($Data, $peOffset) -ne 0x00004550) {
        throw 'Origin.exe does not have a valid PE header.'
    }
    $optionalSize = [BitConverter]::ToUInt16($Data, $peOffset + 20)
    $optionalOffset = $peOffset + 24
    $sectionCount = [BitConverter]::ToUInt16($Data, $peOffset + 6)
    $sectionTable = $optionalOffset + $optionalSize
    $sections = @()
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $offset = $sectionTable + $index * 40
        $sections += [pscustomobject]@{
            Name = [Text.Encoding]::ASCII.GetString(
                $Data[$offset..($offset + 7)]).Trim([char]0)
            VirtualAddress = [BitConverter]::ToUInt32($Data, $offset + 12)
            RawSize = [BitConverter]::ToUInt32($Data, $offset + 16)
            RawOffset = [BitConverter]::ToUInt32($Data, $offset + 20)
            Characteristics = [BitConverter]::ToUInt32($Data, $offset + 36)
        }
    }
    [pscustomobject]@{
        Machine = [BitConverter]::ToUInt16($Data, $peOffset + 4)
        OptionalMagic = [BitConverter]::ToUInt16($Data, $optionalOffset)
        ImageBase = [BitConverter]::ToUInt32($Data, $optionalOffset + 28)
        Sections = $sections
    }
}

function Resolve-ExecutableVa(
    [object]$Pe,
    [int]$Offset,
    [int]$Length,
    [string]$SectionName
) {
    foreach ($section in $Pe.Sections) {
        if ($Offset -lt $section.RawOffset -or
            $Offset + $Length -gt $section.RawOffset + $section.RawSize) {
            continue
        }
        if ($section.Name -ne $SectionName -or
            ($section.Characteristics -band 0x20000000) -eq 0) {
            throw "Owner-Merge range is not in executable $SectionName."
        }
        return [uint64]$Pe.ImageBase + $section.VirtualAddress +
            ([uint64]$Offset - $section.RawOffset)
    }
    throw 'Owner-Merge range is outside an audited PE section.'
}

function Get-RelativeTarget(
    [byte[]]$Code,
    [int]$Offset,
    [uint64]$Va
) {
    if ($Code[$Offset] -ne 0xE8) {
        throw 'Owner-Merge relative call is malformed.'
    }
    [int64]$Va + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
}

function Get-CaveXrefs(
    [byte[]]$Data,
    [object]$Pe,
    [uint64]$StartVa,
    [uint64]$EndVa
) {
    $result = @()
    foreach ($section in $Pe.Sections | Where-Object {
            ($_.Characteristics -band 0x20000000) -ne 0
        }) {
        for ($offset = [int]$section.RawOffset;
            $offset -le [int]($section.RawOffset + $section.RawSize - 5);
            $offset++) {
            if ($Data[$offset] -ne 0xE8 -and $Data[$offset] -ne 0xE9) {
                continue
            }
            $sourceVa = [uint64]$Pe.ImageBase + $section.VirtualAddress +
                ([uint64]$offset - $section.RawOffset)
            $target = [int64]$sourceVa + 5 +
                [BitConverter]::ToInt32($Data, $offset + 1)
            if ($target -ge $StartVa -and $target -lt $EndVa) {
                $result += [pscustomobject]@{ Offset = $offset; Target = $target }
            }
        }
    }
    return $result
}

function Convert-PetXml([byte[]]$Data, [ValidateSet('Source', 'Patched')]
    [string]$Target) {
    if ($Data.Length -lt 3 -or -not (Test-Bytes $Data 0 (
            Convert-HexBytes 'EF BB BF'))) {
        throw 'Pet.xml is not the audited UTF-8 BOM document.'
    }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $text = $utf8.GetString($Data, 3, $Data.Length - 3)
    $state = [pscustomobject]@{ Species = 0; Rows = 0; Openings = 0 }
    $pattern = [regex]::new(
        '<Pet(?<species>\d+)_[01]>|<PetModel Samsara="(?<s>0|8|20)"[^>\r\n]*/>')
    $unitePattern = [regex]::new('unitefile="[^"]+"')
    $converted = $pattern.Replace($text, [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        if ($match.Groups['species'].Success) {
            $state.Species = [int]$match.Groups['species'].Value
            $state.Openings++
            return $match.Value
        }
        $state.Rows++
        $samsara = [int]$match.Groups['s'].Value
        $tier = if ($Target -eq 'Patched') {
            switch ($samsara) { 0 { 1 } 8 { 2 } 20 { 3 } }
        }
        elseif ($state.Species -eq 45) {
            switch ($samsara) { 0 { 1 } 8 { 2 } 20 { 3 } }
        }
        else { 1 }
        $matches = $unitePattern.Matches($match.Value)
        if ($matches.Count -ne 1 -or $state.Species -lt 1 -or
            $state.Species -gt 45) {
            throw 'Pet.xml has an ambiguous owner-Merge effect row.'
        }
        $path = 'unitefile="\\Characters\\PetUniteEffect\\' +
            ('e_he_{0:D4}_all.gwm"' -f $tier)
        return $unitePattern.Replace($match.Value, $path, 1)
    })
    if ($state.Openings -ne 90 -or $state.Rows -ne 270) {
        throw "Pet.xml expected 90 profiles/270 rows; found " +
            "$($state.Openings)/$($state.Rows)."
    }
    $body = $utf8.GetBytes($converted)
    [byte[]]$result = [byte[]]::new($body.Length + 3)
    Copy-Bytes (Convert-HexBytes 'EF BB BF') $result 0
    Copy-Bytes $body $result 3
    return ,$result
}

function Assert-OriginClosed([string]$ExePath) {
    $live = [IO.Path]::GetFullPath('C:\Godswar Origin\Origin.exe')
    if (-not [string]::Equals(
            [IO.Path]::GetFullPath($ExePath), $live,
            [StringComparison]::OrdinalIgnoreCase)) {
        return
    }
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try { throw 'Close Origin.exe before changing its Merge visual.' }
        finally { $process.Dispose() }
    }
}

$expectedLength = 6676480
$sourceExeHash =
    'AD81A6DD10458D481E3515589C50DA9ED429946FA039F9EC958D042614EFAA1C'
$patchedExeHash =
    '74ADEEC986C7005CE1A986027AFB8AAAEEC8E4DA58CA3A28F3794E3DC14C442C'
$sourceXmlHash =
    'F575EB1D7E09AD610C6B948082FA59F94B15895C4451D47DD4F787BFD29AFBB5'
$patchedXmlHash =
    'E55050B49BB5DBED6F6A4A8D2BBB78237177A6FDA065155522034462C479748C'
$octagramXmlHash =
    'A6BBB855D8DC1092B867A9DED096C42348C991D847AB0EBB93C3127D9A8A96BE'
$stockEffect0002Hash =
    '89B98361733C4D127CEE984EACD58D7EE1DA098728672B11CB673AA5BA70A2F2'
$purpleEffect0002Hash =
    '7947392068C9FF1ED3C76973C80D37CA6B214493A8EBB90CD1329D4B5DCA7BE9'

$hook1Offset = 0x2A1729
$hook1Va = [uint64]0x006A1729
$hook2Offset = 0x2A1780
$hook2Va = [uint64]0x006A1780
$caveOffset = 0x53E580
$caveVa = [uint64]0x0093E580
$caveLength = 0xD0
$sourceHook1 = Convert-HexBytes @'
8A 88 3C 06 00 00 8A 90 68 06 00 00 8A 98 60 06
00 00 88 4C 24 0F 88 54 24 14 E8 78 D1 DF FF
'@
$patchedHook1 = Convert-HexBytes @'
E8 52 CE 29 00 90 90 90 90 90 90 90 90 90 90 90
90 90 90 90 90 90 90 90 90 90 90 90 90 90 90
'@
$sourceHook2 = Convert-HexBytes '8B 4E 1C 51 E8 F7 B6 FF FF'
$patchedHook2 = Convert-HexBytes 'E8 3B CE 29 00 90 90 90 90'
$betweenHooks = Convert-HexBytes @'
0F B6 4C 24 14 8B D0 0F B6 C3 50 8A 44 24 13 E8
74 87 00 00 85 C0 74 30 83 C0 7C 74 2B 83 78 14
00 74 25 83 78 18 10 72 05 8B 40 04 EB 03 83 C0
04 50 57 E8 C0 FF DE FF
'@
$lookupReturn = Convert-HexBytes 'C2 04 00'
$effectCreateReturn = Convert-HexBytes 'C2 08 00'
$sourceCave = [byte[]]::new($caveLength)
$patchedCave = [byte[]]::new($caveLength)
$selector = Convert-HexBytes @'
8B 4C 24 18 8A 59 08 8A 51 09 88 54 24 1A 8A 88
3C 06 00 00 8A 90 68 06 00 00 88 4C 24 13 88 54
24 18 80 FB 0C 72 0D 80 FB 0E 72 04 B3 14 EB 06
B3 08 EB 02 30 DB E8 05 03 B6 FF C3
'@
$scaler = Convert-HexBytes @'
9C 60 0F B6 44 24 3E BA 00 00 80 3F 83 F8 1E 72
19 BA 00 00 A0 3F 83 F8 3C 72 0F BA 00 00 C0 3F
83 F8 5A 72 05 BA 00 00 00 40 8B B7 2C 06 00 00
85 F6 74 08 52 52 52 E8 14 80 D4 FF 61 9D 8B 4E
1C 51 E8 79 E8 D5 FF C3
'@
Copy-Bytes $selector $patchedCave 0
Copy-Bytes $scaler $patchedCave 0x40

$handlerStack = 0
$selectorStoredOffset = $handlerStack - 4 + 0x1A
$scalerReadOffset = $handlerStack - 4 - 4 - 32 + 0x3E
$betweenHookStackDelta = -4 + 4 - 8 + 8
if ($selector.Length -ne 60 -or $scaler.Length -ne 72 -or
    -not (Test-Bytes $selector 10 (Convert-HexBytes '88 54 24 1A')) -or
    -not (Test-Bytes $scaler 2 (Convert-HexBytes '0F B6 44 24 3E')) -or
    $selectorStoredOffset -ne 0x16 -or
    $scalerReadOffset -ne $selectorStoredOffset -or
    $betweenHookStackDelta -ne 0 -or
    (Get-RelativeTarget $patchedHook1 0 $hook1Va) -ne $caveVa -or
    (Get-RelativeTarget $patchedHook2 0 $hook2Va) -ne $caveVa + 0x40 -or
    (Get-RelativeTarget $selector 54 $caveVa) -ne 0x0049E8C0 -or
    (Get-RelativeTarget $scaler 55 ($caveVa + 0x40)) -ne 0x00686610 -or
    (Get-RelativeTarget $scaler 66 ($caveVa + 0x40)) -ne 0x0069CE80) {
    throw 'Internal owner-Merge visual code invariants are invalid.'
}

$root = [IO.Path]::GetFullPath($ClientRoot)
$exePath = Join-Path $root 'Origin.exe'
$xmlPaths = @('en_us', 'zh_cn') | ForEach-Object {
    Join-Path $root "Localization\$_\Settings\Sys\Pet.xml"
}
$assetSpecs = @(
    [pscustomobject]@{
        Name = 'e_he_0001_all.gwm'
        Length = 71253
        Hashes = @(
            '042627ABE2A78EF62FD83F1D622E9868282153EF480278B6ECAEC94F2A7190C1')
    },
    [pscustomobject]@{
        Name = 'e_he_0002_all.gwm'
        Length = 43083
        Hashes = @($stockEffect0002Hash, $purpleEffect0002Hash)
    },
    [pscustomobject]@{
        Name = 'e_he_0003_all.gwm'
        Length = 31091
        Hashes = @(
            'D46D3741FBFCBB0E393B758F0B8674782032672CAB3CB49C8E671DFF974937D2')
    }
)
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Origin client was not found: $exePath"
}
foreach ($path in $xmlPaths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing client Pet.xml: $path"
    }
}
foreach ($spec in $assetSpecs) {
    $path = Join-Path $root "Characters\PetUniteEffect\$($spec.Name)"
    $hash = if (Test-Path -LiteralPath $path -PathType Leaf) {
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    else { '' }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path).Length -ne $spec.Length -or
        $hash -notin $spec.Hashes) {
        throw "Owner-Merge effect asset changed or is missing: $($spec.Name)"
    }
}
$effect0002Hash = (Get-FileHash -LiteralPath (Join-Path $root (
        'Characters\PetUniteEffect\e_he_0002_all.gwm')) -Algorithm SHA256).Hash
$effect0002Palette = if ($effect0002Hash -eq $purpleEffect0002Hash) {
    'Purple'
}
else { 'Stock' }

[byte[]]$exe = [IO.File]::ReadAllBytes($exePath)
if ($exe.Length -ne $expectedLength) {
    throw "Unsupported Origin.exe length $($exe.Length)."
}
if (-not (Test-Bytes $exe 0x2A1748 $betweenHooks) -or
    -not (Test-Bytes $exe 0x2A9FF1 $lookupReturn) -or
    -not (Test-Bytes $exe 0x9198E $effectCreateReturn)) {
    throw 'Origin.exe does not preserve the audited balanced hook stack.'
}
$pe = Get-PeMetadata $exe
if ($pe.Machine -ne 0x014C -or $pe.OptionalMagic -ne 0x010B -or
    $pe.ImageBase -ne 0x00400000 -or
    (Resolve-ExecutableVa $pe $hook1Offset $sourceHook1.Length '.text') -ne
        $hook1Va -or
    (Resolve-ExecutableVa $pe $hook2Offset $sourceHook2.Length '.text') -ne
        $hook2Va -or
    (Resolve-ExecutableVa $pe $caveOffset $caveLength '.rdata') -ne
        $caveVa) {
    throw 'Origin.exe is not the audited fixed-base x86 PE32 layout.'
}
$exeHash = Get-BytesSha256 $exe
$compositeStates = Get-RealmCompositeStateMap
$compositeState = if ($compositeStates.ContainsKey($exeHash)) {
    $compositeStates[$exeHash]
}
else { $null }
$isSourceExe = $exeHash -eq $sourceExeHash -and
    (Test-Bytes $exe $hook1Offset $sourceHook1) -and
    (Test-Bytes $exe $hook2Offset $sourceHook2) -and
    (Test-Bytes $exe $caveOffset $sourceCave)
$isPatchedExe = $null -ne $compositeState -and
    (Test-Bytes $exe $hook1Offset $patchedHook1) -and
    ($compositeState.OctagramPatched -or
     ((Test-Bytes $exe $hook2Offset $patchedHook2) -and
      (Test-Bytes $exe $caveOffset $patchedCave)))
if (-not $isSourceExe -and -not $isPatchedExe) {
    throw "Unsupported or partial Origin.exe state (SHA-256 $exeHash)."
}
$xrefs = @(Get-CaveXrefs $exe $pe $caveVa ($caveVa + $caveLength))
if (($isSourceExe -and $xrefs.Count -ne 0) -or
    ($isPatchedExe -and
     ($xrefs.Count -ne 2 -or
      $xrefs[0].Offset -ne $hook1Offset -or
      $xrefs[0].Target -ne $caveVa -or
      $xrefs[1].Offset -ne $hook2Offset -or
      $xrefs[1].Target -ne $caveVa +
        $(if ($compositeState.OctagramPatched) { 0x50 } else { 0x40 })))) {
    throw 'Origin.exe failed the exact owner-Merge cave xref audit.'
}

$xmlBytes = @($xmlPaths | ForEach-Object {
    ,([IO.File]::ReadAllBytes($_))
})
$xmlHashes = @($xmlBytes | ForEach-Object { Get-BytesSha256 $_ })
$isSourceXml = @($xmlHashes | Where-Object { $_ -eq $sourceXmlHash }).Count -eq 2
$expectedPatchedXmlHash = if ($null -ne $compositeState -and
    $compositeState.OctagramPatched) {
    $octagramXmlHash
}
else { $patchedXmlHash }
$isPatchedXml = @($xmlHashes | Where-Object {
        $_ -eq $expectedPatchedXmlHash
    }).Count -eq 2
if ((-not $isSourceXml -and -not $isPatchedXml) -or
    $isSourceExe -ne $isSourceXml -or
    $isPatchedExe -ne $isPatchedXml) {
    throw 'Owner-Merge executable and Pet.xml files are in mixed states.'
}
$state = if ($isPatchedExe) { 'Patched' } else { 'Source' }
$manualPatched = $isPatchedExe -and $compositeState.ManualPatched
$backPatched = $isPatchedExe -and $compositeState.GuardPatched
$octagramPatched = $isPatchedExe -and $compositeState.OctagramPatched
Assert-PetOwnerMergeVisualOctagramAssetState $root $octagramPatched

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = $state
        ExeHash = $exeHash
        PetXmlHash = $xmlHashes[0]
        EffectByQuality =
            '1-11:0001; 12-13:0002; 14-16:0003; 16 at rebirth 90+:0004'
        ScaleByRebirth = '<30:1.00; 30-59:1.25; 60-89:1.50; >=90:2.00'
        Cave = '0x53E580-0x53E650 (exclusive)'
        CaveInboundRelativeXrefs = $xrefs.Count
        Effect0002Palette = $effect0002Palette
        ManualRealmSelection = $manualPatched
        CharacterBackGuard = $backPatched
        PetOwnerMergeOctagram = if ($octagramPatched) {
            'Applied'
        }
        else { 'Reverted' }
        AssetsPreserved = $true
    }
    return
}

$target = if ($Mode -eq 'Apply') { 'Patched' } else { 'Source' }
if ($state -eq $target) {
    [pscustomobject]@{
        Mode = $Mode
        Status = "Already $target"
        Hash = $exeHash
        ManualRealmSelection = $manualPatched
        CharacterBackGuard = $backPatched
        PetOwnerMergeOctagram = if ($octagramPatched) {
            'Applied'
        }
        else { 'Reverted' }
    }
    return
}
if ($Mode -eq 'Revert' -and $exeHash -ne $patchedExeHash) {
    throw 'Cannot revert the base owner-Merge visual from a composite state. Revert the octagram first, then Character Back and manual realm selection in either order, then revert the base visual.'
}
Assert-OriginClosed $exePath

[byte[]]$targetExe = $exe.Clone()
Copy-Bytes $(if ($target -eq 'Patched') { $patchedHook1 } else { $sourceHook1 }) `
    $targetExe $hook1Offset
Copy-Bytes $(if ($target -eq 'Patched') { $patchedHook2 } else { $sourceHook2 }) `
    $targetExe $hook2Offset
Copy-Bytes $(if ($target -eq 'Patched') { $patchedCave } else { $sourceCave }) `
    $targetExe $caveOffset
$targetExeHash = if ($target -eq 'Patched') { $patchedExeHash } else { $sourceExeHash }
if ((Get-BytesSha256 $targetExe) -ne $targetExeHash) {
    throw 'Generated Origin.exe did not match its audited target hash.'
}
$targetXml = @($xmlBytes | ForEach-Object { Convert-PetXml $_ $target })
$targetXmlHash = if ($target -eq 'Patched') { $patchedXmlHash } else { $sourceXmlHash }
if (@($targetXml | Where-Object {
            (Get-BytesSha256 $_) -ne $targetXmlHash
        }).Count -ne 0) {
    throw 'Generated Pet.xml did not match its audited target hash.'
}

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-pet-owner-merge-visual-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$allPaths = @($exePath) + $xmlPaths
$allSource = [object[]]::new(3)
$allTarget = [object[]]::new(3)
$allSource[0] = $exe
$allTarget[0] = $targetExe
for ($index = 0; $index -lt 2; $index++) {
    $allSource[$index + 1] = $xmlBytes[$index]
    $allTarget[$index + 1] = $targetXml[$index]
}
$stages = @()
for ($index = 0; $index -lt $allPaths.Count; $index++) {
    $relative = if ($index -eq 0) { 'Origin.exe' }
        else { "Pet.$($index).xml" }
    $backup = Join-Path $backupDirectory $relative
    [IO.File]::WriteAllBytes($backup, $allSource[$index])
    if ((Get-BytesSha256 ([IO.File]::ReadAllBytes($backup))) -ne
        (Get-BytesSha256 $allSource[$index])) {
        throw "Backup verification failed: $relative"
    }
    $stage = "$($allPaths[$index]).$([guid]::NewGuid().ToString('N')).stage"
    [IO.File]::WriteAllBytes($stage, $allTarget[$index])
    $stages += $stage
}

try {
    for ($index = 0; $index -lt $allPaths.Count; $index++) {
        Move-Item -LiteralPath $stages[$index] `
            -Destination $allPaths[$index] -Force
    }
    if ((Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash -ne
            $targetExeHash) {
        throw 'Installed Origin.exe hash did not match the target.'
    }
    foreach ($path in $xmlPaths) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne
            $targetXmlHash) {
            throw "Installed Pet.xml hash did not match: $path"
        }
    }
}
catch {
    for ($index = 0; $index -lt $allPaths.Count; $index++) {
        [IO.File]::WriteAllBytes($allPaths[$index], $allSource[$index])
    }
    foreach ($stage in $stages) {
        if (Test-Path -LiteralPath $stage) {
            Remove-Item -LiteralPath $stage -Force
        }
    }
    throw
}

[pscustomobject]@{
    Mode = $Mode
    Status = $target
    ExeHash = $targetExeHash
    PetXmlHash = $targetXmlHash
    BackupDirectory = $backupDirectory
    Effect0002Palette = $effect0002Palette
    ManualRealmSelection = $false
    CharacterBackGuard = $false
    PetOwnerMergeOctagram = 'Reverted'
    AssetsPreserved = $true
}
