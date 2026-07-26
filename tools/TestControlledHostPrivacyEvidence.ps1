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
        '[secure-acceptance] authoritative UDP movement accepted',
        '[secure-acceptance] authoritative UDP snapshot queued',
        '[controlled-host] secure server stopping'
    )
    [IO.File]::WriteAllText(
        $path,
        (($valid -join [Environment]::NewLine) +
            [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    $result =
        Assert-RebornControlledHostPrivacyEvidence `
            $path `
            -Profile Baseline `
            -ObservedDuration ([TimeSpan]::FromMinutes(1)) `
            -RequireStopped
    if ($result.Events -ne $valid.Count -or
        $result.Bytes -gt 1536 -or
        $result.Profile -cne 'Baseline') {
        throw 'Valid evidence did not preserve its fixed bounds.'
    }
    Protect-RebornControlledHostPrivacyEvidence $path | Out-Null

    $fallback = @(
        $valid[0]
        '[secure-acceptance] phase4 fault campaign enabled'
        $valid[1..7]
        '[secure-acceptance] snapshot ACK drop started window_ms=1500 max_recorded_drops=32'
        '[secure-acceptance] snapshot ACK drop window completed'
        '[secure-acceptance] one-way TLS fallback observed'
        '[secure-acceptance] authoritative correction forced reason=not_ready'
        '[secure-acceptance] post-fallback TLS movement observed no_switchback=true'
        $valid[-1]
    )
    $fallbackPath = Join-Path $root 'fallback.log'
    [IO.File]::WriteAllText(
        $fallbackPath,
        (($fallback -join [Environment]::NewLine) +
            [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    Assert-RebornControlledHostPrivacyEvidence `
        $fallbackPath `
        -Profile Fallback `
        -ObservedDuration ([TimeSpan]::FromMinutes(1)) `
        -RequireStopped | Out-Null

    $commonOutOfOrder = @($valid)
    $commonOutOfOrder[2] = $valid[3]
    $commonOutOfOrder[3] = $valid[2]
    $commonOutOfOrderPath =
        Join-Path $root 'common-out-of-order.log'
    [IO.File]::WriteAllText(
        $commonOutOfOrderPath,
        (($commonOutOfOrder -join [Environment]::NewLine) +
            [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        Assert-RebornControlledHostPrivacyEvidence `
            $commonOutOfOrderPath `
            -Profile Baseline `
            -ObservedDuration ([TimeSpan]::FromMinutes(1)) `
            -RequireStopped | Out-Null
    } 'complete common evidence in the wrong order'

    $fallbackOutOfOrder = @($fallback)
    $fallbackIndex = [Array]::IndexOf(
        $fallbackOutOfOrder,
        '[secure-acceptance] one-way TLS fallback observed')
    $correctionIndex = [Array]::IndexOf(
        $fallbackOutOfOrder,
        '[secure-acceptance] authoritative correction forced reason=not_ready')
    $fallbackOutOfOrder[$fallbackIndex] =
        $fallback[$correctionIndex]
    $fallbackOutOfOrder[$correctionIndex] =
        $fallback[$fallbackIndex]
    $fallbackOutOfOrderPath =
        Join-Path $root 'fallback-out-of-order.log'
    [IO.File]::WriteAllText(
        $fallbackOutOfOrderPath,
        (($fallbackOutOfOrder -join [Environment]::NewLine) +
            [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        Assert-RebornControlledHostPrivacyEvidence `
            $fallbackOutOfOrderPath `
            -Profile Fallback `
            -ObservedDuration ([TimeSpan]::FromMinutes(1)) `
            -RequireStopped | Out-Null
    } 'complete fallback evidence in the wrong order'

    $lateEnabled = @(
        $fallback |
            Where-Object {
                $_ -cne
                    '[secure-acceptance] phase4 fault campaign enabled'
            }
    )
    $lateEnabled = @(
        $lateEnabled[0..7]
        '[secure-acceptance] phase4 fault campaign enabled'
        $lateEnabled[8..($lateEnabled.Count - 1)]
    )
    $lateEnabledPath = Join-Path $root 'late-enabled.log'
    [IO.File]::WriteAllText(
        $lateEnabledPath,
        (($lateEnabled -join [Environment]::NewLine) +
            [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        Assert-RebornControlledHostPrivacyEvidence `
            $lateEnabledPath `
            -Profile Fallback `
            -ObservedDuration ([TimeSpan]::FromMinutes(1)) `
            -RequireStopped | Out-Null
    } 'fault activation recorded after common connection evidence'

    Assert-Throws {
        Assert-RebornControlledHostPrivacyEvidence `
            $fallbackPath `
            -Profile Baseline `
            -ObservedDuration ([TimeSpan]::FromMinutes(1)) `
            -RequireStopped | Out-Null
    } 'baseline profile containing a fault event'
    Assert-Throws {
        Assert-RebornControlledHostPrivacyEvidence `
            $path `
            -Profile Fallback `
            -ObservedDuration ([TimeSpan]::FromMinutes(1)) `
            -RequireStopped | Out-Null
    } 'fallback profile missing its required fault events'
    Assert-Throws {
        Assert-RebornControlledHostPrivacyEvidence `
            $path `
            -Profile Soak `
            -ObservedDuration ([TimeSpan]::FromMinutes(9.99)) `
            -RequireStopped | Out-Null
    } 'soak profile shorter than ten minutes'
    Assert-RebornControlledHostPrivacyEvidence `
        $path `
        -Profile Soak `
        -ObservedDuration ([TimeSpan]::FromMinutes(10)) `
        -RequireStopped | Out-Null

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
