[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleTransaction.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleTestFixtures.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientInventoryCore.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientInstalledInventory.psm1'
) -Force

function Get-TestSha256 {
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-TestStreamSha256 {
    param([Parameter(Mandatory)][IO.FileStream]$Stream)

    $position = $Stream.Position
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $Stream.Position = 0
        return (
            [BitConverter]::ToString(
                $algorithm.ComputeHash($Stream))
        ).Replace('-', '')
    }
    finally {
        $Stream.Position = $position
        $algorithm.Dispose()
    }
}

$root = Join-Path (
    [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\artifacts'))
) ('paired-origin-bundle-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $client = Join-Path $root 'client'
    $inputs = Join-Path $root 'inputs'
    $backups = Join-Path $root 'backups'
    foreach ($directory in @($client, $inputs, $backups)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $origin = Join-Path $client 'Origin.exe'
    $net = Join-Path $client 'Net.dll'
    $candidateOrigin = Join-Path $inputs 'Origin.exe'
    $candidateNet = Join-Path $inputs 'Net.dll'
    $manifest = Join-Path $inputs 'RebornNetwork.gwem'
    $trust = Join-Path $inputs 'manifest-trust.json'
    $state = Join-Path $root 'activation-state.json'
    Write-TestBytes $origin 4096 101
    Write-TestBytes $net 2048 103
    Write-TestBytes $candidateOrigin 4096 107
    Write-TestBytes $candidateNet 3072 109
    New-SignedManifestFixture $manifest $trust 7

    $stockOriginHash = Get-TestSha256 $origin
    $stockNetHash = Get-TestSha256 $net
    $candidateOriginHash = Get-TestSha256 $candidateOrigin
    $candidateNetHash = Get-TestSha256 $candidateNet
    $manifestHash = Get-TestSha256 $manifest
    $trustHash = Get-TestSha256 $trust
    $policy = New-RebornSecureBundlePolicy `
        $stockOriginHash `
        $stockNetHash `
        $candidateNetHash `
        $manifestHash `
        $trustHash `
        $candidateOriginHash

    $stock = Get-RebornSecureBundleStatus `
        $policy $client $candidateNet $manifest $trust `
        OfflineFile $state `
        -CandidateOriginPath $candidateOrigin
    Assert-True (
        $stock.State -eq 'Stock' -and
        $stock.OriginSha256 -ceq $stockOriginHash
    ) 'paired fixture did not begin in exact Stock state'

    $precommitEvidence = [pscustomobject]@{ Ran = $false }
    $apply = Invoke-RebornSecureBundleApply `
        $policy $client $candidateNet $manifest $trust $backups `
        OfflineFile $state `
        -CandidateOriginPath $candidateOrigin `
        -PreCommitValidation {
            param([IO.FileStream]$LockedOriginStream)
            $precommitEvidence.Ran = $true
            if ((Get-TestStreamSha256 $LockedOriginStream) -cne
                $candidateOriginHash) {
                throw 'Pre-commit lock did not cover candidate Origin.'
            }
            $readBlocked = $false
            try {
                $probe = [IO.File]::OpenRead($origin)
                $probe.Dispose()
            }
            catch {
                $readBlocked = $true
            }
            if (-not $readBlocked) {
                throw 'Candidate Origin was launch-readable before commit.'
            }
        }
    Assert-True (
        $apply.Result -eq 'InstalledExact' -and
        $precommitEvidence.Ran -and
        (Get-TestSha256 $origin) -ceq $candidateOriginHash
    ) 'paired Apply did not atomically install candidate Origin'

    $receipt = Get-Content -LiteralPath (
        Join-Path $apply.BackupPath 'receipt.json'
    ) -Raw | ConvertFrom-Json
    Assert-True (
        $receipt.schemaVersion -eq 4 -and
        @($receipt.files).Count -eq 4 -and
        @($receipt.recoveryInputs).Count -eq 4 -and
        (Get-TestSha256 (
            Join-Path $apply.BackupPath 'Origin.exe'
        )) -ceq $stockOriginHash -and
        (Get-TestSha256 (
            Join-Path $apply.BackupPath 'candidate-Origin.exe'
        )) -ceq $candidateOriginHash
    ) 'paired Apply did not create a self-contained schema-4 receipt'

    $installed = Get-RebornSecureBundleStatus `
        $policy $client $candidateNet $manifest $trust `
        OfflineFile $state `
        -CandidateOriginPath $candidateOrigin
    Assert-True (
        $installed.State -eq 'InstalledExact' -and
        $installed.OriginSha256 -ceq $candidateOriginHash
    ) 'paired installed state did not bind candidate Origin'

    [IO.File]::Delete($candidateOrigin)
    [IO.File]::Delete($candidateNet)
    [IO.File]::Delete($manifest)
    [IO.File]::Delete($trust)
    $restore = Invoke-RebornSecureBundleRestore `
        $policy $client '' '' '' $apply.BackupPath $backups `
        OfflineFile $state
    Assert-True (
        $restore.AcceptedSourceState -eq 'InstalledExact' -and
        (Get-TestSha256 $origin) -ceq $stockOriginHash -and
        (Get-TestSha256 $net) -ceq $stockNetHash -and
        -not (Test-Path (Join-Path $client 'NetLegacy.dll')) -and
        -not (Test-Path (Join-Path $client 'RebornNetwork.gwem'))
    ) 'self-contained paired Restore did not reproduce Stock'

    [IO.File]::Copy(
        (Join-Path $apply.BackupPath 'candidate-Origin.exe'),
        $candidateOrigin)
    [IO.File]::Copy(
        (Join-Path $apply.BackupPath 'candidate-Net.dll'),
        $candidateNet)
    [IO.File]::Copy(
        (Join-Path $apply.BackupPath 'endpoint-manifest.gwem'),
        $manifest)
    [IO.File]::Copy(
        (Join-Path $apply.BackupPath 'manifest-trust.json'),
        $trust)
    foreach ($failurePoint in @(
        'AfterState',
        'AfterManifest',
        'AfterLegacy',
        'AfterCandidate',
        'AfterOrigin'
    )) {
        $failed = $false
        try {
            Invoke-RebornSecureBundleApply `
                $policy $client $candidateNet $manifest $trust $backups `
                OfflineFile $state `
                -CandidateOriginPath $candidateOrigin `
                -FailurePoint $failurePoint | Out-Null
        }
        catch {
            $failed = $_.Exception.Message -match 'Simulated interruption'
        }
        Assert-True (
            $failed -and
            (Get-TestSha256 $origin) -ceq $stockOriginHash -and
            (Get-TestSha256 $net) -ceq $stockNetHash
        ) "paired interruption $failurePoint did not roll back both files"
    }

    $stockInventory = [pscustomobject]@{
        ClientRoot = $client
        Files = @(
            [pscustomobject]@{
                RelativePath = 'Origin.exe'
                Length = 4096
                Sha256 = $stockOriginHash
            },
            [pscustomobject]@{
                RelativePath = 'Net.dll'
                Length = 2048
                Sha256 = $stockNetHash
            }
        )
        SetSha256 = 'unused'
    }
    $legacyNetOnlyInventory = New-RebornControlledHostInstalledInventory `
        $stockInventory `
        $candidateNetHash `
        $stockNetHash `
        $manifestHash `
        3072 `
        2048 `
        (Get-Item $manifest).Length `
        $null `
        $null
    $legacyOriginEntry = @($legacyNetOnlyInventory.Files | Where-Object {
        $_.RelativePath -ceq 'Origin.exe'
    })
    Assert-True (
        $legacyOriginEntry.Count -eq 1 -and
        $legacyOriginEntry[0].Sha256 -ceq $stockOriginHash
    ) 'legacy Net-only inventory did not accept an omitted Origin candidate'

    $installedInventory = New-RebornControlledHostInstalledInventory `
        $stockInventory `
        $candidateNetHash `
        $stockNetHash `
        $manifestHash `
        3072 `
        2048 `
        (Get-Item $manifest).Length `
        $candidateOriginHash `
        ([Nullable[Int64]]4096)
    $originEntry = @($installedInventory.Files | Where-Object {
        $_.RelativePath -ceq 'Origin.exe'
    })
    Assert-True (
        $originEntry.Count -eq 1 -and
        $originEntry[0].Sha256 -ceq $candidateOriginHash
    ) 'installed inventory did not transform the Origin entry'

    Write-Host 'Paired Origin secure-bundle checks passed.'
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
