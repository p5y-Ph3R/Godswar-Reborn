function Get-PetOwnerMergeOctagramAssetSpec([string]$RepositoryRoot) {
    return [pscustomobject]@{
        RelativeClientPath = 'Characters\PetUniteEffect\e_he_0004_all.gwm'
        CanonicalPath = Join-Path $RepositoryRoot `
            'assets\pet-owner-merge\e_he_0004_all.gwm'
        Length = 20816
        Hash = '0CF3D009356726F9A0A4691E2B03AD01557FDB8C7AAAF860E15170D66C0C1B4D'
        ModelLength = 1637
        TextureLength = 18727
        TailLength = 436
        LegacyLength = 20366
        LegacyHash =
            '97E14E301888C41E774F8C4312312F96E3DAD2FC8B88D3836369D60F4A0BAC59'
        LegacyTextureLength = 18277
        Effect0002Hashes = @{
            Stock =
                '89B98361733C4D127CEE984EACD58D7EE1DA098728672B11CB673AA5BA70A2F2'
            Purple =
                '7947392068C9FF1ED3C76973C80D37CA6B214493A8EBB90CD1329D4B5DCA7BE9'
        }
        Effect0001Hash =
            '042627ABE2A78EF62FD83F1D622E9868282153EF480278B6ECAEC94F2A7190C1'
        Effect0003Hash =
            'D46D3741FBFCBB0E393B758F0B8674782032672CAB3CB49C8E671DFF974937D2'
    }
}

function Assert-PetOwnerMergeOctagramAsset(
    [byte[]]$Data,
    [object]$Spec,
    [string]$Label
) {
    if ($Data.Length -ne $Spec.Length -or
        (Get-PetOwnerMergeOctagramSha256 $Data) -ne $Spec.Hash -or
        [BitConverter]::ToUInt32($Data, 0) -ne 1 -or
        [BitConverter]::ToUInt32($Data, 4) -ne 0 -or
        [BitConverter]::ToUInt32($Data, 8) -ne $Spec.ModelLength -or
        [BitConverter]::ToUInt32($Data, 12) -ne $Spec.TextureLength -or
        -not (Test-Bytes $Data 16 (
            [Text.Encoding]::ASCII.GetBytes('xof 0303bzip0032')))) {
        throw "$Label is not the exact audited owner-Merge octagram GWM."
    }
    $textureOffset = 16 + $Spec.ModelLength
    $tailOffset = $textureOffset + $Spec.TextureLength
    if ($Data[$textureOffset + 2] -ne 10 -or
        [BitConverter]::ToUInt16($Data, $textureOffset + 12) -ne 128 -or
        [BitConverter]::ToUInt16($Data, $textureOffset + 14) -ne 128 -or
        $Data[$textureOffset + 16] -ne 32 -or
        $Data[$textureOffset + 17] -ne 8 -or
        $Data.Length - $tailOffset -ne $Spec.TailLength) {
        throw "$Label lost its audited model/TGA/tail structure."
    }
    $tailText = [Text.Encoding]::ASCII.GetString(
        $Data, $tailOffset, $Spec.TailLength)
    if ([regex]::Matches($tailText, 'e_he_0004_all').Count -ne 1 -or
        [regex]::Matches($tailText, 'e_he_0004_a\.tga').Count -ne 1 -or
        $tailText.Contains('e_he_0003')) {
        throw "$Label lost its unique internal resource identities."
    }
    for ($index = $Data.Length - 8; $index -lt $Data.Length; $index++) {
        if ($Data[$index] -ne 0) {
            throw "$Label lost its eight-byte zero trailer."
        }
    }
}

function Get-PetOwnerMergeOctagramInstalledAssetState(
    [byte[]]$Data,
    [object]$Spec,
    [string]$Label
) {
    $hash = Get-PetOwnerMergeOctagramSha256 $Data
    if ($hash -eq $Spec.Hash) {
        Assert-PetOwnerMergeOctagramAsset $Data $Spec $Label
        return 'Fixed'
    }
    if ($hash -eq $Spec.LegacyHash) {
        $legacySpec = [pscustomobject]@{
            Length = $Spec.LegacyLength
            Hash = $Spec.LegacyHash
            ModelLength = $Spec.ModelLength
            TextureLength = $Spec.LegacyTextureLength
            TailLength = $Spec.TailLength
        }
        Assert-PetOwnerMergeOctagramAsset $Data $legacySpec $Label
        return 'LegacyCrossScanline'
    }
    throw "$Label is not an exact recognized owner-Merge octagram GWM."
}

function Get-PetOwnerMergeOctagramCanonicalAsset([object]$Spec) {
    if (-not (Test-Path -LiteralPath $Spec.CanonicalPath -PathType Leaf)) {
        throw "Canonical owner-Merge octagram asset is missing: $($Spec.CanonicalPath)"
    }
    [byte[]]$data = [IO.File]::ReadAllBytes($Spec.CanonicalPath)
    Assert-PetOwnerMergeOctagramAsset $data $Spec 'Canonical asset'
    return ,$data
}

function Assert-PetOwnerMergeOctagramPinnedAssets(
    [string]$ClientRoot,
    [object]$Spec
) {
    foreach ($item in @(
        @('e_he_0001_all.gwm', $Spec.Effect0001Hash),
        @('e_he_0003_all.gwm', $Spec.Effect0003Hash)
    )) {
        $path = Join-Path $ClientRoot (
            'Characters\PetUniteEffect\' + $item[0])
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne
                $item[1]) {
            throw "Pinned owner-Merge effect changed or is missing: $($item[0])"
        }
    }
    $effect0002Path = Join-Path $ClientRoot `
        'Characters\PetUniteEffect\e_he_0002_all.gwm'
    if (-not (Test-Path -LiteralPath $effect0002Path -PathType Leaf)) {
        throw 'Pinned owner-Merge effect is missing: e_he_0002_all.gwm'
    }
    $effect0002Hash = (Get-FileHash -LiteralPath $effect0002Path `
        -Algorithm SHA256).Hash
    foreach ($palette in $Spec.Effect0002Hashes.Keys) {
        if ($effect0002Hash -eq $Spec.Effect0002Hashes[$palette]) {
            return $palette
        }
    }
    throw "Unsupported effect 0002 palette/hash: $effect0002Hash"
}
