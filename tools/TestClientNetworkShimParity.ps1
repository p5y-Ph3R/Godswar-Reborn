[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$ApplyBackupPath =
        'C:\Reborn\backups\client-network-shim-v1-Apply-20260724-112517594'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $PSScriptRoot 'InvokeClientNetworkShimParity.ps1'
$module = Join-Path $PSScriptRoot 'ClientNetworkShimParityEvidence.psm1'
$validationModule = Join-Path `
    $PSScriptRoot 'ClientNetworkShimParityValidation.psm1'
Import-Module $module -Force
Import-Module $validationModule -Force

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Operation,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$ExpectedPattern
    )

    try {
        & $Operation
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedPattern) {
            throw (
                "Wrong refusal for ${Label}: $($_.Exception.Message)"
            )
        }
        Write-Host "Expected refusal ($Label): $($_.Exception.Message)"
        return
    }

    throw "Expected operation to be refused: $Label"
}

if (@(Get-Process -Name Origin -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close Origin.exe before running the parity evidence tests.'
}

$artifactParent = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'artifacts\network-shim-parity-tests')
).TrimEnd('\')
$testRoot = Join-Path $artifactParent ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

$originPath = Join-Path $ClientRoot 'Origin.exe'
$netPath = Join-Path $ClientRoot 'Net.dll'
$legacyPath = Join-Path $ClientRoot 'NetLegacy.dll'
$beforeHashes = @(
    Get-ParitySha256 $originPath
    Get-ParitySha256 $netPath
    Get-ParitySha256 $legacyPath
)

try {
    $begin = & $tool `
        -Mode Begin `
        -ClientRoot $ClientRoot `
        -OriginalApplyBackupPath $ApplyBackupPath `
        -EvidenceRoot $testRoot `
        -AllowDirtyRepository `
        -SkipServerChecks
    Assert-True ($begin.State -eq 'Pending') 'Begin did not return Pending.'
    Assert-True (
        $begin.EvidencePath.StartsWith(
            $testRoot + '\',
            [StringComparison]::OrdinalIgnoreCase)
    ) 'Begin wrote outside the disposable test root.'

    $manifestPath = Join-Path $begin.EvidencePath 'manifest.json'
    $checksumPath = Join-Path $begin.EvidencePath 'manifest.sha256'
    Assert-True (
        (Test-Path -LiteralPath $manifestPath -PathType Leaf) -and
        (Test-Path -LiteralPath $checksumPath -PathType Leaf)
    ) 'Begin did not create its immutable manifest pair.'

    $status = & $tool -Mode Status -EvidencePath $begin.EvidencePath
    Assert-True ($status.State -eq 'Pending') 'Status was not Pending.'
    Assert-True (
        $status.Observations.total -eq 0
    ) 'A fresh evidence run unexpectedly contained observations.'

    Assert-Throws {
        & $tool `
            -Mode Observe `
            -EvidencePath $begin.EvidencePath `
            -Stage ShimParity `
            -AccountId 7 | Out-Null
    } 'Observe without Origin' 'requires exactly one Origin\.exe'

    Assert-Throws {
        & $tool `
            -Mode Complete `
            -EvidencePath $begin.EvidencePath `
            -FinalApplyBackupPath $ApplyBackupPath `
            -CompletedCycles 5 `
            -SoakMinutes 1 `
            -ChecklistPassed `
            -LogsReviewed `
            -NoBehaviorDifference | Out-Null
    } 'Complete without observations and with test overrides' `
        'Test-only Begin/Complete overrides prohibit acceptance'
    Assert-True (
        -not (Test-Path -LiteralPath (
            Join-Path $begin.EvidencePath 'completion.json'
        ))
    ) 'Rejected Complete wrote a completion file.'

    Assert-Throws {
        Write-ParityJsonNew @{ value = 1 } $manifestPath
    } 'atomic no-overwrite' 'Evidence file already exists'

    $before = @(
        [pscustomobject]@{
            relativePath = 'changed.log'
            length = 1
            lastWriteUtc = '2026-01-01T00:00:00.0000000Z'
            sha256 = ('A' * 64)
        },
        [pscustomobject]@{
            relativePath = 'removed.log'
            length = 2
            lastWriteUtc = '2026-01-01T00:00:00.0000000Z'
            sha256 = ('B' * 64)
        }
    )
    $after = @(
        [pscustomobject]@{
            relativePath = 'changed.log'
            length = 3
            lastWriteUtc = '2026-01-02T00:00:00.0000000Z'
            sha256 = ('C' * 64)
        },
        [pscustomobject]@{
            relativePath = 'added.log'
            length = 4
            lastWriteUtc = '2026-01-02T00:00:00.0000000Z'
            sha256 = ('D' * 64)
        }
    )
    $difference = Compare-ParityInventory $before $after
    Assert-True (
        $difference.added.Count -eq 1 -and
        $difference.added[0] -eq 'added.log' -and
        $difference.changed.Count -eq 1 -and
        $difference.changed[0] -eq 'changed.log' -and
        $difference.removed.Count -eq 1 -and
        $difference.removed[0] -eq 'removed.log'
    ) 'Inventory difference was not deterministic.'

    $hiddenInventoryRoot = Join-Path $testRoot 'hidden-inventory'
    New-Item -ItemType Directory -Path $hiddenInventoryRoot | Out-Null
    $hiddenInventoryPath = Join-Path $hiddenInventoryRoot 'hidden.dmp'
    [IO.File]::WriteAllText($hiddenInventoryPath, 'test')
    [IO.File]::SetAttributes(
        $hiddenInventoryPath,
        [IO.File]::GetAttributes($hiddenInventoryPath) -bor
            [IO.FileAttributes]::Hidden)
    Assert-True (
        @(Get-ParityInventory $hiddenInventoryRoot).Count -eq 1
    ) 'Hidden dump evidence was omitted from inventory.'

    $tamperedRoot = Join-Path $testRoot 'tampered'
    Copy-Item -LiteralPath $begin.EvidencePath `
        -Destination $tamperedRoot -Recurse
    Add-Content -LiteralPath (
        Join-Path $tamperedRoot 'manifest.json'
    ) -Value ' '
    Assert-Throws {
        & $tool -Mode Status -EvidencePath $tamperedRoot | Out-Null
    } 'tampered manifest' 'Evidence manifest checksum mismatch'

    $afterHashes = @(
        Get-ParitySha256 $originPath
        Get-ParitySha256 $netPath
        Get-ParitySha256 $legacyPath
    )
    Assert-True (
        ($beforeHashes -join '|') -eq ($afterHashes -join '|')
    ) 'Evidence tooling changed an installed client binary.'

    $observationTestRoot = Join-Path $testRoot 'observation-checksum'
    Copy-Item -LiteralPath $begin.EvidencePath `
        -Destination $observationTestRoot -Recurse
    $manifest = Get-Content -LiteralPath (
        Join-Path $observationTestRoot 'manifest.json'
    ) -Raw | ConvertFrom-Json
    $observationPath = Join-Path (
        Join-Path $observationTestRoot 'observations'
    ) 'synthetic.json'
    Write-ParityJsonNew ([ordered]@{
        schemaVersion = 1
        runId = [string]$manifest.runId
        observedUtc = '2026-07-24T00:01:00Z'
        stage = 'ShimParity'
        accountId = 7
        process = [ordered]@{
            id = 123
            startedUtc = '2026-07-24T00:00:00Z'
            path = Join-Path $ClientRoot 'Origin.exe'
        }
        install = [ordered]@{
            originSupported = $true
            originSha256 = $beforeHashes[0]
            state = 'InstalledExact'
        }
        modules = @(
            [ordered]@{
                name = 'Net.dll'
                path = Join-Path $ClientRoot 'Net.dll'
                diskSha256 = $beforeHashes[1]
            },
            [ordered]@{
                name = 'NetLegacy.dll'
                path = Join-Path $ClientRoot 'NetLegacy.dll'
                diskSha256 = $beforeHashes[2]
            }
        )
        connections = @(
            [ordered]@{
                remote = '127.1.1.110:7000'
                state = 'Established'
            }
        )
        passed = $true
        validationErrors = @()
    }) $observationPath
    Write-ParityTextNew (
        (Get-ParitySha256 $observationPath) + [Environment]::NewLine
    ) "$observationPath.sha256"
    [IO.File]::SetAttributes(
        $observationPath,
        [IO.File]::GetAttributes($observationPath) -bor
            [IO.FileAttributes]::Hidden)
    $loaded = @(
        Get-ParityObservations $observationTestRoot $manifest.runId
    )
    Assert-True (
        $loaded.Count -eq 1 -and $loaded[0].accountId -eq 7
    ) 'A checksummed observation did not round-trip.'
    $semanticErrors = @(
        Get-ParityObservationValidationErrors `
            $loaded $ClientRoot $beforeHashes[0] $beforeHashes[1] `
            $beforeHashes[2] '127.1.1.110:7000' `
            '2026-07-24T00:00:00Z' `
            ([DateTimeOffset]'2026-07-24T01:00:00Z')
    )
    Assert-True (
        $semanticErrors.Count -eq 0
    ) 'A valid observation failed semantic revalidation.'
    $loaded[0].connections = @()
    Assert-True (
        @(
            Get-ParityObservationValidationErrors `
                $loaded $ClientRoot $beforeHashes[0] $beforeHashes[1] `
                $beforeHashes[2] '127.1.1.110:7000' `
                '2026-07-24T00:00:00Z' `
                ([DateTimeOffset]'2026-07-24T01:00:00Z')
        ).Count -gt 0
    ) 'Missing in-world game connection was accepted.'
    $loaded[0].connections = @(
        [pscustomobject]@{
            remote = '127.1.1.110:7000'
            state = 'Established'
        }
    )
    $loaded[0].process.startedUtc = '2026-07-23T23:59:59Z'
    $semanticErrors = @(
        Get-ParityObservationValidationErrors `
            $loaded $ClientRoot $beforeHashes[0] $beforeHashes[1] `
            $beforeHashes[2] '127.1.1.110:7000' `
            '2026-07-24T00:00:00Z' `
            ([DateTimeOffset]'2026-07-24T01:00:00Z')
    )
    Assert-True (
        ($semanticErrors -join "`n") -match 'invalid timestamps'
    ) 'A pre-run observation timestamp was accepted.'
    $loaded[0].process.startedUtc = '2026-07-24T00:00:00Z'
    $loaded[0].observedUtc = '2026-07-24T02:00:00Z'
    $semanticErrors = @(
        Get-ParityObservationValidationErrors `
            $loaded $ClientRoot $beforeHashes[0] $beforeHashes[1] `
            $beforeHashes[2] '127.1.1.110:7000' `
            '2026-07-24T00:00:00Z' `
            ([DateTimeOffset]'2026-07-24T01:00:00Z')
    )
    Assert-True (
        ($semanticErrors -join "`n") -match 'invalid timestamps'
    ) 'A future observation timestamp was accepted.'

    $sequenceBase = [DateTimeOffset]'2026-07-24T00:00:00Z'
    $sequence = @(
        for ($index = 0; $index -lt 5; $index++) {
            [pscustomobject]@{
                stage = 'ShimParity'
                accountId = if ($index % 2 -eq 0) { 7 } else { 13 }
                observedUtc = $sequenceBase.AddMinutes(
                    $index + 1
                ).ToString('O')
                process = [pscustomobject]@{
                    id = 100 + $index
                    startedUtc = $sequenceBase.AddMinutes(
                        $index + 1
                    ).AddSeconds(-30).ToString('O')
                }
                passed = $true
            }
        }
    )
    $sequence += [pscustomobject]@{
        stage = 'StockRollback'
        accountId = 7
        observedUtc = $sequenceBase.AddMinutes(7).ToString('O')
        process = [pscustomobject]@{
            id = 200
            startedUtc = $sequenceBase.AddMinutes(6).ToString('O')
        }
        passed = $true
    }
    $sequence += [pscustomobject]@{
        stage = 'FinalReapply'
        accountId = 7
        observedUtc = $sequenceBase.AddMinutes(9).ToString('O')
        process = [pscustomobject]@{
            id = 201
            startedUtc = $sequenceBase.AddMinutes(8).ToString('O')
        }
        passed = $true
    }
    $syntheticBackup = [pscustomobject]@{
        createdUtc = $sequenceBase.AddMinutes(7).AddSeconds(30).ToString('O')
    }
    Assert-True (
        @(
            Get-ParitySequenceValidationErrors `
                $sequence $syntheticBackup
        ).Count -eq 0
    ) 'A valid launch/rollback/reapply sequence was rejected.'
    $sequence[0].accountId = 13
    $sequenceErrors = @(
        Get-ParitySequenceValidationErrors `
            $sequence $syntheticBackup
    )
    Assert-True (
        ($sequenceErrors -join "`n") -match 'must start with account 7'
    ) 'A launch sequence beginning with account 13 was accepted.'
    $sequence[0].accountId = 7
    $syntheticBackup.createdUtc = $sequenceBase.AddMinutes(6).ToString('O')
    $sequenceErrors = @(
        Get-ParitySequenceValidationErrors `
            $sequence $syntheticBackup
    )
    Assert-True (
        ($sequenceErrors -join "`n") -match 'not created after stock rollback'
    ) 'A final backup predating stock rollback was accepted.'
    $syntheticBackup.createdUtc = $sequenceBase.AddMinutes(
        7
    ).AddSeconds(30).ToString('O')

    $failedMarkdown = New-ParityAcceptanceMarkdown ([pscustomobject]@{
        result = 'Fail'
        completedUtc = $sequenceBase.ToString('O')
        manualAttestation = [pscustomobject]@{
            operator = 'test'
            completedCycles = 0
            soakMinutes = 0
            logsReviewed = $false
            notes = ''
        }
        observationSummary = [pscustomobject]@{
            distinctPassingLaunches = 0
            passingByStage = [pscustomobject]@{
                StockRollback = 0
                FinalReapply = 0
            }
        }
        differences = [pscustomobject]@{
            dumps = [pscustomobject]@{
                added = @()
                changed = @()
                removed = @()
            }
        }
        finalApplyBackup = $null
        repository = [pscustomobject]@{ head = 'test' }
        finalInstall = [pscustomobject]@{
            originSha256 = 'test'
            netSha256 = 'test'
        }
        server = [pscustomobject]@{
            endpoints = @('test')
            imageId = 'test'
        }
    })
    Assert-True (
        $failedMarkdown -match 'missing / missing' -and
        $failedMarkdown -notmatch 'passed / passed'
    ) 'Failure Markdown falsely reported rollback/reapply success.'

    $completionTestRoot = Join-Path $testRoot 'completion-checksum'
    Copy-Item -LiteralPath $begin.EvidencePath `
        -Destination $completionTestRoot -Recurse
    $completionPath = Join-Path $completionTestRoot 'completion.json'
    Write-ParityJsonNew ([ordered]@{
        schemaVersion = 1
        runId = [string]$manifest.runId
        result = 'Fail'
    }) $completionPath
    Write-ParityTextNew (
        (Get-ParitySha256 $completionPath) + [Environment]::NewLine
    ) (Join-Path $completionTestRoot 'completion.sha256')
    Assert-True (
        (Read-ParityCompletion `
            $completionTestRoot $manifest.runId).result -eq 'Fail'
    ) 'A checksummed completion did not round-trip.'
    Add-Content -LiteralPath $completionPath -Value ' '
    Assert-Throws {
        Read-ParityCompletion `
            $completionTestRoot $manifest.runId | Out-Null
    } 'tampered completion' 'Completion checksum mismatch'

    Add-Content -LiteralPath $observationPath -Value ' '
    Assert-Throws {
        Get-ParityObservations `
            $observationTestRoot $manifest.runId | Out-Null
    } 'tampered observation' 'Observation checksum mismatch'

    $repositoryChanges = @(
        & git -C $repoRoot status --porcelain=v1
    )
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect repository cleanliness for integration test.'
    }
    if ($repositoryChanges.Count -eq 0) {
        & (Join-Path `
            $PSScriptRoot 'TestClientNetworkShimParityComplete.ps1') `
            -ClientRoot $ClientRoot `
            -ApplyBackupPath $ApplyBackupPath
    } else {
        Write-Host 'Clean-worktree Complete integration skipped: repository is dirty.'
    }

    Write-Host 'Client network shim parity evidence tests passed.'
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ($resolvedTestRoot.StartsWith(
            $artifactParent + '\',
            [StringComparison]::OrdinalIgnoreCase) -and
        $resolvedTestRoot -ne $artifactParent -and
        (Test-Path -LiteralPath $resolvedTestRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
