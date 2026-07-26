Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

function Get-RebornControlledHostRuntimeLockName {
    param([Parameter(Mandatory)][string]$RuntimeRoot)

    $runtime = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\')
    $bytes = [Text.Encoding]::UTF8.GetBytes(
        $runtime.ToUpperInvariant())
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $digest = $null
    try {
        $digest = $algorithm.ComputeHash($bytes)
        $suffix = ([BitConverter]::ToString($digest)).Replace('-', '')
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        if ($null -ne $digest) {
            [Array]::Clear($digest, 0, $digest.Length)
        }
        $algorithm.Dispose()
    }
    return "Global\RebornControlledHostRuntimeV1-$suffix"
}

function Enter-RebornControlledHostRuntimeLock {
    param(
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)][string]$Purpose
    )

    $name = Get-RebornControlledHostRuntimeLockName $RuntimeRoot
    $mutex = [Threading.Mutex]::new($false, $name)
    try {
        try {
            $acquired = $mutex.WaitOne(0)
        }
        catch [Threading.AbandonedMutexException] {
            $acquired = $true
        }
        if (-not $acquired) {
            throw (
                'The protected controlled-host runtime is already leased; ' +
                "$Purpose cannot continue.")
        }
        return [pscustomobject]@{
            Name = $name
            Purpose = $Purpose
            Mutex = $mutex
            Acquired = $true
        }
    }
    catch {
        $mutex.Dispose()
        throw
    }
}

function Exit-RebornControlledHostRuntimeLock {
    param([Parameter(Mandatory)][object]$Lock)

    if ($Lock.Acquired -ne $true -or
        $Lock.Mutex -isnot [Threading.Mutex]) {
        throw 'The controlled-host runtime lease is invalid.'
    }
    try {
        $Lock.Mutex.ReleaseMutex()
    }
    finally {
        $Lock.Mutex.Dispose()
        $Lock.Acquired = $false
    }
}

function Enter-RebornControlledHostRuntimeSetLock {
    $parent = [IO.Path]::GetFullPath(
        (Join-Path (
            [Environment]::GetFolderPath('CommonApplicationData')
        ) 'RebornSecureNetworkRuntime')).TrimEnd('\')
    $locks = [Collections.Generic.List[object]]::new()
    try {
        if (Test-Path -LiteralPath $parent) {
            Assert-RebornDirectoryPath `
                $parent 'controlled-host runtime parent' | Out-Null
            $entries = @(
                Get-ChildItem -LiteralPath $parent -Force |
                    Sort-Object -Property Name
            )
            foreach ($entry in $entries) {
                if (-not $entry.PSIsContainer -or
                    $entry.Name -cnotmatch '^\d{8}-\d{6}$' -or
                    ($entry.Attributes -band
                        [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw (
                        'Controlled-host runtime parent contains an ' +
                        "unexpected entry: $($entry.Name)")
                }
                $locks.Add(
                    (Enter-RebornControlledHostRuntimeLock `
                        $entry.FullName 'secure bundle mutation'))
            }
        }
        return [pscustomobject]@{
            RuntimeParent = $parent
            Locks = $locks.ToArray()
        }
    }
    catch {
        for ($index = $locks.Count - 1; $index -ge 0; $index--) {
            Exit-RebornControlledHostRuntimeLock $locks[$index]
        }
        throw
    }
}

function Exit-RebornControlledHostRuntimeSetLock {
    param([Parameter(Mandatory)][object]$LockSet)

    $locks = @($LockSet.Locks)
    for ($index = $locks.Count - 1; $index -ge 0; $index--) {
        Exit-RebornControlledHostRuntimeLock $locks[$index]
    }
}

Export-ModuleMember -Function @(
    'Get-RebornControlledHostRuntimeLockName',
    'Enter-RebornControlledHostRuntimeLock',
    'Exit-RebornControlledHostRuntimeLock',
    'Enter-RebornControlledHostRuntimeSetLock',
    'Exit-RebornControlledHostRuntimeSetLock'
)
