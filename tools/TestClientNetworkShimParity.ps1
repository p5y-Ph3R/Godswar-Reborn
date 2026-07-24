[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$ApplyBackupPath =
        'C:\Reborn\backups\client-network-shim-v1-Apply-20260724-150036083'
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
    $beginManifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json
    Assert-True (
        $beginManifest.toolVersion -eq '1.2.0' -and
        @($beginManifest.expected.checklist) -contains
            'avatar preview loading remains responsive' -and
        @($beginManifest.expected.checklist) -contains
            'avatar 3D model appears automatically without relogging' -and
        @($beginManifest.expected.checklist) -contains
            'no unintended behavior differences outside preview timing'
    ) 'Begin did not record the avatar-preview loading-gate contract.'

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
            -LogsReviewed | Out-Null
    } 'Complete without avatar-preview attestation' `
        'Avatar-preview loading-gate behavior was not attested'

    Assert-Throws {
        & $tool `
            -Mode Complete `
            -EvidencePath $begin.EvidencePath `
            -FinalApplyBackupPath $ApplyBackupPath `
            -CompletedCycles 5 `
            -SoakMinutes 1 `
            -ChecklistPassed `
            -LogsReviewed `
            -AvatarPreviewLoadingGatePassed | Out-Null
    } 'Complete without no-unintended-difference attestation' `
        'No-unintended-behavior-difference was not attested'

    Assert-Throws {
        & $tool `
            -Mode Complete `
            -EvidencePath $begin.EvidencePath `
            -FinalApplyBackupPath $ApplyBackupPath `
            -CompletedCycles 5 `
            -SoakMinutes 1 `
            -ChecklistPassed `
            -LogsReviewed `
            -AvatarPreviewLoadingGatePassed `
            -NoUnintendedBehaviorDifference | Out-Null
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

    $oldVersionRoot = Join-Path $testRoot 'old-tool-version'
    Copy-Item -LiteralPath $begin.EvidencePath `
        -Destination $oldVersionRoot -Recurse
    $oldManifestPath = Join-Path $oldVersionRoot 'manifest.json'
    $oldManifest = Get-Content -LiteralPath $oldManifestPath -Raw |
        ConvertFrom-Json
    $oldManifest.toolVersion = '0.0.0'
    $encoding = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText(
        $oldManifestPath,
        ($oldManifest | ConvertTo-Json -Depth 12),
        $encoding)
    [IO.File]::WriteAllText(
        (Join-Path $oldVersionRoot 'manifest.sha256'),
        (Get-ParitySha256 $oldManifestPath) + [Environment]::NewLine,
        $encoding)
    Assert-Throws {
        & $tool -Mode Status -EvidencePath $oldVersionRoot | Out-Null
    } 'old tool version' 'start a new run with 1\.2\.0'

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
            startFileTimeUtc = (
                [DateTimeOffset]'2026-07-24T00:00:00Z'
            ).UtcDateTime.ToFileTimeUtc()
            path = Join-Path $ClientRoot 'Origin.exe'
            pathEvidenceSource = 'ProcessApi'
            pathLocker = $null
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
                baseAddress = '0x10000000'
                memorySize = 4096
                diskSha256 = $beforeHashes[1]
                evidenceSource = 'ProcessModules'
                locker = $null
            },
            [ordered]@{
                name = 'NetLegacy.dll'
                path = Join-Path $ClientRoot 'NetLegacy.dll'
                baseAddress = '0x20000000'
                memorySize = 8192
                diskSha256 = $beforeHashes[2]
                evidenceSource = 'ProcessModules'
                locker = $null
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
    ) 'Observation round-trip failed.'
    $semanticErrors = @(
        Get-ParityObservationValidationErrors `
            $loaded $ClientRoot $beforeHashes[0] $beforeHashes[1] `
            $beforeHashes[2] '127.1.1.110:7000' `
            '2026-07-24T00:00:00Z' `
            ([DateTimeOffset]'2026-07-24T01:00:00Z')
    )
    Assert-True (
        $semanticErrors.Count -eq 0
    ) 'Valid observation rejected.'
    $invalidDirectMetadata = @(
        ($loaded | ConvertTo-Json -Depth 12) | ConvertFrom-Json
    )
    $invalidDirectMetadata[0].modules[0].baseAddress = $null
    $semanticErrors = @(
        Get-ParityObservationValidationErrors `
            $invalidDirectMetadata $ClientRoot `
            $beforeHashes[0] $beforeHashes[1] $beforeHashes[2] `
            '127.1.1.110:7000' '2026-07-24T00:00:00Z' `
            ([DateTimeOffset]'2026-07-24T01:00:00Z')
    )
    Assert-True (
        ($semanticErrors -join "`n") -match 'invalid Net.dll evidence'
    ) 'Incomplete Process.Modules metadata was accepted.'

    $restartManagerLoaded = @(
        ($loaded | ConvertTo-Json -Depth 12) | ConvertFrom-Json
    )
    $restartManagerLoaded[0].process.pathEvidenceSource =
        'QueryFullProcessImageName'
    $restartManagerLoaded[0].process.pathLocker = $null
    foreach ($moduleEvidence in $restartManagerLoaded[0].modules) {
        $locker = [pscustomobject]@{
            resourcePath = [string]$moduleEvidence.path
            processId = $restartManagerLoaded[0].process.id
            processStartFileTimeUtc =
                $restartManagerLoaded[0].process.startFileTimeUtc
            applicationName = 'Origin.exe'
        }
        $moduleEvidence.evidenceSource = 'RestartManagerFileUse'
        $moduleEvidence.baseAddress = $null
        $moduleEvidence.memorySize = $null
        $moduleEvidence.locker = $locker
    }
    $semanticErrors = @(
        Get-ParityObservationValidationErrors `
            $restartManagerLoaded $ClientRoot `
            $beforeHashes[0] $beforeHashes[1] $beforeHashes[2] `
            '127.1.1.110:7000' '2026-07-24T00:00:00Z' `
            ([DateTimeOffset]'2026-07-24T01:00:00Z')
    )
    Assert-True (
        $semanticErrors.Count -eq 0
    ) 'Valid RM file-use evidence was rejected.'
    $invalidRestartManagerMetadata = @(
        ($restartManagerLoaded | ConvertTo-Json -Depth 12) |
            ConvertFrom-Json
    )
    $invalidRestartManagerMetadata[0].modules[0].baseAddress =
        '0x10000000'
    $semanticErrors = @(
        Get-ParityObservationValidationErrors `
            $invalidRestartManagerMetadata $ClientRoot `
            $beforeHashes[0] $beforeHashes[1] $beforeHashes[2] `
            '127.1.1.110:7000' '2026-07-24T00:00:00Z' `
            ([DateTimeOffset]'2026-07-24T01:00:00Z')
    )
    Assert-True (
        ($semanticErrors -join "`n") -match 'invalid Net.dll evidence'
    ) 'Restart Manager evidence claimed direct-module metadata.'
    $restartManagerLoaded[0].modules[0].evidenceSource = 'ProcessModules'
    $restartManagerLoaded[0].modules[0].baseAddress = '0x10000000'
    $restartManagerLoaded[0].modules[0].memorySize = 4096
    $restartManagerLoaded[0].modules[0].locker = $null
    $semanticErrors = @(
        Get-ParityObservationValidationErrors `
            $restartManagerLoaded $ClientRoot `
            $beforeHashes[0] $beforeHashes[1] $beforeHashes[2] `
            '127.1.1.110:7000' '2026-07-24T00:00:00Z' `
            ([DateTimeOffset]'2026-07-24T01:00:00Z')
    )
    Assert-True (
        ($semanticErrors -join "`n") -match 'mixes module evidence sources'
    ) 'Mixed module sources were accepted.'
    $restartManagerLoaded[0].modules[0].evidenceSource =
        'RestartManagerFileUse'
    $restartManagerLoaded[0].modules[0].baseAddress = $null
    $restartManagerLoaded[0].modules[0].memorySize = $null
    $restartManagerLoaded[0].modules[0].locker = [pscustomobject]@{
        resourcePath = [string]$restartManagerLoaded[0].modules[0].path
        processId = $restartManagerLoaded[0].process.id
        processStartFileTimeUtc =
            $restartManagerLoaded[0].process.startFileTimeUtc
        applicationName = 'Origin.exe'
    }
    $restartManagerLoaded[0].modules[0].locker.processId = 999
    $semanticErrors = @(
        Get-ParityObservationValidationErrors `
            $restartManagerLoaded $ClientRoot `
            $beforeHashes[0] $beforeHashes[1] $beforeHashes[2] `
            '127.1.1.110:7000' '2026-07-24T00:00:00Z' `
            ([DateTimeOffset]'2026-07-24T01:00:00Z')
    )
    Assert-True (
        ($semanticErrors -join "`n") -match 'invalid Net.dll evidence'
    ) 'Wrong RM process was accepted.'

    $loaded[0].connections = @()
    Assert-True (
        @(
            Get-ParityObservationValidationErrors `
                $loaded $ClientRoot $beforeHashes[0] $beforeHashes[1] `
                $beforeHashes[2] '127.1.1.110:7000' `
                '2026-07-24T00:00:00Z' `
                ([DateTimeOffset]'2026-07-24T01:00:00Z')
        ).Count -gt 0
    ) 'Missing game connection was accepted.'
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

    & (Join-Path `
        $PSScriptRoot 'TestClientNetworkShimParityValidation.ps1')

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
