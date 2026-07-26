[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostPrivacyEvidence.psm1'
) -Force

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Description
    )

    try {
        & $Action
    }
    catch {
        return
    }
    throw "Expected failure: $Description"
}

$root = Join-Path (
    [IO.Path]::GetTempPath()
) "reborn-controlled-host-evidence-$([Guid]::NewGuid().ToString('N'))"
$resolvedTemp =
    ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())).TrimEnd('\')
$resolvedRoot = [IO.Path]::GetFullPath($root)
if (-not $resolvedRoot.StartsWith(
        "$resolvedTemp\reborn-controlled-host-evidence-",
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing an unexpected evidence-test root.'
}

try {
    $path = New-RebornControlledHostEvidencePath $root
    if (Test-Path -LiteralPath $path) {
        throw 'Evidence path must be reserved but not pre-created.'
    }

    $valid = @(
        '[controlled-host] privacy-safe evidence channel started',
        '[controlled-host] secure listeners ready',
        '[controlled-host] TLS policy accepted',
        '[controlled-host] accepted secure preface response written',
        '[controlled-host] TLS client authenticated',
        '[controlled-host] UDP endpoint authenticated and bound',
        '[controlled-host] secure server stopping'
    )
    [IO.File]::WriteAllText(
        $path,
        (($valid -join [Environment]::NewLine) +
            [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    $result =
        Assert-RebornControlledHostPrivacyEvidence `
            $path -RequireStopped
    if ($result.Events -ne $valid.Count -or
        $result.Bytes -gt 1536) {
        throw 'Valid evidence did not preserve its fixed bounds.'
    }
    Protect-RebornControlledHostPrivacyEvidence $path | Out-Null

    $invalid = Join-Path $root 'injected.log'
    [IO.File]::WriteAllText(
        $invalid,
        (
            $valid[0] + [Environment]::NewLine +
            'character=Alice' + [Environment]::NewLine +
            '[secure-acceptance] one-way TLS fallback observed' +
            [Environment]::NewLine +
            $valid[-1] + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        Assert-RebornControlledHostPrivacyEvidence `
            $invalid -RequireStopped | Out-Null
    } 'attacker-controlled or forged line'

    $duplicate = Join-Path $root 'duplicate.log'
    [IO.File]::WriteAllText(
        $duplicate,
        (
            $valid[0] + [Environment]::NewLine +
            $valid[1] + [Environment]::NewLine +
            $valid[1] + [Environment]::NewLine +
            $valid[-1] + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        Assert-RebornControlledHostPrivacyEvidence `
            $duplicate -RequireStopped | Out-Null
    } 'duplicate fixed event'

    $oversized = Join-Path $root 'oversized.log'
    [IO.File]::WriteAllText(
        $oversized,
        ('X' * 1537),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        Assert-RebornControlledHostPrivacyEvidence `
            $oversized | Out-Null
    } 'oversized evidence'

    'Controlled-host privacy evidence tests passed.'
}
finally {
    if (Test-Path -LiteralPath $resolvedRoot) {
        $identity =
            [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
        foreach ($file in @(
            Get-ChildItem -LiteralPath $resolvedRoot -File -Force
        )) {
            & $icacls $file.FullName /grant:r "${identity}:(F)" |
                Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw 'Failed to restore the exact test-file cleanup ACL.'
            }
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
