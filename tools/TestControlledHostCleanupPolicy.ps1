[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostCleanupPolicy.psm1'
) -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Message)
    $text = $null
    try {
        & $Action | Out-Null
    }
    catch {
        $text = $_.Exception.Message
    }
    Assert-True (
        $null -ne $text -and $text -match $Pattern
    ) "$Message; error was: $text"
}

$root = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-cleanup-policy-test-$([guid]::NewGuid().ToString('N'))")
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $client = Join-Path $root 'client'
    [IO.Directory]::CreateDirectory($client) | Out-Null
    [IO.File]::WriteAllBytes(
        (Join-Path $client 'Origin.exe'),
        [byte[]](1, 2, 3, 4))
    [IO.File]::WriteAllBytes(
        (Join-Path $client 'Net.dll'),
        [byte[]](5, 6, 7, 8))
    $legacyHash = (
        Get-FileHash (Join-Path $client 'Net.dll') -Algorithm SHA256
    ).Hash
    $disabled = [pscustomobject]@{
        Exists = $true
        Complete = $true
        Mode = [UInt64]0
        Environment = [UInt64]1
        SequenceFloor = [UInt64]7
    }
    $accepted = Assert-RebornControlledHostCleanupState `
        $client $client $legacyHash 1 7 $disabled -AllowTestPath
    Assert-True (
        $accepted.SequenceFloor -eq 7
    ) 'exact restored/disabled cleanup state was rejected'

    $unknown = Join-Path $root 'unknown-client'
    Assert-Throws {
        Assert-RebornControlledHostCleanupState `
            $unknown $client $legacyHash 1 7 $disabled -AllowTestPath
    } 'exact issued disposable client' (
        'caller-selected cleanup client was accepted')

    $alternate = Join-Path $root 'alternate-stock-clone'
    [IO.Directory]::CreateDirectory($alternate) | Out-Null
    foreach ($name in @('Origin.exe', 'Net.dll')) {
        [IO.File]::Copy(
            (Join-Path $client $name),
            (Join-Path $alternate $name))
    }
    Assert-Throws {
        Assert-RebornControlledHostCleanupState `
            $alternate $client $legacyHash 1 7 $disabled -AllowTestPath
    } 'exact issued disposable client' (
        'alternate byte-identical stock clone was accepted')

    [IO.File]::WriteAllText(
        (Join-Path $client 'RebornNetwork.gwem'),
        'active')
    Assert-Throws {
        Assert-RebornControlledHostCleanupState `
            $client $client $legacyHash 1 7 $disabled -AllowTestPath
    } 'shim remains active' 'active secure shim was accepted for cleanup'
    [IO.File]::Delete((Join-Path $client 'RebornNetwork.gwem'))

    $modeOne = $disabled.PSObject.Copy()
    $modeOne.Mode = [UInt64]1
    Assert-Throws {
        Assert-RebornControlledHostCleanupState `
            $client $client $legacyHash 1 7 $modeOne -AllowTestPath
    } 'receipt-bound disabled' 'ActivationMode=1 was accepted for cleanup'

    $environmentZero = $disabled.PSObject.Copy()
    $environmentZero.Environment = [UInt64]0
    Assert-Throws {
        Assert-RebornControlledHostCleanupState `
            $client $client $legacyHash 1 7 $environmentZero -AllowTestPath
    } 'receipt-bound disabled' (
        'Environment=0 was accepted for cleanup')

    $wrongEnvironment = $disabled.PSObject.Copy()
    $wrongEnvironment.Environment = [UInt64]2
    Assert-Throws {
        Assert-RebornControlledHostCleanupState `
            $client $client $legacyHash 1 7 $wrongEnvironment -AllowTestPath
    } 'receipt-bound disabled' (
        'wrong nonzero environment was accepted for cleanup')

    $wrongFloor = $disabled.PSObject.Copy()
    $wrongFloor.SequenceFloor = [UInt64]8
    Assert-Throws {
        Assert-RebornControlledHostCleanupState `
            $client $client $legacyHash 1 7 $wrongFloor -AllowTestPath
    } 'receipt-bound disabled' (
        'wrong retained sequence floor was accepted for cleanup')

    $partial = $disabled.PSObject.Copy()
    $partial.Complete = $false
    Assert-Throws {
        Assert-RebornControlledHostCleanupState `
            $client $client $legacyHash 1 7 $partial -AllowTestPath
    } 'receipt-bound disabled' (
        'partial activation state was accepted for cleanup')

    [pscustomobject]@{
        Result = 'Passed'
        ExactStockAccepted = $true
        UnknownClientRefused = $true
        AlternateStockCloneRefused = $true
        ActiveShimRefused = $true
        ActivationModeOneRefused = $true
        EnvironmentZeroRefused = $true
        WrongEnvironmentRefused = $true
        WrongFloorRefused = $true
        PartialActivationRefused = $true
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temporary = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith(
            $temporary,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unexpected test cleanup path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
