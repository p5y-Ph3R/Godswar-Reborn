[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$InstalledClientRoot = 'C:\Godswar Origin'
)

$ErrorActionPreference = 'Stop'

$stockHash = `
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
$repoRoot = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $PSScriptRoot 'InstallClientNetworkShim.ps1'
$binaryTest = Join-Path $PSScriptRoot 'TestClientNetworkShim.ps1'
$candidate = Join-Path $repoRoot `
    "client\network-shim\bin\$Configuration\Win32\Net.dll"
$probe = Join-Path $repoRoot `
    "client\network-shim\bin\$Configuration\Win32\Godswar.NetShim.Checks.exe"

& $binaryTest -Configuration $Configuration | Out-Host

$sourceOrigin = Join-Path $InstalledClientRoot 'Origin.exe'
$installedLegacy = Join-Path $InstalledClientRoot 'NetLegacy.dll'
$installedNet = Join-Path $InstalledClientRoot 'Net.dll'
$sourceLegacy = if (
    Test-Path -LiteralPath $installedLegacy -PathType Leaf
) {
    $installedLegacy
} else {
    $installedNet
}

foreach ($requiredPath in @(
    $sourceOrigin,
    $sourceLegacy,
    $candidate,
    $probe
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Installer-test prerequisite not found: $requiredPath"
    }
}

if ((Get-FileHash -LiteralPath $sourceLegacy -Algorithm SHA256).Hash -ne
    $stockHash) {
    throw "Installer-test source is not the supported stock Net.dll."
}

$artifactRoot = Join-Path $repoRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$testRoot = Join-Path $artifactRoot (
    'network-shim-installer-' + [guid]::NewGuid().ToString('N')
)
New-Item -ItemType Directory -Path $testRoot | Out-Null

function New-DisposableClient {
    param([Parameter(Mandatory)][string]$Name)

    $client = Join-Path $testRoot $Name
    New-Item -ItemType Directory -Path $client | Out-Null
    Copy-Item -LiteralPath $sourceOrigin -Destination (
        Join-Path $client 'Origin.exe'
    )
    Copy-Item -LiteralPath $sourceLegacy -Destination (
        Join-Path $client 'Net.dll'
    )
    return $client
}

function Get-State {
    param(
        [Parameter(Mandatory)][string]$Client,
        [Parameter(Mandatory)][string]$Shim
    )

    return & $installer `
        -Mode Status `
        -ClientRoot $Client `
        -ShimPath $Shim
}

function Assert-State {
    param(
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Client,
        [Parameter(Mandatory)][string]$Shim
    )

    $status = Get-State $Client $Shim
    if ($status.State -ne $Expected) {
        throw "Expected state $Expected, got $($status.State) for $Client"
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Operation,
        [Parameter(Mandatory)][string]$Label
    )

    try {
        & $Operation
    }
    catch {
        Write-Host "Expected refusal ($Label): $($_.Exception.Message)"
        return
    }

    throw "Expected operation to be refused: $Label"
}

$fakeOriginProcess = $null
try {
    # Verify default parameter binding in a fresh Windows PowerShell 5.1
    # process; this caught a prior $PSScriptRoot initializer regression.
    & powershell.exe `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -Command "& '$installer' -Mode Status | Out-Null"
    if ($LASTEXITCODE -ne 0) {
        throw "PowerShell 5.1 default Status failed: $LASTEXITCODE"
    }

    $mainClient = New-DisposableClient 'main-client'
    $mainBackups = Join-Path $testRoot 'main-backups'
    $customCandidate = Join-Path $testRoot 'candidate-Net.dll'
    Copy-Item -LiteralPath $candidate -Destination $customCandidate

    Assert-State 'Stock' $mainClient $customCandidate

    & $installer `
        -Mode Apply `
        -ClientRoot $mainClient `
        -BackupRoot $mainBackups `
        -ShimPath $customCandidate `
        -WhatIf `
        -Confirm:$false | Out-Host
    Assert-State 'Stock' $mainClient $customCandidate
    if (Test-Path -LiteralPath $mainBackups) {
        throw 'WhatIf created an installer backup directory.'
    }

    Assert-Throws {
        & $installer `
            -Mode Apply `
            -ClientRoot $mainClient `
            -BackupRoot $mainBackups `
            -ShimPath (Join-Path $mainClient 'Net.dll') `
            -Confirm:$false
    } 'decoy custom candidate'
    Assert-State 'Stock' $mainClient $customCandidate

    & $installer `
        -Mode Apply `
        -ClientRoot $mainClient `
        -BackupRoot $mainBackups `
        -ShimPath $customCandidate `
        -Confirm:$false | Out-Host
    Assert-State 'InstalledExact' $mainClient $customCandidate

    & $probe --probe (Join-Path $mainClient 'Net.dll')
    if ($LASTEXITCODE -ne 0) {
        throw "Disposable installed-object probe failed: $LASTEXITCODE"
    }

    $applyBackups = @(
        Get-ChildItem `
            -LiteralPath $mainBackups `
            -Directory `
            -Filter 'client-network-shim-v1-Apply-*'
    )
    if ($applyBackups.Count -ne 1) {
        throw "Expected one Apply backup, got $($applyBackups.Count)."
    }

    & $installer `
        -Mode Apply `
        -ClientRoot $mainClient `
        -BackupRoot $mainBackups `
        -ShimPath $customCandidate `
        -Confirm:$false | Out-Host
    $idempotentBackups = @(
        Get-ChildItem `
            -LiteralPath $mainBackups `
            -Directory `
            -Filter 'client-network-shim-v1-Apply-*'
    )
    if ($idempotentBackups.Count -ne 1) {
        throw 'Idempotent Apply created another backup.'
    }

    & $installer `
        -Mode Restore `
        -ClientRoot $mainClient `
        -BackupRoot $mainBackups `
        -ApplyBackupPath $applyBackups[0].FullName `
        -ShimPath (Join-Path $testRoot 'missing-build-artifact.dll') `
        -Confirm:$false | Out-Host
    Assert-State 'Stock' $mainClient $customCandidate

    $partialClient = New-DisposableClient 'partial-client'
    $partialBackups = Join-Path $testRoot 'partial-backups'
    Copy-Item -LiteralPath $sourceLegacy -Destination (
        Join-Path $partialClient 'NetLegacy.dll'
    )
    Assert-State 'RecoverablePartial' $partialClient $customCandidate
    & $installer `
        -Mode Apply `
        -ClientRoot $partialClient `
        -BackupRoot $partialBackups `
        -ShimPath $customCandidate `
        -Confirm:$false | Out-Host
    Assert-State 'InstalledExact' $partialClient $customCandidate

    $interruptedClient = New-DisposableClient 'interrupted-restore-client'
    $interruptedBackups = Join-Path $testRoot 'interrupted-backups'
    & $installer `
        -Mode Apply `
        -ClientRoot $interruptedClient `
        -BackupRoot $interruptedBackups `
        -ShimPath $customCandidate `
        -Confirm:$false | Out-Host
    $interruptedApply = Get-ChildItem `
        -LiteralPath $interruptedBackups `
        -Directory `
        -Filter 'client-network-shim-v1-Apply-*' |
        Select-Object -First 1
    if (-not $interruptedApply) {
        throw 'Interrupted-restore Apply backup was not created.'
    }

    $legacyLock = [IO.File]::Open(
        (Join-Path $interruptedClient 'NetLegacy.dll'),
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        Assert-Throws {
            & $installer `
                -Mode Restore `
                -ClientRoot $interruptedClient `
                -BackupRoot $interruptedBackups `
                -ApplyBackupPath $interruptedApply.FullName `
                -Confirm:$false
        } 'locked legacy during Restore'
        Assert-State `
            'RecoverablePartial' `
            $interruptedClient `
            $customCandidate
    }
    finally {
        $legacyLock.Dispose()
    }

    & $installer `
        -Mode Restore `
        -ClientRoot $interruptedClient `
        -BackupRoot $interruptedBackups `
        -ApplyBackupPath $interruptedApply.FullName `
        -ShimPath (Join-Path $testRoot 'missing-on-resume.dll') `
        -Confirm:$false | Out-Host
    Assert-State 'Stock' $interruptedClient $customCandidate

    $foreignClient = New-DisposableClient 'foreign-legacy-client'
    Copy-Item -LiteralPath $candidate -Destination (
        Join-Path $foreignClient 'NetLegacy.dll'
    )
    Assert-State 'UnknownRefused' $foreignClient $customCandidate
    Assert-Throws {
        & $installer `
            -Mode Apply `
            -ClientRoot $foreignClient `
            -BackupRoot (Join-Path $testRoot 'foreign-backups') `
            -ShimPath $customCandidate `
            -Confirm:$false
    } 'foreign NetLegacy.dll'

    $unsupportedClient = New-DisposableClient 'unsupported-origin-client'
    $unsupportedOrigin = Join-Path $unsupportedClient 'Origin.exe'
    $originBytes = [IO.File]::ReadAllBytes($unsupportedOrigin)
    $originBytes[$originBytes.Length - 1] = [byte](
        $originBytes[$originBytes.Length - 1] -bxor 0xFF
    )
    [IO.File]::WriteAllBytes($unsupportedOrigin, $originBytes)
    Assert-Throws {
        Get-State $unsupportedClient $customCandidate | Out-Null
    } 'unsupported Origin.exe'

    if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
        throw 'Close the real Origin.exe before running installer integration tests.'
    }

    $runningClient = New-DisposableClient 'running-client'
    $fakeProcessDirectory = Join-Path $testRoot 'fake-process'
    New-Item -ItemType Directory -Path $fakeProcessDirectory | Out-Null
    $fakeOrigin = Join-Path $fakeProcessDirectory 'Origin.exe'
    Copy-Item -LiteralPath "$env:WINDIR\System32\ping.exe" `
        -Destination $fakeOrigin
    $fakeOriginProcess = Start-Process `
        -FilePath $fakeOrigin `
        -ArgumentList @('127.0.0.1', '-n', '30') `
        -WindowStyle Hidden `
        -PassThru
    Start-Sleep -Milliseconds 150
    Assert-Throws {
        & $installer `
            -Mode Apply `
            -ClientRoot $runningClient `
            -BackupRoot (Join-Path $testRoot 'running-backups') `
            -ShimPath $customCandidate `
            -Confirm:$false
    } 'running Origin.exe'
    Assert-State 'Stock' $runningClient $customCandidate

    Write-Host 'All network-shim installer checks passed.'
}
finally {
    if ($fakeOriginProcess -and -not $fakeOriginProcess.HasExited) {
        Stop-Process -Id $fakeOriginProcess.Id -Force
        $fakeOriginProcess.WaitForExit()
    }

    $resolvedArtifactRoot = [IO.Path]::GetFullPath(
        $artifactRoot
    ).TrimEnd('\')
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith(
            "$resolvedArtifactRoot\",
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
