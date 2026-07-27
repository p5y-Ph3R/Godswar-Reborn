[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\RebornNetworkAcceptanceClient'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientMutableOutput.psm1'
) -Force

$expectedClientRoot = 'C:\RebornNetworkAcceptanceClient'
$originSha256 =
    '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
$stockNetSha256 =
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
$writableOutputRelativePaths = @(
    'Log',
    'Dump',
    'ScreensHot',
    'Localization\en_us\Settings\User',
    'Localization\zh_cn\Settings\User'
)

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-WriteDenied {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]
        [ValidateSet('Create', 'Open')]
        [string]$Operation
    )

    $stream = $null
    $denied = $false
    try {
        if ($Operation -eq 'Create') {
            $stream = [IO.File]::Open(
                $Path,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
        } else {
            $stream = [IO.File]::Open(
                $Path,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Write,
                [IO.FileShare]::Read)
        }
    }
    catch [UnauthorizedAccessException] {
        $denied = $true
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        if ($Operation -eq 'Create' -and
            (Test-Path -LiteralPath $Path -PathType Leaf)) {
            Remove-Item -LiteralPath $Path -Force
        }
    }
    if (-not $denied) {
        throw "Expected current-user write denial: $Path"
    }
}

if (Test-IsAdministrator) {
    throw (
        'Run this access probe from the normal non-elevated acceptance ' +
        'account so ACL enforcement is measured accurately.')
}
if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
    throw 'Origin.exe must be closed during the client ACL probe.'
}

$client = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
if (-not $client.Equals(
        $expectedClientRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "ACL probe is restricted to $expectedClientRoot."
}
$origin = Join-Path $client 'Origin.exe'
$net = Join-Path $client 'Net.dll'
if ((Get-Sha256 $origin) -cne $originSha256 -or
    (Get-Sha256 $net) -cne $stockNetSha256) {
    throw 'Acceptance-client binaries are not the pinned stock baseline.'
}

$status = & (
    Join-Path $PSScriptRoot 'PrepareControlledHostClient.ps1'
) -Mode Status -ClientRoot $client
if ($status.State -ne 'Hardened') {
    throw "Acceptance-client ACL is not hardened: $($status.Reason)"
}
Assert-RebornControlledHostWritableOutputFileInactive `
    $client | Out-Null

$probeName = ".reborn-write-probe-$([guid]::NewGuid().ToString('N')).tmp"
Assert-WriteDenied `
    (Join-Path $client $probeName) `
    Create
$netBefore = Get-Sha256 $net
Assert-WriteDenied $net Open
Assert-WriteDenied (
    Join-Path $client 'patcher\patcher.log'
) Open
if ((Get-Sha256 $net) -cne $netBefore) {
    throw 'Net.dll changed during its denial probe.'
}
foreach ($relativePath in $writableOutputRelativePaths) {
    $directory = Join-Path $client $relativePath
    $probe = Join-Path $directory $probeName
    try {
        $bytes = [Text.Encoding]::ASCII.GetBytes('reborn-acl-probe')
        try {
            $stream = [IO.File]::Open(
                $probe,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush($true)
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
    }
    finally {
        if (Test-Path -LiteralPath $probe -PathType Leaf) {
            Remove-Item -LiteralPath $probe -Force
        }
    }
}
foreach ($locale in @('en_us', 'zh_cn')) {
    Assert-WriteDenied (
        Join-Path $client (
            "Localization\$locale\Settings\Sys\$probeName")
    ) Create
}

[pscustomobject]@{
    Result = 'Passed'
    ClientRoot = $client
    RootCreateDenied = $true
    NetWriteDenied = $true
    InactivePatcherLogWriteDenied = $true
    WritableOutputDirectories = $writableOutputRelativePaths.Count
    SystemSettingsCreateDenied = $true
}
