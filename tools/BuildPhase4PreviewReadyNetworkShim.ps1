[CmdletBinding()]
param(
    [string]$LegacyDllPath =
        'C:\RebornNetworkAcceptanceClient\Net.dll'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$headerPath = Join-Path $repoRoot (
    'client\network-shim\src\' +
    'SecureClientManifestDevelopmentKeys.generated.h')
$trustPath = Join-Path $repoRoot (
    'artifacts\secure-network\development-manifest-trust.json')
$nextTrustPath = Join-Path $repoRoot (
    'artifacts\secure-network\development-manifest-next-trust.json')
$manifestPath = Join-Path $repoRoot (
    'artifacts\secure-network\RebornNetwork.gwem')
$expectedTrustSha256 =
    'A32B40917A01D510504528F5D6996F918A6A218991B64C50234ED84C75C75C07'
$expectedNextTrustSha256 =
    '582C252D31DE3361157C7625FB21DD104F907EA762FB77044E1CCEF2EA51E571'
$expectedManifestSha256 =
    '3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C'

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentEndpointManifestKeyGeneration.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentEndpointManifestKeyReceipt.psm1'
) -Force

function Assert-FileHash {
    param([string]$Path, [string]$Expected)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne
            $Expected) {
        throw "Phase 4 public build input changed: $Path"
    }
}

function Read-PublicTrust {
    param(
        [string]$Path,
        [string]$ExpectedKeyId,
        [string]$ExpectedPurpose
    )

    $record = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($record.schemaVersion -ne 1 -or
        [string]$record.keyId -cne $ExpectedKeyId -or
        [string]$record.environment -cne '1' -or
        [string]$record.minimumSequence -cne '1' -or
        [string]$record.purpose -cne $ExpectedPurpose -or
        [string]::IsNullOrWhiteSpace([string]$record.cngKeyName)) {
        throw "Phase 4 public trust contract changed: $Path"
    }

    $x = [Convert]::FromBase64String([string]$record.x)
    $y = [Convert]::FromBase64String([string]$record.y)
    if ($x.Length -ne 32 -or $y.Length -ne 32) {
        throw "Phase 4 public trust coordinate length changed: $Path"
    }

    return [pscustomobject]@{
        Name = [string]$record.cngKeyName
        X = $x
        Y = $y
    }
}

function Invoke-CleanBuild {
    param([string]$BuildScript)

    $output = @(& $BuildScript -Configuration Release)
    $output | Out-Host
    $results = @(
        $output |
            Where-Object {
                $null -ne $_ -and
                $null -ne $_.PSObject.Properties['ShimPath'] -and
                $null -ne $_.PSObject.Properties['TestPath']
            })
    if ($results.Count -ne 1) {
        throw 'Clean build did not return one artifact authority.'
    }
    return $results[0]
}

Assert-FileHash $trustPath $expectedTrustSha256
Assert-FileHash $nextTrustPath $expectedNextTrustSha256
Assert-FileHash $manifestPath $expectedManifestSha256

$snapshot = Get-RebornManifestKeyArtifactSnapshot $headerPath
$snapshotSha256 = (
    Get-FileHash -LiteralPath $headerPath -Algorithm SHA256
).Hash
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()) (
    'reborn-phase4-public-build-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

try {
    $current = Read-PublicTrust `
        $trustPath '53249' `
        'development-only endpoint manifest verification'
    $next = Read-PublicTrust `
        $nextTrustPath '53250' `
        'development-only next endpoint manifest verification'

    Write-RebornManifestKeyPublicArtifacts `
        $current $next $current.Name $next.Name `
        $headerPath `
        (Join-Path $temporaryRoot 'current.json') `
        (Join-Path $temporaryRoot 'next.json')

    $binding = Get-RebornManifestKeyArtifactBinding `
        $headerPath $trustPath $nextTrustPath `
        $current.Name $next.Name
    if ($binding.CurrentTrustSha256 -cne $expectedTrustSha256 -or
        $binding.NextTrustSha256 -cne $expectedNextTrustSha256) {
        throw 'Generated public header is not bound to both pinned trusts.'
    }

    $buildScript = Join-Path $PSScriptRoot 'BuildClientNetworkShim.ps1'
    $first = Invoke-CleanBuild $buildScript
    $firstShimSha256 = (
        Get-FileHash -LiteralPath $first.ShimPath -Algorithm SHA256
    ).Hash
    $firstChecksSha256 = (
        Get-FileHash -LiteralPath $first.TestPath -Algorithm SHA256
    ).Hash

    $second = Invoke-CleanBuild $buildScript
    $secondShimSha256 = (
        Get-FileHash -LiteralPath $second.ShimPath -Algorithm SHA256
    ).Hash
    $secondChecksSha256 = (
        Get-FileHash -LiteralPath $second.TestPath -Algorithm SHA256
    ).Hash
    if ($firstShimSha256 -cne $secondShimSha256 -or
        $firstChecksSha256 -cne $secondChecksSha256) {
        throw 'Two clean public-trust builds were not deterministic.'
    }

    & (Join-Path $PSScriptRoot 'TestClientNetworkShim.ps1') `
        -Configuration Release `
        -LegacyDllPath $LegacyDllPath `
        -CandidateShimPath $second.ShimPath `
        -SkipBuild | Out-Host

    & $second.TestPath --offline
    if ($LASTEXITCODE -ne 0) {
        throw 'Public-trust candidate failed its offline native suite.'
    }
    & $second.TestPath `
        --offline-manifest-probe $second.ShimPath $manifestPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Public-trust candidate rejected the pinned signed manifest.'
    }
    & $second.TestPath --offline-contract-probe $second.ShimPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Public-trust candidate failed its embedded contract probe.'
    }

    [pscustomobject]@{
        Result = 'VerifiedPublicTrustBuild'
        CandidatePath = [IO.Path]::GetFullPath($second.ShimPath)
        CandidateSha256 = $secondShimSha256
        NativeChecksPath = [IO.Path]::GetFullPath($second.TestPath)
        NativeChecksSha256 = $secondChecksSha256
        ManifestSha256 = $expectedManifestSha256
        CurrentTrustSha256 = $expectedTrustSha256
        NextTrustSha256 = $expectedNextTrustSha256
        EmbeddedHeaderSha256 = $binding.HeaderSha256
        PrivateKeyAccess = 'None'
    }
}
finally {
    Restore-RebornManifestKeyArtifactSnapshot $snapshot
    if ((Get-FileHash -LiteralPath $headerPath -Algorithm SHA256).Hash -cne
        $snapshotSha256) {
        throw 'Placeholder public-key header was not restored exactly.'
    }

    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemporary =
        [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if (-not $resolvedTemporary.StartsWith(
            "$resolvedSystemTemporary\reborn-phase4-public-build-",
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove an unexpected public-build directory.'
    }
    if (Test-Path -LiteralPath $resolvedTemporary -PathType Container) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}
