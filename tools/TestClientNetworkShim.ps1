[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$LegacyDllPath,

    [string]$CandidateShimPath,

    [string]$EndpointManifestPath,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
# Installer -WhatIf must still run this test's private, disposable staging.
$WhatIfPreference = $false

$requiredVcToolsVersion = '14.44.35207'
$expectedLegacyHash = `
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $repoRoot `
    "client\network-shim\bin\$Configuration\Win32"
$shimPath = if ($CandidateShimPath) {
    [IO.Path]::GetFullPath($CandidateShimPath)
} else {
    Join-Path $outputDirectory 'Net.dll'
}
$testPath = Join-Path $outputDirectory 'Godswar.NetShim.Checks.exe'

if (-not $LegacyDllPath) {
    $installedLegacy = 'C:\Godswar Origin\NetLegacy.dll'
    $LegacyDllPath = if (
        Test-Path -LiteralPath $installedLegacy -PathType Leaf
    ) {
        $installedLegacy
    } else {
        'C:\Godswar Origin\Net.dll'
    }
}

if (-not $SkipBuild) {
    $buildScript = Join-Path $PSScriptRoot 'BuildClientNetworkShim.ps1'
    $firstBuild = & $buildScript -Configuration $Configuration
    $firstBuild | Out-Host
    $secondBuild = & $buildScript -Configuration $Configuration
    $secondBuild | Out-Host

    if ($firstBuild.ShimSha256 -ne $secondBuild.ShimSha256) {
        throw (
            'Two clean network-shim builds produced different hashes: ' +
            "$($firstBuild.ShimSha256) and $($secondBuild.ShimSha256)"
        )
    }
}

foreach ($requiredPath in @($LegacyDllPath, $shimPath, $testPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required file not found: $requiredPath"
    }
}
if ($EndpointManifestPath -and
    -not (Test-Path -LiteralPath $EndpointManifestPath -PathType Leaf)) {
    throw "Endpoint manifest not found: $EndpointManifestPath"
}

$legacyHash = (
    Get-FileHash -LiteralPath $LegacyDllPath -Algorithm SHA256
).Hash
if ($legacyHash -ne $expectedLegacyHash) {
    throw "Unsupported legacy Net.dll hash: $legacyHash"
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} `
    'Microsoft Visual Studio\Installer\vswhere.exe'
$installPath = & $vswhere `
    -latest `
    -products '*' `
    -version '[17.0,18.0)' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $installPath) {
    throw 'Visual Studio 2022 with the x86/x64 C++ tools is required.'
}

$toolRoot = Join-Path $installPath `
    "VC\Tools\MSVC\$requiredVcToolsVersion"
$dumpbin = Join-Path $toolRoot `
    'bin\Hostx64\x86\dumpbin.exe'
if (-not (Test-Path -LiteralPath $dumpbin -PathType Leaf)) {
    throw "dumpbin not found: $dumpbin"
}

$headers = (& $dumpbin /headers $shimPath) -join "`n"
$exports = (& $dumpbin /exports $shimPath) -join "`n"
$dependents = (& $dumpbin /dependents $shimPath) -join "`n"

if ($headers -notmatch '(?im)^\s*14C machine \(x86\)\s*$') {
    throw 'Shim is not a PE32 x86 image.'
}
if ($headers -notmatch '(?im)^\s*50000000 image base\b') {
    throw 'Shim preferred base must remain 0x50000000, away from NetLegacy.dll.'
}
if ($headers -notmatch '(?im)Dynamic base') {
    throw 'Shim is missing ASLR/DYNAMICBASE.'
}
if ($headers -notmatch '(?im)NX compatible') {
    throw 'Shim is missing NX compatibility.'
}
if ($headers -notmatch '(?im)Control Flow Guard|Guard CF') {
    throw 'Shim is missing Control Flow Guard.'
}

$exportMatches = [regex]::Matches(
    $exports,
    '(?im)^\s+(\d+)\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)(?:\s+=.*)?\s*$')
$exportTable = @(
    foreach ($match in $exportMatches) {
        [pscustomobject]@{
            Ordinal = [int]$match.Groups[1].Value
            Name = $match.Groups[2].Value
        }
    }
)

if ($exportTable.Count -ne 2 -or
    -not ($exportTable | Where-Object {
        $_.Ordinal -eq 1 -and $_.Name -eq 'NetClientCreate'
    }) -or
    -not ($exportTable | Where-Object {
        $_.Ordinal -eq 2 -and $_.Name -eq 'NetServiceCreate'
    })) {
    throw "Unexpected export contract:`n$exports"
}

if ($dependents -match '(?im)VCRUNTIME|MSVCP|UCRTBASE') {
    throw "Shim has an unbundled C/C++ runtime dependency:`n$dependents"
}

& $testPath
if ($LASTEXITCODE -ne 0) {
    throw "Proxy unit checks failed with exit code $LASTEXITCODE."
}

$artifactRoot = Join-Path $repoRoot 'artifacts\network-shim'
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$stage = Join-Path $artifactRoot ("probe-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null

try {
    $validStage = Join-Path $stage 'valid'
    New-Item -ItemType Directory -Path $validStage | Out-Null
    $stagedShim = Join-Path $validStage 'Net.dll'
    $stagedLegacy = Join-Path $validStage 'NetLegacy.dll'
    Copy-Item -LiteralPath $shimPath -Destination $stagedShim
    Copy-Item -LiteralPath $LegacyDllPath -Destination $stagedLegacy
    if ($EndpointManifestPath) {
        Copy-Item -LiteralPath $EndpointManifestPath -Destination (
            Join-Path $validStage 'RebornNetwork.gwem')
    }

    & $testPath --probe $stagedShim
    if ($LASTEXITCODE -ne 0) {
        throw "Stock delegation probe failed with exit code $LASTEXITCODE."
    }

    $missingStage = Join-Path $stage 'missing'
    New-Item -ItemType Directory -Path $missingStage | Out-Null
    $missingShim = Join-Path $missingStage 'Net.dll'
    Copy-Item -LiteralPath $shimPath -Destination $missingShim
    if ($EndpointManifestPath) {
        Copy-Item -LiteralPath $EndpointManifestPath -Destination (
            Join-Path $missingStage 'RebornNetwork.gwem')
    }
    & $testPath --probe-rejected $missingShim 2
    if ($LASTEXITCODE -ne 0) {
        throw "Missing-legacy rejection failed with exit code $LASTEXITCODE."
    }

    $tamperedStage = Join-Path $stage 'tampered'
    New-Item -ItemType Directory -Path $tamperedStage | Out-Null
    $tamperedShim = Join-Path $tamperedStage 'Net.dll'
    $tamperedLegacy = Join-Path $tamperedStage 'NetLegacy.dll'
    Copy-Item -LiteralPath $shimPath -Destination $tamperedShim
    Copy-Item -LiteralPath $LegacyDllPath -Destination $tamperedLegacy
    if ($EndpointManifestPath) {
        Copy-Item -LiteralPath $EndpointManifestPath -Destination (
            Join-Path $tamperedStage 'RebornNetwork.gwem')
    }
    $tamperedBytes = [IO.File]::ReadAllBytes($tamperedLegacy)
    $tamperedBytes[$tamperedBytes.Length - 1] = [byte](
        $tamperedBytes[$tamperedBytes.Length - 1] -bxor 0xFF
    )
    [IO.File]::WriteAllBytes($tamperedLegacy, $tamperedBytes)

    & $testPath --probe-rejected $tamperedShim 13
    if ($LASTEXITCODE -ne 0) {
        throw "Tampered-legacy rejection failed with exit code $LASTEXITCODE."
    }
}
finally {
    $resolvedArtifactRoot = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if ($resolvedStage.StartsWith(
            "$resolvedArtifactRoot\",
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

[pscustomobject]@{
    Result = 'Passed'
    Architecture = 'x86'
    Exports = 'NetClientCreate@1, NetServiceCreate@2'
    LegacySha256 = $legacyHash
    ShimSha256 = (Get-FileHash -LiteralPath $shimPath -Algorithm SHA256).Hash
    Runtime = 'Static'
    RejectedLegacyCases = 'Missing, tampered'
}
