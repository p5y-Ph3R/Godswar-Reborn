Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'ControlledHostClientRootLease.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'ControlledHostRuntimeCleanupReceipt.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

function Invoke-RebornControlledHostRuntimeCleanup {
    param(
        [Parameter(Mandatory)][string]$ReceiptPath,
        [ValidateSet(
            'None',
            'AfterRename',
            'AfterFirstChildDelete',
            'BeforeRootDelete')]
        [string]$FaultAfter = 'None',
        [switch]$AllowTestPath
    )

    $receipt =
        Read-RebornControlledHostRuntimeCleanupReceipt `
            $ReceiptPath -AllowTestPath:$AllowTestPath
    if ($receipt.Record.state -ceq 'Removed') {
        if ((Test-Path -LiteralPath $receipt.RuntimeRoot) -or
            (Test-Path -LiteralPath $receipt.TombstoneRoot)) {
            throw 'Removed cleanup receipt still has a runtime directory.'
        }
        return $receipt
    }
    if ($receipt.Record.state -ceq 'Prepared') {
        $sourceExists =
            Test-Path -LiteralPath $receipt.RuntimeRoot -PathType Container
        $tombstoneExists =
            Test-Path -LiteralPath $receipt.TombstoneRoot -PathType Container
        if ($sourceExists -and -not $tombstoneExists) {
            $lease = Enter-RebornControlledHostDirectoryLease `
                $receipt.RuntimeRoot
            try {
                if ([string]$lease.Identity -cne
                        [string]$receipt.Record.runtimeIdentity) {
                    throw 'Runtime cleanup source identity changed.'
                }
                Assert-RuntimeCleanupRemainingTree `
                    $receipt $receipt.RuntimeRoot | Out-Null
                Assert-RebornControlledHostDirectoryLease $lease |
                    Out-Null
            }
            finally {
                Exit-RebornControlledHostDirectoryLease $lease
            }
            [IO.Directory]::Move(
                $receipt.RuntimeRoot,
                $receipt.TombstoneRoot)
            if ($FaultAfter -ceq 'AfterRename') {
                throw 'Injected runtime cleanup fault after rename.'
            }
        } elseif ($sourceExists -or -not $tombstoneExists) {
            throw 'Prepared runtime cleanup has an ambiguous directory state.'
        }
        $lease = Enter-RebornControlledHostDirectoryLease `
            $receipt.TombstoneRoot
        try {
            if ([string]$lease.Identity -cne
                    [string]$receipt.Record.runtimeIdentity) {
                throw 'Runtime cleanup tombstone identity changed.'
            }
            Assert-RuntimeCleanupRemainingTree `
                $receipt $receipt.TombstoneRoot | Out-Null
            $receipt = Set-RuntimeCleanupState `
                $receipt Tombstoned -AllowTestPath:$AllowTestPath
        }
        finally {
            Exit-RebornControlledHostDirectoryLease $lease
        }
    }

    if (Test-Path -LiteralPath $receipt.RuntimeRoot) {
        throw 'Tombstoned cleanup unexpectedly restored the runtime root.'
    }
    $tombstoneExists = Test-Path `
        -LiteralPath $receipt.TombstoneRoot `
        -PathType Container
    if (-not $tombstoneExists) {
        return Set-RuntimeCleanupState `
            $receipt Removed -AllowTestPath:$AllowTestPath
    }

    $lease = Enter-RebornControlledHostDirectoryLease `
        $receipt.TombstoneRoot
    $released = $false
    try {
        if ([string]$lease.Identity -cne
                [string]$receipt.Record.runtimeIdentity) {
            throw 'Runtime cleanup tombstone identity changed.'
        }
        Assert-RuntimeCleanupRemainingTree `
            $receipt $receipt.TombstoneRoot | Out-Null
        $deleted = 0
        foreach ($entry in @($receipt.Entries |
                Where-Object kind -CEQ 'File')) {
            $path = Join-Path `
                $receipt.TombstoneRoot ([string]$entry.relativePath)
            if (-not (Test-Path -LiteralPath $path)) {
                continue
            }
            $file = Assert-RebornSingleLinkRegularFilePath `
                $path 'runtime cleanup file deletion'
            if ([IO.FileInfo]::new($file).Length -ne
                    [Int64]$entry.length -or
                (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash `
                    -cne [string]$entry.sha256) {
                throw 'Runtime cleanup file changed before deletion.'
            }
            [IO.File]::Delete($file)
            $deleted++
            if ($FaultAfter -ceq 'AfterFirstChildDelete' -and
                $deleted -eq 1) {
                throw 'Injected runtime cleanup fault after child deletion.'
            }
        }
        $directories = @(
            $receipt.Entries |
                Where-Object kind -CEQ 'Directory' |
                Sort-Object {
                    ([string]$_.relativePath).Split('\').Count
                }, {
                    [string]$_.relativePath
                } -Descending
        )
        foreach ($entry in $directories) {
            $path = Join-Path `
                $receipt.TombstoneRoot ([string]$entry.relativePath)
            if (-not (Test-Path -LiteralPath $path)) {
                continue
            }
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                ($attributes -band
                    [IO.FileAttributes]::Directory) -eq 0 -or
                [IO.Directory]::EnumerateFileSystemEntries(
                    $path).GetEnumerator().MoveNext()) {
                throw 'Runtime cleanup directory is not exactly empty.'
            }
            [IO.Directory]::Delete($path)
        }
        Assert-RebornControlledHostDirectoryLease $lease | Out-Null
        if ([IO.Directory]::EnumerateFileSystemEntries(
                $receipt.TombstoneRoot).GetEnumerator().MoveNext()) {
            throw 'Runtime cleanup tombstone root is not empty.'
        }
        Exit-RebornControlledHostDirectoryLease $lease
        $released = $true
        if ($FaultAfter -ceq 'BeforeRootDelete') {
            throw 'Injected runtime cleanup fault before root deletion.'
        }
        $identityCheck = Enter-RebornControlledHostDirectoryLease `
            $receipt.TombstoneRoot
        try {
            if ([string]$identityCheck.Identity -cne
                    [string]$receipt.Record.runtimeIdentity -or
                [IO.Directory]::EnumerateFileSystemEntries(
                    $receipt.TombstoneRoot).GetEnumerator().MoveNext()) {
                throw 'Runtime cleanup empty-root identity changed.'
            }
        }
        finally {
            Exit-RebornControlledHostDirectoryLease $identityCheck
        }
        [IO.Directory]::Delete($receipt.TombstoneRoot)
    }
    finally {
        if (-not $released) {
            Exit-RebornControlledHostDirectoryLease $lease
        }
    }
    return Set-RuntimeCleanupState `
        $receipt Removed -AllowTestPath:$AllowTestPath
}

Export-ModuleMember -Function (
    'Invoke-RebornControlledHostRuntimeCleanup'
)
