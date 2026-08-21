[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$statePath = Join-Path $repositoryRoot `
    'artifacts/development-combat-dummies/owner.json'
$readinessPath = Join-Path $repositoryRoot `
    'artifacts/development-combat-dummies/readiness.json'
$startLockPath = Join-Path $repositoryRoot `
    'artifacts/development-combat-dummies/start.lock'
$expectedExecutable = Join-Path $repositoryRoot `
    'tools/Godswar.Server.CombatDummyHost/bin/Release/net10.0/Godswar.Server.CombatDummyHost.exe'

[IO.Directory]::CreateDirectory(
    (Split-Path -Parent $statePath)) | Out-Null
$startLock = $null
try {
    try {
        $startLock = [IO.File]::Open(
            $startLockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    } catch [IO.IOException] {
        throw 'Another combat-dummy start/stop operation is in progress.'
    }

    $state = $null
    $process = $null
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        try {
            $state = Get-Content -LiteralPath $statePath -Raw |
                ConvertFrom-Json
        } catch {
            throw 'The combat-dummy host state is corrupt; refusing PID action.'
        }
        $process = Get-Process -Id ([int]$state.ProcessId) `
            -ErrorAction SilentlyContinue
    } else {
        $hostLockPath = Join-Path $repositoryRoot `
            'artifacts/development-combat-dummies/host.lock'
        $hostLockProbe = $null
        $hostLockOwned = $false
        try {
            try {
                $hostLockProbe = [IO.File]::Open(
                    $hostLockPath,
                    [IO.FileMode]::OpenOrCreate,
                    [IO.FileAccess]::ReadWrite,
                    [IO.FileShare]::None)
            } catch [IO.IOException] {
                $hostLockOwned = $true
            }
        } finally {
            if ($null -ne $hostLockProbe) { $hostLockProbe.Dispose() }
        }
        if (-not $hostLockOwned) {
            Write-Host 'No development combat-dummy host state was found.'
            return
        }

        $candidates = @(Get-Process | Where-Object {
            $candidatePath = $null
            try { $candidatePath = $_.Path } catch { $candidatePath = $null }
            $null -ne $candidatePath -and
                [IO.Path]::GetFullPath($candidatePath).Equals(
                    [IO.Path]::GetFullPath($expectedExecutable),
                    [StringComparison]::OrdinalIgnoreCase)
        })
        if ($candidates.Count -ne 1) {
            throw 'A host owns the lock, but its process cannot be uniquely recovered.'
        }
        $process = $candidates[0]
        $state = [pscustomobject]@{
            ProcessId = $process.Id
            ProcessStartTimeUtcTicks =
                $process.StartTime.ToUniversalTime().Ticks
        }
    }

    $processId = [int]$state.ProcessId
    if ($null -eq $process) {
        Remove-Item -LiteralPath $statePath -Force `
            -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $readinessPath -Force `
            -ErrorAction SilentlyContinue
        Write-Host "Cleaned stale combat-dummy state for PID $processId."
        return
    }

    $actualPath = $null
    $actualStartTicks = $null
    try {
        $actualPath = $process.Path
        $actualStartTicks = $process.StartTime.ToUniversalTime().Ticks
    } catch {
        $actualPath = $null
        $actualStartTicks = $null
    }
    if ($null -eq $actualPath -or $null -eq $actualStartTicks -or
        -not [IO.Path]::GetFullPath($actualPath).Equals(
            [IO.Path]::GetFullPath($expectedExecutable),
            [StringComparison]::OrdinalIgnoreCase) -or
        [long]$state.ProcessStartTimeUtcTicks -ne $actualStartTicks) {
        throw 'Refusing to stop a PID that is not the recorded dummy host.'
    }

    if ($PSCmdlet.ShouldProcess(
            "PID $processId",
            'Stop the isolated development combat-dummy host')) {
        $approvedProcess = Get-Process -Id $processId -ErrorAction Stop
        try {
            $approvedPath = $approvedProcess.Path
            $approvedStartTicks =
                $approvedProcess.StartTime.ToUniversalTime().Ticks
        } catch {
            throw 'Could not revalidate the approved dummy-host process.'
        }
        if (-not [IO.Path]::GetFullPath($approvedPath).Equals(
                [IO.Path]::GetFullPath($expectedExecutable),
                [StringComparison]::OrdinalIgnoreCase) -or
            [long]$state.ProcessStartTimeUtcTicks -ne $approvedStartTicks) {
            throw 'The approved dummy-host PID changed before termination.'
        }
        Stop-Process -InputObject $approvedProcess
        Wait-Process -InputObject $approvedProcess -Timeout 10 `
            -ErrorAction SilentlyContinue
        $approvedProcess.Refresh()
        if (-not $approvedProcess.HasExited) {
            throw "Combat-dummy host PID $processId did not stop."
        }

        Remove-Item -LiteralPath $statePath -Force `
            -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $readinessPath -Force `
            -ErrorAction SilentlyContinue
        Write-Host "Development combat-dummy host PID $processId stopped."
    }
} finally {
    if ($null -ne $startLock) {
        $startLock.Dispose()
    }
}
