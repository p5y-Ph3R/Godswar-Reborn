function Get-PetOwnerMergeOctagramSha256([byte[]]$Data) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($Data)).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-PetOwnerMergeOctagramRelativeTarget(
    [byte[]]$Code,
    [int]$Offset,
    [uint64]$Va
) {
    if ($Offset -lt 0 -or $Offset + 5 -gt $Code.Length -or
        $Code[$Offset] -ne 0xE8) {
        throw 'Owner-Merge octagram relative call is malformed.'
    }
    return [int64]$Va + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
}

function Get-PetOwnerMergeOctagramCaveXrefs(
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
            if ($Data[$offset] -notin @(0xE8, 0xE9)) { continue }
            $sourceVa = [uint64]$Pe.ImageBase + $section.VirtualAddress +
                ([uint64]$offset - $section.RawOffset)
            $target = [int64]$sourceVa + 5 +
                [BitConverter]::ToInt32($Data, $offset + 1)
            if ($target -ge $StartVa -and $target -lt $EndVa) {
                $result += [pscustomobject]@{
                    Offset = $offset
                    Target = $target
                }
            }
        }
    }
    return $result
}

function Get-PetOwnerMergeOctagramSelector(
    [int]$Quality,
    [int]$CompletedRebirths
) {
    if ($Quality -eq 16 -and $CompletedRebirths -ge 90) { return 90 }
    if ($Quality -lt 12) { return 0 }
    if ($Quality -lt 14) { return 8 }
    return 20
}

function Get-PetOwnerMergeOctagramBinarySpec {
    $hook1Offset = 0x2A1729
    $hook1Va = [uint64]0x006A1729
    $hook2Offset = 0x2A1780
    $hook2Va = [uint64]0x006A1780
    $caveOffset = 0x53E580
    $caveVa = [uint64]0x0093E580
    $caveLength = 0xD0

    $hook1 = Convert-HexBytes @'
E8 52 CE 29 00 90 90 90 90 90 90 90 90 90 90 90
90 90 90 90 90 90 90 90 90 90 90 90 90 90 90
'@
    $oldHook2 = Convert-HexBytes 'E8 3B CE 29 00 90 90 90 90'
    $newHook2 = Convert-HexBytes 'E8 4B CE 29 00 90 90 90 90'
    $oldCave = [byte[]]::new($caveLength)
    $newCave = [byte[]]::new($caveLength)
    $oldSelector = Convert-HexBytes @'
8B 4C 24 18 8A 59 08 8A 51 09 88 54 24 1A 8A 88
3C 06 00 00 8A 90 68 06 00 00 88 4C 24 13 88 54
24 18 80 FB 0C 72 0D 80 FB 0E 72 04 B3 14 EB 06
B3 08 EB 02 30 DB E8 05 03 B6 FF C3
'@
    $oldScaler = Convert-HexBytes @'
9C 60 0F B6 44 24 3E BA 00 00 80 3F 83 F8 1E 72
19 BA 00 00 A0 3F 83 F8 3C 72 0F BA 00 00 C0 3F
83 F8 5A 72 05 BA 00 00 00 40 8B B7 2C 06 00 00
85 F6 74 08 52 52 52 E8 14 80 D4 FF 61 9D 8B 4E
1C 51 E8 79 E8 D5 FF C3
'@
    $newSelector = Convert-HexBytes @'
8B 4C 24 18 8A 59 08 8A 51 09 88 54 24 1A 80 FB
10 75 09 80 FA 5A 72 04 B3 5A EB 14 80 FB 0C 72
0D 80 FB 0E 72 04 B3 14 EB 06 B3 08 EB 02 30 DB
8A 88 3C 06 00 00 8A 90 68 06 00 00 88 4C 24 13
88 54 24 18 E8 F7 02 B6 FF C3
'@
    $newScaler = Convert-HexBytes @'
9C 60 0F B6 44 24 3E BA 00 00 80 3F 83 F8 1E 72
19 BA 00 00 A0 3F 83 F8 3C 72 0F BA 00 00 C0 3F
83 F8 5A 72 05 BA 00 00 00 40 8B B7 2C 06 00 00
85 F6 74 08 52 52 52 E8 04 80 D4 FF 61 9D 8B 4E
1C 51 E8 69 E8 D5 FF C3
'@
    Copy-Bytes $oldSelector $oldCave 0
    Copy-Bytes $oldScaler $oldCave 0x40
    Copy-Bytes $newSelector $newCave 0
    Copy-Bytes $newScaler $newCave 0x50

    $hook1Target = Get-PetOwnerMergeOctagramRelativeTarget `
        $hook1 0 $hook1Va
    $oldHook2Target = Get-PetOwnerMergeOctagramRelativeTarget `
        $oldHook2 0 $hook2Va
    $newHook2Target = Get-PetOwnerMergeOctagramRelativeTarget `
        $newHook2 0 $hook2Va
    $selectorLookupTarget = Get-PetOwnerMergeOctagramRelativeTarget `
        $newSelector 68 $caveVa
    $scalerScaleTarget = Get-PetOwnerMergeOctagramRelativeTarget `
        $newScaler 55 ($caveVa + 0x50)
    $scalerFinishTarget = Get-PetOwnerMergeOctagramRelativeTarget `
        $newScaler 66 ($caveVa + 0x50)

    $betweenHooks = Convert-HexBytes @'
0F B6 4C 24 14 8B D0 0F B6 C3 50 8A 44 24 13 E8
74 87 00 00 85 C0 74 30 83 C0 7C 74 2B 83 78 14
00 74 25 83 78 18 10 72 05 8B 40 04 EB 03 83 C0
04 50 57 E8 C0 FF DE FF
'@
    $lookupReturn = Convert-HexBytes 'C2 04 00'
    $effectCreateReturn = Convert-HexBytes 'C2 08 00'
    $manualOriginal = Convert-HexBytes 'E8 92 23 00 00'
    $manualPatched = Convert-HexBytes '90 90 90 90 90'
    $backOriginalHook = Convert-HexBytes '8B 0D A0 60 57 01'
    $backPatchedHook = Convert-HexBytes 'E9 25 8B 34 00 90'
    $backCode = Convert-HexBytes @'
83 3D 4C 5F 57 01 02 75 12 A1 A0 60 57 01 85 C0
74 14 A1 8C 60 57 01 85 C0 74 0B 8B 0D A0 60 57
01 E9 B6 74 CB FF BF 02 00 00 00 C6 05 66 5C 57
01 01 89 3D 50 5F 57 01 E9 CD 74 CB FF
'@
    $backOriginalCave = [byte[]]::new(112)
    $backPatchedCave = [byte[]]::new(112)
    Copy-Bytes $backCode $backPatchedCave 0

    if ($hook1.Length -ne 31 -or $oldHook2.Length -ne 9 -or
        $newHook2.Length -ne 9 -or $oldSelector.Length -ne 60 -or
        $oldScaler.Length -ne 72 -or $newSelector.Length -ne 74 -or
        $newScaler.Length -ne 72 -or
        (Measure-ByteDifference $oldHook2 $newHook2) -ne 1 -or
        (Measure-ByteDifference $oldCave $newCave) -ne 138 -or
        $hook1Target -ne $caveVa -or
        $oldHook2Target -ne $caveVa + 0x40 -or
        $newHook2Target -ne $caveVa + 0x50 -or
        $selectorLookupTarget -ne 0x0049E8C0 -or
        $scalerScaleTarget -ne 0x00686610 -or
        $scalerFinishTarget -ne 0x0069CE80) {
        throw 'Internal owner-Merge octagram binary invariants are invalid.'
    }

    $states = @{}
    foreach ($state in @(
        @('74ADEEC986C7005CE1A986027AFB8AAAEEC8E4DA58CA3A28F3794E3DC14C442C',
          '8D15E202D8178927E69F06909659EA14DD7FD0EE8BE853BD3394E5EEE684D31F',
          $false, $false, 'Base'),
        @('9896D740DB9FC3A82478DFB696A70E3BB3D9F8619E4575069F1BA311B39AD4CA',
          '4EF7A3A5F62BB739081CD76425D4AF14BEFDB03D1F36DABECF66624B1C4BA2DB',
          $true, $false, 'ManualRealmSelection'),
        @('C22D932A70A037B0983DE7DAB3D3A9DA44DD3A56DB143C6D31FBCA8913EF50F9',
          'FE01690D51B5A6C1FAEE48627372F35FFE9E110966E01F7D1EA96163EE8DEF61',
          $false, $true, 'CharacterBack'),
        @('318BA84B9F7720E827D91F658387D6FA2C9F61E8E05D5901647F54EE525208DF',
          'FFCC3508FA48DCCEF1135BD92194BD46A95872B4CED914FE5B025801C9C5AFD5',
          $true, $true, 'ManualRealmSelectionAndCharacterBack')
    )) {
        $oldHash, $newHash, $manual, $back, $peerName = $state
        $states[$oldHash] = [pscustomobject]@{
            Hash = $oldHash; PeerHash = $newHash; Octagram = $false
            Manual = $manual; Back = $back; PeerName = $peerName
        }
        $states[$newHash] = [pscustomobject]@{
            Hash = $newHash; PeerHash = $oldHash; Octagram = $true
            Manual = $manual; Back = $back; PeerName = $peerName
        }
    }

    return [pscustomobject]@{
        ExpectedLength = 6676480
        Hook1Offset = $hook1Offset; Hook1Va = $hook1Va; Hook1 = $hook1
        Hook2Offset = $hook2Offset; Hook2Va = $hook2Va
        OldHook2 = $oldHook2; NewHook2 = $newHook2
        CaveOffset = $caveOffset; CaveVa = $caveVa; CaveLength = $caveLength
        OldCave = $oldCave; NewCave = $newCave
        NewSelector = $newSelector; NewScaler = $newScaler
        BetweenHooks = $betweenHooks; LookupReturn = $lookupReturn
        EffectCreateReturn = $effectCreateReturn
        ManualOffset = 0x1F9A19
        ManualOriginal = $manualOriginal; ManualPatched = $manualPatched
        BackHookOffset = 0x1F58B6
        BackOriginalHook = $backOriginalHook; BackPatchedHook = $backPatchedHook
        BackCaveOffset = 0x53E3E0
        BackOriginalCave = $backOriginalCave; BackPatchedCave = $backPatchedCave
        States = $states
    }
}

function Get-PetOwnerMergeOctagramExeState(
    [byte[]]$Data,
    [object]$Spec
) {
    if ($Data.Length -ne $Spec.ExpectedLength) {
        throw "Unsupported Origin.exe length $($Data.Length)."
    }
    $pe = Get-PeMetadata $Data
    $hook1Mapping = Resolve-ExecutableFileRange `
        $pe $Spec.Hook1Offset $Spec.Hook1.Length
    $hook2Mapping = Resolve-ExecutableFileRange `
        $pe $Spec.Hook2Offset $Spec.OldHook2.Length
    $caveMapping = Resolve-ExecutableFileRange `
        $pe $Spec.CaveOffset $Spec.CaveLength
    if ($pe.Machine -ne 0x014C -or $pe.OptionalMagic -ne 0x010B -or
        $pe.ImageBase -ne 0x00400000 -or
        $hook1Mapping.Va -ne $Spec.Hook1Va -or
        $hook2Mapping.Va -ne $Spec.Hook2Va -or
        $caveMapping.Va -ne $Spec.CaveVa) {
        throw 'Origin.exe is not the audited fixed-base x86 PE32 layout.'
    }
    if (-not (Test-Bytes $Data $Spec.Hook1Offset $Spec.Hook1) -or
        -not (Test-Bytes $Data 0x2A1748 $Spec.BetweenHooks) -or
        -not (Test-Bytes $Data 0x2A9FF1 $Spec.LookupReturn) -or
        -not (Test-Bytes $Data 0x9198E $Spec.EffectCreateReturn)) {
        throw 'Origin.exe owner-Merge native prerequisites changed.'
    }

    $hash = Get-PetOwnerMergeOctagramSha256 $Data
    if (-not $Spec.States.ContainsKey($hash)) {
        throw "Unsupported Origin.exe SHA-256/state: $hash"
    }
    $state = $Spec.States[$hash]
    $hook2 = if ($state.Octagram) { $Spec.NewHook2 } else { $Spec.OldHook2 }
    $cave = if ($state.Octagram) { $Spec.NewCave } else { $Spec.OldCave }
    $manual = if ($state.Manual) {
        $Spec.ManualPatched
    }
    else { $Spec.ManualOriginal }
    $backHook = if ($state.Back) {
        $Spec.BackPatchedHook
    }
    else { $Spec.BackOriginalHook }
    $backCave = if ($state.Back) {
        $Spec.BackPatchedCave
    }
    else { $Spec.BackOriginalCave }
    if (-not (Test-Bytes $Data $Spec.Hook2Offset $hook2) -or
        -not (Test-Bytes $Data $Spec.CaveOffset $cave) -or
        -not (Test-Bytes $Data $Spec.ManualOffset $manual) -or
        -not (Test-Bytes $Data $Spec.BackHookOffset $backHook) -or
        -not (Test-Bytes $Data $Spec.BackCaveOffset $backCave)) {
        throw "Origin.exe bytes disagree with exact state $hash."
    }
    $xrefs = @(Get-PetOwnerMergeOctagramCaveXrefs `
        $Data $pe $Spec.CaveVa ($Spec.CaveVa + $Spec.CaveLength))
    $expectedHook2Target = $Spec.CaveVa + $(if ($state.Octagram) { 0x50 } else { 0x40 })
    if ($xrefs.Count -ne 2 -or
        $xrefs[0].Offset -ne $Spec.Hook1Offset -or
        $xrefs[0].Target -ne $Spec.CaveVa -or
        $xrefs[1].Offset -ne $Spec.Hook2Offset -or
        $xrefs[1].Target -ne $expectedHook2Target) {
        throw 'Origin.exe failed the owner-Merge cave xref audit.'
    }
    return $state
}

function Convert-PetOwnerMergeOctagramExe(
    [byte[]]$Data,
    [object]$Spec,
    [bool]$TargetOctagram
) {
    $state = Get-PetOwnerMergeOctagramExeState $Data $Spec
    if ($state.Octagram -eq $TargetOctagram) { return ,([byte[]]$Data.Clone()) }
    [byte[]]$result = $Data.Clone()
    Copy-Bytes $(if ($TargetOctagram) { $Spec.NewHook2 } else { $Spec.OldHook2 }) `
        $result $Spec.Hook2Offset
    Copy-Bytes $(if ($TargetOctagram) { $Spec.NewCave } else { $Spec.OldCave }) `
        $result $Spec.CaveOffset
    $expected = $state.PeerHash
    if ((Get-PetOwnerMergeOctagramSha256 $result) -ne $expected -or
        (Measure-ByteDifference $Data $result) -ne 139) {
        throw 'Generated owner-Merge octagram Origin.exe failed exact validation.'
    }
    [void](Get-PetOwnerMergeOctagramExeState $result $Spec)
    return ,$result
}
