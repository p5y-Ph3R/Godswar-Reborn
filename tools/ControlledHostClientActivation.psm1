Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
foreach ($moduleName in @(
    'ControlledHostClientInventoryReceipt.psm1',
    'ControlledHostProcessEnvironment.psm1',
    'SecureNetworkActivationState.psm1',
    'SecureNetworkBundleTransaction.psm1',
    'SecureNetworkPathSafety.psm1'
)) {
    Import-Module (Join-Path $moduleRoot $moduleName) -Force
}

$script:ExpectedClientRoot = 'C:\RebornNetworkAcceptanceClient'

function Test-RebornControlledHostActivationStateEqual {
    param(
        [Parameter(Mandatory)][object]$Left,
        [Parameter(Mandatory)][object]$Right
    )

    return (
        [bool]$Left.Exists -eq [bool]$Right.Exists -and
        [bool]$Left.Complete -eq [bool]$Right.Complete -and
        [UInt64]$Left.Mode -eq [UInt64]$Right.Mode -and
        [UInt64]$Left.Environment -eq [UInt64]$Right.Environment -and
        [UInt64]$Left.SequenceFloor -eq [UInt64]$Right.SequenceFloor)
}

function Invoke-RebornControlledHostNativeCheck {
    param(
        [Parameter(Mandatory)][string]$ChecksPath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutMilliseconds = 15000
    )

    if ($TimeoutMilliseconds -lt 1000 -or
        $TimeoutMilliseconds -gt 60000) {
        throw 'Native-check timeout is outside the controlled bound.'
    }
    foreach ($argument in $Arguments) {
        if ($argument.Contains('"') -or
            $argument.Contains("`r") -or
            $argument.Contains("`n")) {
            throw 'Native-check argument contains a forbidden character.'
        }
    }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $ChecksPath
    $start.Arguments = (
        $Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $start.WorkingDirectory = Split-Path -Parent $ChecksPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    Set-RebornControlledHostSanitizedChildEnvironment $start | Out-Null

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw 'The protected native-check process did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            try {
                $process.Kill()
            }
            catch {
                # Preserve the timeout as the primary failure.
            }
            throw 'The protected native-check process timed out.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($stdout.Length -gt 65536 -or $stderr.Length -gt 65536) {
            throw 'The protected native-check output exceeded its bound.'
        }
        if ($process.ExitCode -ne 0) {
            $detail = $stderr.Trim()
            if ($detail.Length -gt 512) {
                $detail = $detail.Substring(0, 512)
            }
            throw (
                "Protected native check failed with exit code " +
                "$($process.ExitCode): $detail")
        }
        return $stdout.Trim()
    }
    finally {
        $process.Dispose()
    }
}

function Assert-RebornControlledHostClientActivation {
    param(
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$InventoryReceiptPath,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedInventoryReceiptSha256,
        [Parameter(Mandatory)][string]$ManifestTrustPath,
        [Parameter(Mandatory)][string]$NativeChecksPath,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedOriginSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedLegacyNetSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedCandidateSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedManifestSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedManifestTrustSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedNativeChecksSha256
    )

    $client = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
    if (-not $client.Equals(
            $script:ExpectedClientRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Client activation is outside the disposable acceptance root.'
    }
    if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
        throw 'Origin.exe must be closed before activation validation.'
    }
    Assert-RebornProtectedDirectoryPath `
        $client 'acceptance client root' `
        -ProtectContents `
        -RequireProtectedAcl | Out-Null

    $trust = Assert-RebornSingleLinkRegularFilePath `
        $ManifestTrustPath 'protected manifest trust'
    $checks = Assert-RebornSingleLinkRegularFilePath `
        $NativeChecksPath 'protected native checks'
    foreach ($path in @($trust, $checks)) {
        Assert-RebornProtectedRegularFilePath `
            $path 'protected activation input' | Out-Null
    }
    if ((Get-FileHash -LiteralPath $trust -Algorithm SHA256).Hash -cne
            $ExpectedManifestTrustSha256.ToUpperInvariant() -or
        (Get-FileHash -LiteralPath $checks -Algorithm SHA256).Hash -cne
            $ExpectedNativeChecksSha256.ToUpperInvariant()) {
        throw 'A protected activation input does not match its hash pin.'
    }

    $rebootGate =
        Assert-RebornControlledHostClientPostInventoryReboot `
            $InventoryReceiptPath `
            $ExpectedInventoryReceiptSha256
    $receipt = $rebootGate.Receipt
    $inventory = Assert-RebornControlledHostClientInventoryReceipt `
        $receipt `
        $client `
        InstalledExact `
        $ExpectedCandidateSha256 `
        $ExpectedLegacyNetSha256 `
        $ExpectedManifestSha256

    $candidate = Join-Path $client 'Net.dll'
    $manifest = Join-Path $client 'RebornNetwork.gwem'
    $policy = New-RebornSecureBundlePolicy `
        $ExpectedOriginSha256 `
        $ExpectedLegacyNetSha256 `
        $ExpectedCandidateSha256 `
        $ExpectedManifestSha256 `
        $ExpectedManifestTrustSha256
    $activationBefore = Assert-RebornProtectedHklmActivationState
    $status = Get-RebornSecureBundleStatus `
        $policy `
        $client `
        $candidate `
        $manifest `
        $trust `
        Hklm
    $activationAfter = Assert-RebornProtectedHklmActivationState
    if (-not (Test-RebornControlledHostActivationStateEqual `
            $activationBefore $activationAfter)) {
        throw 'The protected HKLM activation state changed during validation.'
    }
    if ($status.State -cne 'InstalledExact' -or
        -not $activationAfter.Exists -or
        -not $activationAfter.Complete -or
        [UInt64]$activationAfter.Mode -ne 1 -or
        [UInt64]$activationAfter.Environment -ne 1 -or
        [UInt64]$activationAfter.SequenceFloor -ne
            [UInt64]$status.ManifestSequence) {
        throw (
            'The client bundle and protected activation state are not an ' +
            'exact installed development bundle.')
    }

    Invoke-RebornControlledHostNativeCheck `
        $checks @('--offline-probe', $candidate) | Out-Null
    Invoke-RebornControlledHostNativeCheck `
        $checks @('--offline-manifest-probe', $candidate, $manifest) |
        Out-Null
    $activationFinal = Assert-RebornProtectedHklmActivationState
    if (-not (Test-RebornControlledHostActivationStateEqual `
            $activationAfter $activationFinal)) {
        throw 'The protected HKLM activation state changed after native checks.'
    }

    return [pscustomobject]@{
        State = $status.State
        ClientRoot = $client
        InventoryReceiptPath = $inventory.ReceiptPath
        InventoryReceiptSha256 = $inventory.ReceiptSha256
        StockInventorySetSha256 =
            $inventory.StockInventorySetSha256
        InstalledInventorySetSha256 =
            $inventory.CurrentInventorySetSha256
        SequenceFloor = [UInt64]$activationFinal.SequenceFloor
        ManifestSequence = [UInt64]$status.ManifestSequence
        InventoryCreatedUtc = $rebootGate.InventoryCreatedUtc
        LastBootUpTimeUtc = $rebootGate.LastBootUpTimeUtc
        NativeChecksSha256 =
            $ExpectedNativeChecksSha256.ToUpperInvariant()
    }
}

Export-ModuleMember -Function @(
    'Test-RebornControlledHostActivationStateEqual',
    'Invoke-RebornControlledHostNativeCheck',
    'Assert-RebornControlledHostClientActivation'
)
