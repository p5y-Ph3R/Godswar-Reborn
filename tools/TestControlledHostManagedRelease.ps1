[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostManagedRelease.psm1'
) -Force

function Assert-Rejected {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Label
    )

    $rejected = $false
    try {
        & $Action | Out-Null
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Unsafe managed-release fixture was accepted: $Label"
    }
}

$source = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot `
        '..\src\Godswar.Server\bin\Release\net10.0'))
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) "reborn-managed-release-$([guid]::NewGuid().ToString('N'))"
$release = Join-Path $temporaryRoot 'release'
$external = Join-Path $temporaryRoot 'external-Npgsql.dll'
$junction = Join-Path $temporaryRoot 'release-junction'

try {
    New-Item -ItemType Directory -Path $release | Out-Null
    Get-ChildItem -LiteralPath $source -File -Force |
        Copy-Item -Destination $release
    $baseline = Get-RebornControlledHostManagedReleaseSet $release
    if ($baseline.Files.Count -ne 8 -or
        $baseline.SetSha256 -cnotmatch '^[0-9A-F]{64}$') {
        throw 'Managed-release baseline did not produce a canonical set.'
    }

    $unexpected = Join-Path $release 'unexpected.dll'
    [IO.File]::WriteAllBytes($unexpected, [byte[]](1, 2, 3))
    Assert-Rejected {
        Get-RebornControlledHostManagedReleaseSet $release
    } 'unexpected DLL'
    Remove-Item -LiteralPath $unexpected -Force

    $runtime = Join-Path $release 'Godswar.Server.runtimeconfig.json'
    $runtimeBackup = [IO.File]::ReadAllBytes($runtime)
    try {
        [IO.File]::AppendAllText($runtime, ' ')
        $tampered = Get-RebornControlledHostManagedReleaseSet $release
        if ($tampered.SetSha256 -ceq $baseline.SetSha256) {
            throw 'Managed-release tampering did not change the set hash.'
        }
    }
    finally {
        [IO.File]::WriteAllBytes($runtime, $runtimeBackup)
        [Array]::Clear($runtimeBackup, 0, $runtimeBackup.Length)
    }

    $npgsql = Join-Path $release 'Npgsql.dll'
    Copy-Item -LiteralPath $npgsql -Destination $external
    Remove-Item -LiteralPath $npgsql -Force
    New-Item -ItemType HardLink -Path $npgsql -Target $external |
        Out-Null
    Assert-Rejected {
        Get-RebornControlledHostManagedReleaseSet $release
    } 'hard-linked runtime file'
    Remove-Item -LiteralPath $npgsql -Force
    Copy-Item -LiteralPath $external -Destination $npgsql

    New-Item -ItemType Junction -Path $junction -Target $release |
        Out-Null
    Assert-Rejected {
        Get-RebornControlledHostManagedReleaseSet $junction
    } 'reparse-point release root'

    $restored = Get-RebornControlledHostManagedReleaseSet $release
    if ($restored.SetSha256 -cne $baseline.SetSha256) {
        throw 'Managed-release fixture did not restore exactly.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        $resolved = [IO.Path]::GetFullPath($temporaryRoot)
        $temp = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\')
        if (-not $resolved.StartsWith(
                $temp + '\reborn-managed-release-',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing unsafe managed-release cleanup: $resolved"
        }
        if (Test-Path -LiteralPath $junction) {
            [IO.Directory]::Delete($junction)
        }
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host 'Controlled-host managed release checks passed.'
