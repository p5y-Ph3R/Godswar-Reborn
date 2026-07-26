[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath =
    Join-Path $PSScriptRoot 'ManageDevelopmentNetworkHosts.ps1'
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force
$root = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-hosts-hardlink-test-$([guid]::NewGuid().ToString('N'))")

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )
    $errorText = $null
    try {
        & $Action | Out-Null
    }
    catch {
        $errorText = $_.Exception.Message
    }
    Assert-True (
        $null -ne $errorText -and $errorText -match $Pattern
    ) "$Message; error was: $errorText"
}

function Invoke-HostsTool {
    param(
        [Parameter(Mandatory)][object]$Fixture,
        [Parameter(Mandatory)]
        [ValidateSet('Status', 'Apply', 'Restore')]
        [string]$Mode
    )
    $parameters = @{
        Mode = $Mode
        HostsPath = $Fixture.Hosts
        ReceiptPath = $Fixture.Receipt
        OperationLockRoot = (Join-Path $Fixture.Root 'operation-lock')
        AllowTestPath = $true
    }
    if ($Mode -ne 'Status') {
        $parameters.AllowHostsWrite = $true
        $parameters.Confirm = $false
    }
    & $scriptPath @parameters
}

function Remove-TestLink {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
    }
}

[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $hosts = Join-Path $root 'hosts'
    $receipt = Join-Path $root 'receipt\hosts-receipt.json'
    [IO.File]::WriteAllText(
        $hosts,
        "127.0.0.1 localhost`r`n",
        [Text.Encoding]::ASCII)
    $fixture = [pscustomobject]@{
        Root = $root
        Hosts = $hosts
        Receipt = $receipt
    }

    $result = 'SkippedUnsupportedFileSystem'
    $probe = Join-Path $root 'probe-alias'
    try {
        New-Item -ItemType HardLink -Path $probe -Target $hosts |
            Out-Null
        $result = 'Passed'
    }
    catch {
        $result = 'SkippedUnsupportedFileSystem'
    }
    finally {
        Remove-TestLink $probe
    }

    if ($result -eq 'Passed') {
        $hostsLink = Join-Path $root 'hosts-alias'
        New-Item -ItemType HardLink `
            -Path $hostsLink -Target $hosts | Out-Null
        try {
            Assert-Throws {
                Invoke-HostsTool $fixture Status
            } 'hard-linked' 'hard-linked hosts target was accepted'
        }
        finally {
            Remove-TestLink $hostsLink
        }

        Invoke-HostsTool $fixture Apply | Out-Null
        $active =
            Get-Content $receipt -Raw | ConvertFrom-Json
        $backupLink = Join-Path $root 'backup-alias'
        New-Item -ItemType HardLink `
            -Path $backupLink -Target ([string]$active.backupPath) |
            Out-Null
        try {
            Assert-Throws {
                Invoke-HostsTool $fixture Restore
            } 'hard-linked' 'hard-linked hosts backup was accepted'
        }
        finally {
            Remove-TestLink $backupLink
        }

        $receiptLink = Join-Path $root 'receipt-alias'
        New-Item -ItemType HardLink `
            -Path $receiptLink -Target $receipt | Out-Null
        try {
            Assert-Throws {
                Invoke-HostsTool $fixture Restore
            } 'hard-linked' 'hard-linked active receipt was accepted'
        }
        finally {
            Remove-TestLink $receiptLink
        }
        Invoke-HostsTool $fixture Restore | Out-Null

        $history = @(
            Get-ChildItem -LiteralPath (Split-Path -Parent $receipt) -File |
                Where-Object {
                    $_.Name -like (
                        'hosts-receipt.history-*-Restored.json')
                }
        )[0]
        $historyLink = Join-Path $root 'history-alias'
        New-Item -ItemType HardLink `
            -Path $historyLink -Target $history.FullName | Out-Null
        try {
            Assert-Throws {
                Assert-RebornSingleLinkRegularFilePath `
                    $history.FullName 'hosts receipt history'
            } 'hard-linked' 'hard-linked receipt history was accepted'
        }
        finally {
            Remove-TestLink $historyLink
        }
    }

    [pscustomobject]@{
        Result = 'Passed'
        HardLinkRefusal = $result
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
