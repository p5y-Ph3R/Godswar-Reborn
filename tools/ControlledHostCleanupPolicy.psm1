Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force

function Assert-RebornControlledHostCleanupState {
    param(
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$ExpectedClientRoot,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedLegacyNetSha256,
        [Parameter(Mandatory)]
        [ValidateRange(1, 3)]
        [UInt64]$ExpectedEnvironment,
        [Parameter(Mandatory)]
        [ValidateRange(1, [Int64]::MaxValue)]
        [UInt64]$ExpectedSequenceFloor,
        [Parameter(Mandatory)][object]$ActivationState,
        [switch]$AllowTestPath
    )

    $client = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
    $expected = [IO.Path]::GetFullPath(
        $ExpectedClientRoot).TrimEnd('\')
    if (-not $client.Equals(
            $expected,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cleanup client root is not the exact issued disposable client.'
    }
    if (-not $AllowTestPath -and
        -not $client.Equals(
            'C:\RebornNetworkAcceptanceClient',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Production cleanup requires the controlled acceptance client.'
    }
    if (-not (Test-Path -LiteralPath $client -PathType Container)) {
        throw 'Controlled cleanup client root does not exist.'
    }
    $origin = Join-Path $client 'Origin.exe'
    $net = Join-Path $client 'Net.dll'
    foreach ($path in @($origin, $net)) {
        Assert-RebornSingleLinkRegularFilePath `
            $path 'controlled cleanup client file' | Out-Null
    }
    if (
        (Test-Path -LiteralPath (Join-Path $client 'NetLegacy.dll')) -or
        (Test-Path -LiteralPath (
            Join-Path $client 'RebornNetwork.gwem'))
    ) {
        throw 'Secure client shim remains active; Restore it before cleanup.'
    }
    if ((Get-FileHash -LiteralPath $net -Algorithm SHA256).Hash -cne
        $ExpectedLegacyNetSha256.ToUpperInvariant()) {
        throw 'Controlled client Net.dll is not the exact restored predecessor.'
    }

    $complete = if (
        $null -ne $ActivationState.PSObject.Properties['Complete']
    ) {
        [bool]$ActivationState.Complete
    } else {
        [bool]$ActivationState.Exists
    }
    if (
        -not $ActivationState.Exists -or
        -not $complete -or
        [UInt64]$ActivationState.Mode -ne 0 -or
        [UInt64]$ActivationState.Environment -ne $ExpectedEnvironment -or
        [UInt64]$ActivationState.SequenceFloor -ne $ExpectedSequenceFloor
    ) {
        throw (
            'Protected HKLM activation does not exactly match the ' +
            'receipt-bound disabled environment and sequence floor.')
    }
    [pscustomobject]@{
        ClientRoot = $client
        LegacyNetSha256 =
            $ExpectedLegacyNetSha256.ToUpperInvariant()
        Environment = [UInt64]$ActivationState.Environment
        SequenceFloor = [UInt64]$ActivationState.SequenceFloor
    }
}

Export-ModuleMember -Function 'Assert-RebornControlledHostCleanupState'
