function Assert-PetOwnerMergeVisualOctagramAssetState(
    [string]$ClientRoot,
    [bool]$OctagramPatched
) {
    $path = Join-Path $ClientRoot `
        'Characters\PetUniteEffect\e_he_0004_all.gwm'
    $hashes = @(
        '97E14E301888C41E774F8C4312312F96E3DAD2FC8B88D3836369D60F4A0BAC59',
        '0CF3D009356726F9A0A4691E2B03AD01557FDB8C7AAAF860E15170D66C0C1B4D')
    if ($OctagramPatched) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -notin
                $hashes) {
            throw 'Owner-Merge octagram state is missing its exact effect 0004.'
        }
    }
    elseif (Test-Path -LiteralPath $path) {
        throw 'Effect 0004 exists while the owner-Merge octagram selector is off.'
    }
}
