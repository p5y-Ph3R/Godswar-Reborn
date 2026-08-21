[CmdletBinding()]
param(
    [string]$ConfigurationDirectory,

    [ValidateRange(5, 120)]
    [int]$ReadyTimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot `
    'tools/Godswar.Server.CombatDummyHost/Godswar.Server.CombatDummyHost.csproj'
$outputRoot = Join-Path $repositoryRoot `
    'artifacts/development-combat-dummies'
$statePath = Join-Path $outputRoot 'owner.json'
$readinessPath = Join-Path $outputRoot 'readiness.json'
$startLockPath = Join-Path $outputRoot 'start.lock'
$hostLockPath = Join-Path $outputRoot 'host.lock'
$stdoutPath = Join-Path $outputRoot 'host.stdout.log'
$stderrPath = Join-Path $outputRoot 'host.stderr.log'
$executable = Join-Path $repositoryRoot `
    'tools/Godswar.Server.CombatDummyHost/bin/Release/net10.0/Godswar.Server.CombatDummyHost.exe'

function Get-LogTail {
    param([Parameter(Mandatory)] [string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ''
    }

    return ((Get-Content -LiteralPath $Path -Tail 20) -join ' | ').Trim()
}

function Test-ReadySnapshot {
    param(
        [Parameter(Mandatory)] [object]$Snapshot,
        [Parameter(Mandatory)] [int]$ProcessId,
        [Parameter(Mandatory)] [string]$Manifest
    )

    try {
        if ([int]$Snapshot.ProcessId -ne $ProcessId -or
            [string]$Snapshot.IdentityManifest -cne $Manifest -or
            $Snapshot.AllReady -ne $true -or
            [int]$Snapshot.ReadyCount -ne 4 -or
            [int]$Snapshot.ExpectedCount -ne 4) {
            return $false
        }
        $dummies = @($Snapshot.Dummies)
        if ($dummies.Count -ne 4 -or
            (($dummies | Sort-Object CharacterId | ForEach-Object {
                [int]$_.CharacterId
            }) -join ',') -cne '7001,7002,7003,7004' -or
            @($dummies | Where-Object {
                $_.Ready -ne $true -or $_.Status -cne 'Ready'
            }).Count -ne 0) {
            return $false
        }
        $updated = [DateTimeOffset]::Parse(
            [string]$Snapshot.UpdatedAtUtc,
            [Globalization.CultureInfo]::InvariantCulture)
        $age = [DateTimeOffset]::UtcNow - $updated
        return $age.TotalSeconds -ge -5 -and $age.TotalSeconds -le 75
    } catch {
        return $false
    }
}

[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
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

    & (Join-Path $PSScriptRoot 'TestDevelopmentStackIsolation.ps1') `
        -ConfigurationDirectory $ConfigurationDirectory `
        -RequireLive | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'The isolated development stack did not pass its live guard.'
    }

    & dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'The development combat-dummy host did not build.'
    }
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "The expected dummy-host executable is missing: $executable"
    }

    & $executable --self-test | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'The combat-dummy host self-test failed.'
    }

    $expectedManifest = (& $executable --print-identity-manifest).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($expectedManifest)) {
        throw 'The dummy host did not expose its immutable identity manifest.'
    }

    $serverEnvironment = @(
        & docker inspect godswar-dev-tempest-openworld-01 `
            --format '{{range .Config.Env}}{{println .}}{{end}}')
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not inspect the isolated development server container.'
    }
    if ($serverEnvironment -notcontains `
        'GODSWAR_TRAINING_DUMMIES_ENABLED=true') {
        throw 'The development server does not enable training dummies.'
    }
    $identityEntry = @($serverEnvironment | Where-Object {
        $_ -clike 'GODSWAR_TRAINING_DUMMY_IDENTITIES=*'
    })
    if ($identityEntry.Count -ne 1) {
        throw 'The development server has no unique dummy identity manifest.'
    }
    $actualManifest = $identityEntry[0].Substring(
        'GODSWAR_TRAINING_DUMMY_IDENTITIES='.Length)
    if (-not $actualManifest.Equals(
            $expectedManifest,
            [StringComparison]::Ordinal)) {
        throw 'The running server and dummy host identity manifests differ.'
    }

    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        try {
            $state = Get-Content -LiteralPath $statePath -Raw |
                ConvertFrom-Json
            $existing = Get-Process -Id ([int]$state.ProcessId) `
                -ErrorAction SilentlyContinue
        } catch {
            throw 'The combat-dummy host state is corrupt; inspect it before retrying.'
        }

        if ($null -ne $existing) {
            $path = $null
            try { $path = $existing.Path } catch { $path = $null }
            $startTicks = $null
            try {
                $startTicks = $existing.StartTime.ToUniversalTime().Ticks
            } catch { $startTicks = $null }
            if ($null -eq $path -or $null -eq $startTicks -or
                -not [IO.Path]::GetFullPath($path).Equals(
                    [IO.Path]::GetFullPath($executable),
                    [StringComparison]::OrdinalIgnoreCase) -or
                [long]$state.ProcessStartTimeUtcTicks -ne $startTicks) {
                throw 'The recorded dummy-host PID belongs to another process.'
            }

            if (Test-Path -LiteralPath $readinessPath -PathType Leaf) {
                $ready = Get-Content -LiteralPath $readinessPath -Raw |
                    ConvertFrom-Json
                if (Test-ReadySnapshot $ready $existing.Id `
                        $expectedManifest) {
                    Write-Host "Development combat dummies are ready (PID $($existing.Id))."
                    return
                }
            }

            throw 'A dummy host is running but all four sessions are not ready.'
        }

        Remove-Item -LiteralPath $statePath -Force
    }

    $hostLockProbe = $null
    try {
        try {
            $hostLockProbe = [IO.File]::Open(
                $hostLockPath,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
        } catch [IO.IOException] {
            throw 'A host owns the singleton lock without valid owner state.'
        }
    } finally {
        if ($null -ne $hostLockProbe) { $hostLockProbe.Dispose() }
    }

    Remove-Item -LiteralPath $readinessPath -Force `
        -ErrorAction SilentlyContinue
    $process = Start-Process `
        -FilePath $executable `
        -ArgumentList @(
            '--host', '127.1.1.111',
            '--game-port', '7000',
            '--identity-manifest', $expectedManifest,
            '--readiness-file', $readinessPath,
            '--singleton-file', $hostLockPath,
            '--owner-file', $statePath) `
        -WorkingDirectory $repositoryRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru

    try {
        $processStartTicks = $process.StartTime.ToUniversalTime().Ticks

        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(
            $ReadyTimeoutSeconds)
        $ready = $null
        $readyAccepted = $false
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) {
                $stderr = Get-LogTail -Path $stderrPath
                throw "Combat-dummy host exited during startup: $stderr"
            }

            $ownerMatches = $false
            if (Test-Path -LiteralPath $statePath -PathType Leaf) {
                try {
                    $owner = Get-Content -LiteralPath $statePath -Raw |
                        ConvertFrom-Json
                    $ownerMatches = [int]$owner.ProcessId -eq $process.Id -and
                        [long]$owner.ProcessStartTimeUtcTicks -eq `
                            $processStartTicks -and
                        [string]$owner.IdentityManifest -ceq `
                            $expectedManifest -and
                        [IO.Path]::GetFullPath([string]$owner.Executable).Equals(
                            [IO.Path]::GetFullPath($executable),
                            [StringComparison]::OrdinalIgnoreCase)
                } catch {
                    $ownerMatches = $false
                }
            }
            if ($ownerMatches -and
                (Test-Path -LiteralPath $readinessPath -PathType Leaf)) {
                try {
                    $candidate = Get-Content -LiteralPath $readinessPath -Raw |
                        ConvertFrom-Json
                    $ready = $candidate
                    if (Test-ReadySnapshot $candidate $process.Id `
                            $expectedManifest) {
                        $readyAccepted = $true
                        break
                    }
                } catch {
                    $ready = $null
                }
            }

            Start-Sleep -Milliseconds 250
        }

        if (-not $readyAccepted) {
            $details = if ($null -eq $ready) {
                'no valid readiness snapshot'
            } else {
                (@($ready.Dummies | ForEach-Object {
                    "$($_.CharacterName)=$($_.Status):$($_.Detail)"
                }) -join '; ')
            }
            throw "All four combat dummies were not ready: $details"
        }

        Write-Host "Development combat dummies ready (PID $($process.Id), 4/4)."
        Write-Host "Log: $stdoutPath"
    } catch {
        $startFailure = $_
        $exited = $false
        try {
            $process.Refresh()
            $exited = $process.HasExited
        } catch { $exited = $false }
        if (-not $exited) {
            Stop-Process -InputObject $process `
                -ErrorAction SilentlyContinue
            Wait-Process -InputObject $process -Timeout 10 `
                -ErrorAction SilentlyContinue
            try {
                $process.Refresh()
                $exited = $process.HasExited
            } catch { $exited = $false }
        }
        if (-not $exited) {
            throw [InvalidOperationException]::new(
                "Combat dummy startup failed and PID $($process.Id) could " +
                "not be terminated; owner/readiness metadata was retained. " +
                "Original failure: $($startFailure.Exception.Message)",
                $startFailure.Exception)
        }
        Remove-Item -LiteralPath $statePath -Force `
            -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $readinessPath -Force `
            -ErrorAction SilentlyContinue
        throw $startFailure
    }
} finally {
    if ($null -ne $startLock) {
        $startLock.Dispose()
    }
}
