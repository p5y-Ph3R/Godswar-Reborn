[CmdletBinding()]
param(
    [string]$FixtureRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-OriginContractTest {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-OriginContractTestSha256 {
    param([Parameter(Mandatory)][string]$LiteralPath)

    return (
        Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256
    ).Hash
}

if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $acceptanceRoot = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot `
            '..\artifacts\controlled-host-acceptance'))
    $fixture = @(
        Get-ChildItem -LiteralPath $acceptanceRoot `
            -Directory -ErrorAction Stop |
            Where-Object Name -Like '*-preview-ready-v6' |
            Sort-Object LastWriteTimeUtc -Descending
    ) | Select-Object -First 1
    if ($null -eq $fixture) {
        throw 'The PreviewReadyV6 offline fixture is absent.'
    }
    $FixtureRoot = $fixture.FullName
}

$resolvedFixture = [IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\')
$candidateRoot = Join-Path $resolvedFixture 'candidate'
$candidate = Join-Path $candidateRoot 'Net.dll'
$candidateOrigin = Join-Path $candidateRoot 'Origin.exe'
$stockNet = Join-Path $candidateRoot 'NetLegacy.dll'
$manifest = Join-Path $candidateRoot 'RebornNetwork.gwem'
$checks = Join-Path $candidateRoot 'Godswar.NetShim.Checks.exe'
foreach ($required in @(
    $candidate,
    $candidateOrigin,
    $stockNet,
    $manifest,
    $checks
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "PreviewReadyV6 gate input is absent: $required"
    }
}

$root = Join-Path (
    [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\artifacts'))
) ('origin-contract-gate-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $tamperedOrigin = Join-Path $root 'Origin-tampered.exe'

    $candidateHash = Get-OriginContractTestSha256 $candidate
    $candidateOriginHash =
        Get-OriginContractTestSha256 $candidateOrigin
    $stockNetHash = Get-OriginContractTestSha256 $stockNet
    $manifestHash = Get-OriginContractTestSha256 $manifest
    $checksHash = Get-OriginContractTestSha256 $checks
    $protectedHashes = [ordered]@{
        $candidate = $candidateHash
        $candidateOrigin = $candidateOriginHash
        $stockNet = $stockNetHash
        $manifest = $manifestHash
        $checks = $checksHash
    }

    & $checks --offline
    Assert-OriginContractTest ($LASTEXITCODE -eq 0) (
        'The PreviewReadyV6 offline native checks failed.')

    & $checks `
        --offline-manifest-probe `
        $candidate `
        $manifest
    Assert-OriginContractTest ($LASTEXITCODE -eq 0) (
        'The PreviewReadyV6 manifest contract probe failed.')

    & $checks `
        --offline-origin-contract-probe `
        $candidate `
        $candidateOrigin
    Assert-OriginContractTest ($LASTEXITCODE -eq 0) (
        'The matching PreviewReadyV6 Net-to-Origin probe failed.')

    & $checks --offline-probe $candidate
    Assert-OriginContractTest ($LASTEXITCODE -eq 0) (
        'The PreviewReadyV6 stock-delegation probe failed.')

    [IO.File]::Copy($candidateOrigin, $tamperedOrigin, $false)
    $tamperedBytes = [IO.File]::ReadAllBytes($tamperedOrigin)
    $tamperedBytes[$tamperedBytes.Length - 1] =
        $tamperedBytes[$tamperedBytes.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($tamperedOrigin, $tamperedBytes)
    [Array]::Clear($tamperedBytes, 0, $tamperedBytes.Length)
    $tamperedOriginHash =
        Get-OriginContractTestSha256 $tamperedOrigin
    Assert-OriginContractTest (
        $tamperedOriginHash -cne $candidateOriginHash
    ) 'The negative Origin fixture was not changed.'

    $savedErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $checks `
            --offline-origin-contract-probe `
            $candidate `
            $tamperedOrigin 2>$null
        $tamperedProbeExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorPreference
    }
    Assert-OriginContractTest (
        $tamperedProbeExitCode -ne 0
    ) 'The native probe did not reject a tampered Origin build identity.'

    $installerPath =
        Join-Path $PSScriptRoot 'InstallSecureNetworkBundle.ps1'
    $tokens = $null
    $parseErrors = $null
    $installerAst =
        [Management.Automation.Language.Parser]::ParseFile(
            $installerPath,
            [ref]$tokens,
            [ref]$parseErrors)
    Assert-OriginContractTest (
        @($parseErrors).Count -eq 0
    ) 'The secure bundle installer did not parse for order validation.'
    $commands = @($installerAst.FindAll({
        param($Node)
        $Node -is [Management.Automation.Language.CommandAst]
    }, $true))
    $gateCommands = @($commands | Where-Object {
        $_.GetCommandName() -ceq
            'Invoke-RebornSecureBundleNativeOfflineGate'
    })
    $mutationCommands = @($commands | Where-Object {
        $_.GetCommandName() -ceq 'Invoke-RebornSecureBundleApply'
    })
    Assert-OriginContractTest (
        $gateCommands.Count -eq 1 -and
        $mutationCommands.Count -eq 1 -and
        $gateCommands[0].Extent.StartOffset -lt
            $mutationCommands[0].Extent.StartOffset
    ) (
        'The secure bundle installer no longer rejects a mismatched ' +
        'Origin before its first client mutation.')

    foreach ($binding in $protectedHashes.GetEnumerator()) {
        Assert-OriginContractTest (
            (Get-OriginContractTestSha256 $binding.Key) -ceq
                $binding.Value
        ) "The offline gate mutated its fixture input: $($binding.Key)"
    }

    Write-Host (
        'PreviewReadyV6 Net-to-Origin offline contract gate checks passed.')
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $artifactRoot = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\artifacts')).TrimEnd('\')
    if ($resolved.StartsWith(
            $artifactRoot + '\',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved -PathType Container)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
