Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'ControlledHostClientInventoryCore.psm1'
)

function New-RebornControlledHostInstalledInventory {
    param(
        [Parameter(Mandatory)][object]$StockInventory,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$CandidateSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$LegacyNetSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ManifestSha256,
        [Parameter(Mandatory)][Int64]$CandidateLength,
        [Parameter(Mandatory)][Int64]$LegacyNetLength,
        [Parameter(Mandatory)][Int64]$ManifestLength,
        [ValidateScript({
            [string]::IsNullOrEmpty($_) -or
            $_ -cmatch '^[0-9A-Fa-f]{64}$'
        })]
        [string]$CandidateOriginSha256,
        [Nullable[Int64]]$CandidateOriginLength
    )

    $hasOriginCandidate =
        -not [string]::IsNullOrWhiteSpace($CandidateOriginSha256)
    $hasOriginLength = $null -ne $CandidateOriginLength
    if ($hasOriginCandidate -ne $hasOriginLength) {
        throw (
            'Installed inventory requires both candidate Origin hash and ' +
            'length, or neither.')
    }
    if ($hasOriginCandidate -and [Int64]$CandidateOriginLength -le 0) {
        throw 'Candidate Origin length must be positive.'
    }

    $files = [Collections.Generic.List[object]]::new()
    $foundNet = $false
    $foundOrigin = $false
    foreach ($file in @($StockInventory.Files)) {
        if ([string]$file.RelativePath -ceq 'Net.dll') {
            $files.Add([pscustomobject]@{
                RelativePath = 'Net.dll'
                Length = $CandidateLength
                Sha256 = $CandidateSha256.ToUpperInvariant()
            })
            $foundNet = $true
        } elseif (
            $hasOriginCandidate -and
            [string]$file.RelativePath -ceq 'Origin.exe'
        ) {
            $files.Add([pscustomobject]@{
                RelativePath = 'Origin.exe'
                Length = [Int64]$CandidateOriginLength
                Sha256 = $CandidateOriginSha256.ToUpperInvariant()
            })
            $foundOrigin = $true
        } else {
            $files.Add($file)
        }
    }
    if (-not $foundNet) {
        throw 'Stock client inventory does not contain Net.dll.'
    }
    if ($hasOriginCandidate -and -not $foundOrigin) {
        throw 'Stock client inventory does not contain Origin.exe.'
    }

    $files.Add([pscustomobject]@{
        RelativePath = 'NetLegacy.dll'
        Length = $LegacyNetLength
        Sha256 = $LegacyNetSha256.ToUpperInvariant()
    })
    $files.Add([pscustomobject]@{
        RelativePath = 'RebornNetwork.gwem'
        Length = $ManifestLength
        Sha256 = $ManifestSha256.ToUpperInvariant()
    })
    $set = Get-RebornControlledHostInventorySetSha256 $files.ToArray()
    return [pscustomobject]@{
        ClientRoot = $StockInventory.ClientRoot
        SetSha256 = $set.SetSha256
        Files = $set.Files
        WritableOutputRelativePaths =
            Get-RebornControlledHostWritableOutputRelativePaths
        WritableOutputFileRelativePaths =
            Get-RebornControlledHostWritableOutputFileRelativePaths
    }
}

Export-ModuleMember -Function 'New-RebornControlledHostInstalledInventory'
