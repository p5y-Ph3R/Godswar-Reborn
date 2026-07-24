[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$originHash =
    'E0F5BC951C6E37550F4D9CC1E25BFDCB4F020466ADD854DC2E7EA04E0D22F81C'
$shimHash =
    'EF531F8CB20A4FCA8D1DBA979FD131ECA002383AE862890435426DF948817597'
$legacyHash =
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
Import-Module (
    Join-Path $PSScriptRoot 'ClientNetworkShimWindowsEvidence.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ClientNetworkShimParityEvidence.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ClientNetworkShimParityValidation.psm1'
) -Force

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$artifactParent = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'artifacts\network-shim-windows-evidence-tests')
).TrimEnd('\')
New-Item -ItemType Directory -Force -Path $artifactParent | Out-Null
$testRoot = Join-Path $artifactParent ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$testPath = Join-Path $testRoot 'locked.bin'
[IO.File]::WriteAllText($testPath, 'restart-manager-test')

try {
    $current = Get-Process -Id $PID
    $expectedStart = $current.StartTime.ToUniversalTime().ToFileTimeUtc()
    $stream = [IO.File]::Open(
        $testPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $users = @(Get-ParityRestartManagerFileUsers $testPath)
        $matching = @(
            $users |
                Where-Object {
                    $_.processId -eq $PID -and
                    $_.processStartFileTimeUtc -eq $expectedStart
                }
        )
        Assert-True (
            $matching.Count -eq 1
        ) 'Restart Manager did not identify the exact locking process.'
    }
    finally {
        $stream.Dispose()
    }

    $missing = Join-Path $testRoot 'missing.bin'
    try {
        Get-ParityRestartManagerFileUsers $missing | Out-Null
        throw 'A missing evidence file was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'does not exist') {
            throw
        }
    }

    $origins = @(Get-Process -Name Origin -ErrorAction SilentlyContinue)
    if ($origins.Count -eq 1) {
        $origin = $origins[0]
        $runtime = Get-ParityOriginRuntimeEvidence `
            -Process $origin `
            -ClientRoot $ClientRoot `
            -Stage ShimParity
        Assert-True (
            @($runtime.errors).Count -eq 0
        ) 'Live Origin runtime evidence contained validation errors.'
        Assert-True (
            $runtime.processPath.Equals(
                (Join-Path $ClientRoot 'Origin.exe'),
                [StringComparison]::OrdinalIgnoreCase)
        ) 'Cross-integrity Origin image path was not resolved.'
        Assert-True (
            $runtime.pathEvidenceSource -in @(
                'ProcessApi',
                'QueryFullProcessImageName'
            )
        ) 'Origin image path used an unsupported evidence source.'
        Assert-True (
            @($runtime.modules).Count -eq 2
        ) 'Runtime evidence did not identify both shim-stage DLLs.'

        $expectedStart = (
            $origin.StartTime.ToUniversalTime().ToFileTimeUtc()
        )
        $rmModules = @()
        foreach ($name in @('Net.dll', 'NetLegacy.dll')) {
            $path = Join-Path $ClientRoot $name
            $module = @(
                $runtime.modules |
                    Where-Object name -ieq $name
            )
            Assert-True (
                $module.Count -eq 1 -and
                ([string]$module[0].path).Equals(
                    $path,
                    [StringComparison]::OrdinalIgnoreCase) -and
                $module[0].diskSha256 -eq (
                    Get-FileHash -LiteralPath $path -Algorithm SHA256
                ).Hash
            ) "Runtime evidence mapping is invalid for $name."

            $fileUsers = @(
                Get-ParityRestartManagerFileUsers $path |
                    Where-Object {
                        $_.processId -eq $origin.Id -and
                        $_.processStartFileTimeUtc -eq $expectedStart -and
                        $_.applicationName -ieq 'Origin.exe'
                    }
            )
            Assert-True (
                $fileUsers.Count -eq 1 -and
                ([string]$fileUsers[0].resourcePath).Equals(
                    $path,
                    [StringComparison]::OrdinalIgnoreCase)
            ) "Restart Manager mapping is invalid for $name."
            $rmModules += [pscustomobject][ordered]@{
                name = $name
                path = $path
                baseAddress = $null
                memorySize = $null
                diskSha256 = (
                    Get-FileHash -LiteralPath $path -Algorithm SHA256
                ).Hash
                evidenceSource = 'RestartManagerFileUse'
                locker = $fileUsers[0]
            }
        }

        $gameConnection = @()
        if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
            $gameConnection = @(
                Get-NetTCPConnection -OwningProcess $origin.Id `
                    -ErrorAction SilentlyContinue |
                    Where-Object {
                        "$($_.RemoteAddress):$($_.RemotePort)" -eq
                            '127.1.1.110:7000' -and
                        [string]$_.State -eq 'Established'
                    } |
                    Select-Object -First 1
            )
        }
        $install = $null
        if ($gameConnection.Count -eq 1) {
            $install = Get-ParityClientSnapshot `
                $ClientRoot $originHash $shimHash $legacyHash
        }
        if ($gameConnection.Count -eq 1 -and
            $install.state -eq 'InstalledExact') {
            $startedUtc = $origin.StartTime.ToUniversalTime()
            $observation = [pscustomobject][ordered]@{
                schemaVersion = 1
                runId = 'live-windows-evidence-test'
                observedUtc = [DateTime]::UtcNow.ToString('O')
                stage = 'ShimParity'
                accountId = 7
                process = [pscustomobject][ordered]@{
                    id = $origin.Id
                    startedUtc = $startedUtc.ToString('O')
                    startFileTimeUtc = $expectedStart
                    path = Join-Path $ClientRoot 'Origin.exe'
                    pathEvidenceSource = $runtime.pathEvidenceSource
                    pathLocker = $null
                }
                install = $install
                modules = $rmModules
                connections = @(
                    [pscustomobject][ordered]@{
                        local = 'live'
                        remote = '127.1.1.110:7000'
                        state = 'Established'
                    }
                )
                passed = $true
                validationErrors = @()
            }
            $semanticErrors = @(
                Get-ParityObservationValidationErrors `
                    @($observation) $ClientRoot `
                    $originHash $shimHash $legacyHash `
                    '127.1.1.110:7000' `
                    $startedUtc.AddSeconds(-1).ToString('O') `
                    ([DateTimeOffset]::UtcNow)
            )
            Assert-True (
                $semanticErrors.Count -eq 0
            ) (
                'Live Restart Manager observation failed semantic ' +
                "validation: $($semanticErrors -join '; ')"
            )

            $tampered = $observation |
                ConvertTo-Json -Depth 12 |
                ConvertFrom-Json
            $tampered.modules[0].locker.resourcePath = (
                Join-Path $ClientRoot 'wrong.dll'
            )
            $tamperedErrors = @(
                Get-ParityObservationValidationErrors `
                    @($tampered) $ClientRoot `
                    $originHash $shimHash $legacyHash `
                    '127.1.1.110:7000' `
                    $startedUtc.AddSeconds(-1).ToString('O') `
                    ([DateTimeOffset]::UtcNow)
            )
            Assert-True (
                @(
                    $tamperedErrors |
                        Where-Object {
                            $_ -match 'invalid Net\.dll evidence'
                        }
                ).Count -gt 0
            ) 'A mismatched Restart Manager resource path was accepted.'
        } elseif ($gameConnection.Count -eq 1) {
            Write-Host (
                'Live V2 semantic observation skipped: client state is ' +
                "$($install.state)."
            )
        }
        Write-Host 'Live Origin process and DLL evidence passed.'
    } else {
        Write-Host 'Live elevated-Origin fallback skipped: Origin count is not one.'
    }

    Write-Host 'Windows process-evidence checks passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if (-not $resolved.StartsWith(
            $artifactParent + '\',
            [StringComparison]::OrdinalIgnoreCase) -or
        $resolved -eq $artifactParent) {
        throw 'Refusing test cleanup outside the artifact root.'
    }
    if (Test-Path -LiteralPath $testPath -PathType Leaf) {
        Remove-Item -LiteralPath $testPath -Force
    }
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        Remove-Item -LiteralPath $resolved -Force
    }
}
