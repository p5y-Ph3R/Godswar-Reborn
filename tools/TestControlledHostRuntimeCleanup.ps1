[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($moduleName in @(
    'ControlledHostRuntimeCleanup.psm1',
    'ControlledHostRuntimeCleanupReceipt.psm1',
    'ControlledHostClientRootLease.psm1'
)) {
    Import-Module (Join-Path $PSScriptRoot $moduleName) -Force
}

function Assert-True {
    param([bool]$Condition, [string]$Label)
    if (-not $Condition) {
        throw "Assertion failed: $Label"
    }
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Label)
    $accepted = $true
    try {
        & $Action | Out-Null
    }
    catch {
        $accepted = $false
    }
    if ($accepted) {
        throw "Unsafe runtime cleanup case was accepted: $Label"
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-runtime-cleanup-$([Guid]::NewGuid().ToString('N'))")
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

function New-CleanupFixture {
    param([Parameter(Mandatory)][int]$Index)

    $scope = Join-Path $testRoot "scope-$Index"
    $parent = Join-Path $scope 'RebornSecureNetworkRuntime'
    $runtime = Join-Path $parent (
        '20260726-' + (170000 + $Index).ToString('000000'))
    $receiptRoot = Join-Path $scope 'cleanup-receipts'
    [IO.Directory]::CreateDirectory(
        (Join-Path $runtime 'managed\nested')) | Out-Null
    [IO.Directory]::CreateDirectory(
        (Join-Path $runtime 'tls')) | Out-Null
    [IO.Directory]::CreateDirectory(
        (Join-Path $runtime 'bundle')) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $runtime 'receipt.json'),
        '{"fixture":true}')
    [IO.File]::WriteAllText(
        (Join-Path $runtime 'receipt.sha256'),
        ('A' * 64))
    [IO.File]::WriteAllBytes(
        (Join-Path $runtime 'managed\server.dll'),
        [byte[]](1, 2, 3, 4, 5))
    [IO.File]::WriteAllBytes(
        (Join-Path $runtime 'managed\nested\data.bin'),
        [byte[]](6, 7, 8))
    [IO.File]::WriteAllText(
        (Join-Path $runtime 'tls\secret.clixml'),
        'fixture-secret')
    $trustReceipt =
        Join-Path $runtime 'tls\current-user-trust-receipt.json'
    $keyReceipt =
        Join-Path $runtime (
            'bundle\development-manifest-key-receipt.json')
    [IO.File]::WriteAllText($trustReceipt, '{"state":"Removed"}')
    [IO.File]::WriteAllText($keyReceipt, '{"state":"Removed"}')
    $lease = Enter-RebornControlledHostDirectoryLease $runtime
    try {
        $identity = [string]$lease.Identity
    }
    finally {
        Exit-RebornControlledHostDirectoryLease $lease
    }
    $cleanup =
        New-RebornControlledHostRuntimeCleanupReceipt `
            -RuntimeRoot $runtime `
            -RuntimeIdentity $identity `
            -RuntimeReceiptSha256 ('B' * 64) `
            -RuntimeReceiptChecksumSha256 ('C' * 64) `
            -ClientInventoryReceiptPath (
                Join-Path $scope 'client-inventory.json') `
            -ClientInventoryReceiptSha256 ('D' * 64) `
            -FinalTrustReceiptSha256 (
                Get-FileHash $trustReceipt -Algorithm SHA256
            ).Hash `
            -FinalManifestKeyReceiptSha256 (
                Get-FileHash $keyReceipt -Algorithm SHA256
            ).Hash `
            -TrustRootThumbprint ('E' * 40) `
            -TrustRootSha256 ('F' * 64) `
            -ManifestCurrentKeyName (
                'Reborn-Network-Manifest-Development-Current-v1') `
            -ManifestNextKeyName (
                'Reborn-Network-Manifest-Development-Next-v1') `
            -ManifestCurrentTrustSha256 ('1' * 64) `
            -ManifestNextTrustSha256 ('2' * 64) `
            -ActivationEnvironment 1 `
            -ActivationSequenceFloor 7 `
            -ReceiptRoot $receiptRoot `
            -AllowTestPath
    [pscustomobject]@{
        Scope = $scope
        Runtime = $runtime
        ReceiptPath = $cleanup.Path
        Tombstone = $cleanup.TombstoneRoot
    }
}

try {
    $complete = New-CleanupFixture 1
    $removed =
        Invoke-RebornControlledHostRuntimeCleanup `
            $complete.ReceiptPath -AllowTestPath
    Assert-True `
        ($removed.Record.state -ceq 'Removed' -and
         -not (Test-Path -LiteralPath $complete.Runtime) -and
         -not (Test-Path -LiteralPath $complete.Tombstone)) `
        'full runtime cleanup'

    $rename = New-CleanupFixture 2
    Assert-Rejected {
        Invoke-RebornControlledHostRuntimeCleanup `
            $rename.ReceiptPath `
            -FaultAfter AfterRename `
            -AllowTestPath
    } 'injected post-rename interruption'
    Assert-True `
        (-not (Test-Path -LiteralPath $rename.Runtime) -and
         (Test-Path -LiteralPath $rename.Tombstone)) `
        'post-rename interruption boundary'
    $renameRetry =
        Invoke-RebornControlledHostRuntimeCleanup `
            $rename.ReceiptPath -AllowTestPath
    Assert-True `
        ($renameRetry.Record.state -ceq 'Removed') `
        'post-rename retry completion'

    $child = New-CleanupFixture 3
    Assert-Rejected {
        Invoke-RebornControlledHostRuntimeCleanup `
            $child.ReceiptPath `
            -FaultAfter AfterFirstChildDelete `
            -AllowTestPath
    } 'injected child-deletion interruption'
    Assert-True `
        (Test-Path -LiteralPath $child.Tombstone) `
        'partial child deletion retains tombstone'
    $childRetry =
        Invoke-RebornControlledHostRuntimeCleanup `
            $child.ReceiptPath -AllowTestPath
    Assert-True `
        ($childRetry.Record.state -ceq 'Removed') `
        'partial child-deletion retry completion'

    $boundary = New-CleanupFixture 4
    Assert-Rejected {
        Invoke-RebornControlledHostRuntimeCleanup `
            $boundary.ReceiptPath `
            -FaultAfter BeforeRootDelete `
            -AllowTestPath
    } 'injected empty-root boundary interruption'
    Assert-True `
        ((Test-Path -LiteralPath $boundary.Tombstone) -and
         @([IO.Directory]::EnumerateFileSystemEntries(
            $boundary.Tombstone)).Count -eq 0) `
        'empty tombstone survives root boundary interruption'
    $boundaryRetry =
        Invoke-RebornControlledHostRuntimeCleanup `
            $boundary.ReceiptPath -AllowTestPath
    Assert-True `
        ($boundaryRetry.Record.state -ceq 'Removed') `
        'empty-root boundary retry completion'

    $tamper = New-CleanupFixture 5
    Assert-Rejected {
        Invoke-RebornControlledHostRuntimeCleanup `
            $tamper.ReceiptPath `
            -FaultAfter AfterRename `
            -AllowTestPath
    } 'tamper fixture rename interruption'
    [IO.File]::WriteAllText(
        (Join-Path $tamper.Tombstone 'unexpected.txt'),
        'not receipt-bound')
    Assert-Rejected {
        Invoke-RebornControlledHostRuntimeCleanup `
            $tamper.ReceiptPath -AllowTestPath
    } 'unexpected tombstone entry'

    Write-Host 'Controlled-host runtime cleanup checks passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temporary = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith(
            $temporary + 'reborn-runtime-cleanup-',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unsafe runtime-cleanup test removal: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
