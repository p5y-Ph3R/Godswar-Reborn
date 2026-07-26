[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$hostsTool =
    Join-Path $PSScriptRoot 'ManageDevelopmentNetworkHosts.ps1'
Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentNetworkHostsRuntimeGate.psm1'
) -Force
$root = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-hosts-runtime-gate-test-$([guid]::NewGuid().ToString('N'))")

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $hosts = Join-Path $root 'hosts'
    $receipt = Join-Path $root 'receipt\hosts-receipt.json'
    $lockRoot = Join-Path $root 'operation-lock'
    [IO.File]::WriteAllText(
        $hosts,
        "127.0.0.1 localhost`r`n",
        [Text.Encoding]::ASCII)
    $parameters = @{
        HostsPath = $hosts
        ReceiptPath = $receipt
        OperationLockRoot = $lockRoot
        AllowTestPath = $true
        AllowHostsWrite = $true
        Confirm = $false
    }

    & $hostsTool -Mode Apply @parameters | Out-Null
    $receiptHashBefore =
        (Get-FileHash $receipt -Algorithm SHA256).Hash
    $hostsHashBefore =
        (Get-FileHash $hosts -Algorithm SHA256).Hash
    $lock = Enter-RebornDevelopmentHostsRuntimeLock `
        -LockRoot $lockRoot -AllowTestPath
    try {
        $authority = Assert-RebornDevelopmentHostsInstalledExact `
            -HostsPath $hosts `
            -ReceiptPath $receipt `
            -AllowTestPath
        Assert-True (
            $authority.State -ceq 'InstalledExact' -and
            $authority.HostsSha256 -ceq $hostsHashBefore -and
            (Get-FileHash $receipt -Algorithm SHA256).Hash -ceq
                $receiptHashBefore
        ) 'read-only runtime gate did not preserve InstalledExact state'

        $blocked = $false
        try {
            & $hostsTool -Mode Restore @parameters | Out-Null
        }
        catch {
            $blocked = $_.Exception.Message -match (
                'operation lock is already held')
        }
        Assert-True (
            $blocked -and
            (Get-FileHash $hosts -Algorithm SHA256).Hash -ceq
                $hostsHashBefore -and
            (Get-FileHash $receipt -Algorithm SHA256).Hash -ceq
                $receiptHashBefore
        ) 'runtime lock did not block a concurrent hosts mutation'
    }
    finally {
        Exit-RebornDevelopmentHostsRuntimeLock $lock
    }

    & $hostsTool -Mode Restore @parameters | Out-Null
    [pscustomobject]@{
        Result = 'Passed'
        InstalledExactGate = $true
        ReadOnlyValidation = $true
        LifetimeLockContention = $true
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
