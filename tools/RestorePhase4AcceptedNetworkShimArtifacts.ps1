[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleFiles.psm1'
) -Force

$acceptedRoot = Join-Path $PSScriptRoot (
    '..\artifacts\controlled-host-acceptance\20260727-011921\' +
    'candidate-posthandshake-alpn-fix')
$outputRoot = Join-Path $PSScriptRoot (
    '..\client\network-shim\bin\Release\Win32')
$manifestPath = Join-Path $PSScriptRoot (
    '..\artifacts\secure-network\RebornNetwork.gwem')
$candidateSha256 =
    '0328D7EA84B68DD8D5A1DF7B0A291B9DC17EF3337C0114A7A396283FC4EF852B'
$checksSha256 =
    'D583309B921C7AA795F7A044F096762703AA2DB376A1D07B9EEB4F44312208D0'
$manifestSha256 =
    '3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C'

$sourceCandidate = Join-Path $acceptedRoot 'Net.dll'
$sourceChecks = Join-Path $acceptedRoot 'Godswar.NetShim.Checks.New.exe'
$sourceLegacy = Join-Path $acceptedRoot 'NetLegacy.dll'
$targetCandidate = Join-Path $outputRoot 'Net.dll'
$targetChecks = Join-Path $outputRoot 'Godswar.NetShim.Checks.exe'

foreach ($binding in @(
    @($sourceCandidate, $candidateSha256),
    @($sourceChecks, $checksSha256),
    @(
        $sourceLegacy,
        '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
    ),
    @($manifestPath, $manifestSha256)
)) {
    if (-not (Test-Path -LiteralPath $binding[0] -PathType Leaf) -or
        (Get-FileHash -LiteralPath $binding[0] -Algorithm SHA256).Hash -cne
            $binding[1]) {
        throw "Pinned accepted shim artifact changed: $($binding[0])"
    }
}

[IO.Directory]::CreateDirectory(
    [IO.Path]::GetFullPath($outputRoot)) | Out-Null
Copy-RebornFileAtomic `
    $sourceCandidate $targetCandidate $candidateSha256
Copy-RebornFileAtomic `
    $sourceChecks $targetChecks $checksSha256

& $targetChecks --offline
if ($LASTEXITCODE -ne 0) {
    throw 'Accepted native checks failed their offline suite.'
}
& $targetChecks `
    --offline-manifest-probe $targetCandidate $manifestPath
if ($LASTEXITCODE -ne 0) {
    throw 'Accepted client no longer verifies the signed manifest.'
}
& $targetChecks --offline-probe $sourceCandidate
if ($LASTEXITCODE -ne 0) {
    throw 'Accepted client no longer delegates to the stock predecessor.'
}

[pscustomobject]@{
    Result = 'RestoredAcceptedArtifacts'
    CandidatePath = [IO.Path]::GetFullPath($targetCandidate)
    CandidateSha256 = (
        Get-FileHash -LiteralPath $targetCandidate -Algorithm SHA256
    ).Hash
    NativeChecksPath = [IO.Path]::GetFullPath($targetChecks)
    NativeChecksSha256 = (
        Get-FileHash -LiteralPath $targetChecks -Algorithm SHA256
    ).Hash
    ManifestSha256 = $manifestSha256
}
