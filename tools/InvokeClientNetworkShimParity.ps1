[CmdletBinding()]
param(
    [ValidateSet('Begin', 'Observe', 'Status', 'Complete')]
    [string]$Mode = 'Status',

    [string]$EvidencePath,

    [string]$EvidenceRoot,

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$OriginalApplyBackupPath =
        'C:\Reborn\backups\client-network-shim-v1-Apply-20260724-112517594',

    [string]$FinalApplyBackupPath,

    [ValidateSet('ShimParity', 'StockRollback', 'FinalReapply')]
    [string]$Stage = 'ShimParity',

    [int]$AccountId,

    [string]$Operator = $env:USERNAME,

    [int]$CompletedCycles,

    [int]$SoakMinutes,

    [switch]$ChecklistPassed,

    [switch]$LogsReviewed,

    [switch]$NoBehaviorDifference,

    [switch]$RecordFailure,

    [string]$Notes = '',

    [switch]$AllowDirtyRepository,

    [switch]$SkipServerChecks
)

$ErrorActionPreference = 'Stop'

$toolVersion = '1.1.0'
$originHash =
    '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
$shimHash =
    '528913E66888D5C070C39949D2FC1AE439B8414B15152312D4E093A29D17A6DD'
$legacyHash =
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
$serverEndpoints = @('127.1.1.110:5998', '127.1.1.110:7000')
$containerName = 'godswar-server'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Import-Module (
    Join-Path $PSScriptRoot 'ClientNetworkShimParityEvidence.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ClientNetworkShimParityValidation.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ClientNetworkShimWindowsEvidence.psm1'
) -Force

if (-not $EvidenceRoot) {
    $EvidenceRoot = Join-Path $repositoryRoot `
        'artifacts\network-shim\manual-parity'
}

function Assert-OriginClosed {
    $running = @(Get-Process -Name Origin -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        throw 'Origin.exe must be fully closed for this operation.'
    }
}

function Get-CurrentInventories {
    param([Parameter(Mandatory)][string]$Root)

    $dump = @(Get-ParityInventory (Join-Path $Root 'Dump'))
    $logs = @(Get-ParityInventory (Join-Path $Root 'Log'))
    return [ordered]@{
        dumps = $dump
        dumpSummary = Get-ParityInventorySummary $dump
        logs = $logs
        logSummary = Get-ParityInventorySummary $logs
    }
}

if ($Mode -eq 'Begin') {
    Assert-OriginClosed
    if ([string]::IsNullOrWhiteSpace($Operator) -or $Operator.Length -gt 64) {
        throw 'Operator must contain 1..64 characters.'
    }

    $client = Get-ParityClientSnapshot `
        $ClientRoot $originHash $shimHash $legacyHash
    if (-not $client.originSupported -or
        $client.state -ne 'InstalledExact') {
        throw "Begin requires InstalledExact; got $($client.state)."
    }
    $repository = Get-ParityRepositorySnapshot $repositoryRoot
    if (-not $repository.clean -and -not $AllowDirtyRepository) {
        throw 'Begin requires a clean repository worktree.'
    }
    $server = Get-ParityServerSnapshot `
        $containerName $serverEndpoints -SkipChecks:$SkipServerChecks
    if (-not $SkipServerChecks -and
        (-not $server.running -or -not $server.endpointsPresent)) {
        throw 'Begin requires the server and both legacy listeners.'
    }
    $backup = Get-ParityBackupSnapshot `
        $OriginalApplyBackupPath $originHash $shimHash $legacyHash
    $before = Get-CurrentInventories $ClientRoot

    $root = Resolve-ParityDirectory `
        $EvidenceRoot 'EvidenceRoot' -AllowMissing
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $runId = (
        [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' +
        [guid]::NewGuid().ToString('N').Substring(0, 8)
    )
    $runRoot = Join-Path $root $runId
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    New-Item -ItemType Directory -Path (
        Join-Path $runRoot 'observations'
    ) | Out-Null

    $manifest = [ordered]@{
        schemaVersion = 1
        toolVersion = $toolVersion
        runId = $runId
        state = 'Pending'
        startedUtc = [DateTime]::UtcNow.ToString('O')
        operator = $Operator
        clientRoot = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
        originalApplyBackup = $backup
        repository = $repository
        client = $client
        server = $server
        testOverrides = [ordered]@{
            dirtyRepositoryAllowed = [bool]$AllowDirtyRepository
            serverChecksSkipped = [bool]$SkipServerChecks
        }
        expected = [ordered]@{
            accounts = @(7, 13)
            minimumShimLaunches = 5
            stages = @('ShimParity', 'StockRollback', 'FinalReapply')
            checklist = @(
                'world entry',
                'movement',
                'map transition',
                'combat and skill',
                'chat',
                'inventory and equip/unequip',
                'forge and Gear Mentor',
                'Zodiac',
                'clean logout'
            )
        }
        before = $before
    }
    $manifestPath = Join-Path $runRoot 'manifest.json'
    Write-ParityJsonNew $manifest $manifestPath
    Write-ParityTextNew (
        (Get-ParitySha256 $manifestPath) + [Environment]::NewLine
    ) (Join-Path $runRoot 'manifest.sha256')

    [pscustomobject]@{
        State = 'Pending'
        EvidencePath = $runRoot
        RepositoryHead = $repository.head
        DumpBaseline = $before.dumpSummary.inventorySha256
        LogBaseline = $before.logSummary.inventorySha256
        Next = 'Launch Origin, then run Observe while the character is in-world.'
    }
    return
}

if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    throw "$Mode requires the exact -EvidencePath printed by Begin."
}
$evidence = Read-ParityManifest $EvidencePath
$runRoot = $evidence.Root
$manifest = $evidence.Manifest
if ([string]$manifest.toolVersion -ne $toolVersion) {
    throw (
        "Evidence tool version is $($manifest.toolVersion); " +
        "start a new run with $toolVersion."
    )
}
$actualClientRoot = [string]$manifest.clientRoot
$observations = @(
    Get-ParityObservations $runRoot ([string]$manifest.runId)
)

if ($Mode -eq 'Observe') {
    if ($AccountId -notin @(7, 13)) {
        throw 'Observe requires -AccountId 7 or 13.'
    }
    if (Read-ParityCompletion $runRoot ([string]$manifest.runId)) {
        throw 'This evidence run is already complete.'
    }
    if ($observations.Count -ge 64) {
        throw 'Observation count has reached the hard limit of 64.'
    }

    $processes = @(Get-Process -Name Origin -ErrorAction SilentlyContinue)
    if ($processes.Count -ne 1) {
        throw "Observe requires exactly one Origin.exe; found $($processes.Count)."
    }
    $process = $processes[0]
    $errors = @()
    $runtime = Get-ParityOriginRuntimeEvidence `
        -Process $process `
        -ClientRoot $actualClientRoot `
        -Stage $Stage
    $processPath = $runtime.processPath
    $errors += @($runtime.errors)

    $client = Get-ParityClientSnapshot `
        $actualClientRoot $originHash $shimHash $legacyHash
    $expectedState = if ($Stage -eq 'StockRollback') {
        'Stock'
    } else {
        'InstalledExact'
    }
    if ($client.state -ne $expectedState) {
        $errors += "Stage $Stage requires $expectedState, got $($client.state)."
    }
    if (-not $client.originSupported) {
        $errors += 'The running client is not the supported Origin.exe build.'
    }

    $modules = @($runtime.modules)
    $netModule = @($modules | Where-Object name -ieq 'Net.dll')
    $legacyModule = @($modules | Where-Object name -ieq 'NetLegacy.dll')
    if ($netModule.Count -ne 1) {
        $errors += 'Exactly one loaded Net.dll was not observed.'
    }
    if ($Stage -eq 'StockRollback') {
        if ($legacyModule.Count -ne 0) {
            $errors += 'Stock rollback unexpectedly loaded NetLegacy.dll.'
        }
        if ($netModule.Count -eq 1 -and
            $netModule[0].diskSha256 -ne $legacyHash) {
            $errors += 'Stock rollback loaded the wrong Net.dll hash.'
        }
    } else {
        if ($legacyModule.Count -ne 1) {
            $errors += 'Installed shim did not load exactly one NetLegacy.dll.'
        }
        if ($netModule.Count -eq 1 -and
            $netModule[0].diskSha256 -ne $shimHash) {
            $errors += 'Installed stage loaded the wrong Net.dll hash.'
        }
        if ($legacyModule.Count -eq 1 -and
            $legacyModule[0].diskSha256 -ne $legacyHash) {
            $errors += 'Installed stage loaded the wrong NetLegacy.dll hash.'
        }
    }
    $startedUtc = $process.StartTime.ToUniversalTime().ToString('O')
    foreach ($existing in $observations) {
        if ($existing.process.id -eq $process.Id -and
            $existing.process.startedUtc -eq $startedUtc) {
            throw 'This Origin.exe launch already has an observation.'
        }
    }
    $connections = @()
    if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
        try {
            $connections = @(
                Get-NetTCPConnection -OwningProcess $process.Id `
                    -ErrorAction Stop |
                    Select-Object -First 32 |
                    ForEach-Object {
                        [ordered]@{
                            local = "$($_.LocalAddress):$($_.LocalPort)"
                            remote = "$($_.RemoteAddress):$($_.RemotePort)"
                            state = [string]$_.State
                        }
                    }
            )
        }
        catch {
            $connections = @()
        }
    }
    if (@(
            $connections |
                Where-Object {
                    $_.remote -eq $serverEndpoints[1] -and
                    $_.state -eq 'Established'
                }
        ).Count -lt 1) {
        $errors += "No established game connection to $($serverEndpoints[1])."
    }
    try {
        $currentProcess = Get-Process -Id $process.Id -ErrorAction Stop
        $currentStartFileTime = (
            $currentProcess.StartTime.ToUniversalTime().ToFileTimeUtc()
        )
        if ($currentProcess.HasExited -or
            $currentStartFileTime -ne $runtime.processStartFileTimeUtc) {
            $errors += 'Origin.exe changed after connection evidence.'
        }
    }
    catch {
        $errors += "Final Origin identity check failed: $($_.Exception.Message)"
    }

    $observedUtc = [DateTime]::UtcNow.ToString('O')
    $observation = [ordered]@{
        schemaVersion = 1
        runId = [string]$manifest.runId
        observedUtc = $observedUtc
        stage = $Stage
        accountId = $AccountId
        process = [ordered]@{
            id = $process.Id
            startedUtc = $startedUtc
            startFileTimeUtc = $runtime.processStartFileTimeUtc
            path = $processPath
            pathEvidenceSource = $runtime.pathEvidenceSource
            pathLocker = $runtime.pathLocker
        }
        install = $client
        modules = $modules
        connections = $connections
        passed = $errors.Count -eq 0
        validationErrors = $errors
    }
    if ($errors.Count -eq 0) {
        $errors += @(
            Get-ParityObservationValidationErrors `
                @([pscustomobject]$observation) `
                $actualClientRoot $originHash $shimHash $legacyHash `
                $serverEndpoints[1] ([string]$manifest.startedUtc) `
                ([DateTimeOffset]::UtcNow)
        )
    }
    $observation['passed'] = $errors.Count -eq 0
    $observation['validationErrors'] = @($errors)
    $fileName = (
        [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' +
        [guid]::NewGuid().ToString('N').Substring(0, 8) + '.json'
    )
    $observationPath = Join-Path (
        Join-Path $runRoot 'observations'
    ) $fileName
    Write-ParityJsonNew $observation $observationPath
    Write-ParityTextNew (
        (Get-ParitySha256 $observationPath) + [Environment]::NewLine
    ) "$observationPath.sha256"
    [pscustomobject]@{
        Passed = $observation.passed
        Stage = $Stage
        AccountId = $AccountId
        ProcessId = $process.Id
        ObservationPath = $observationPath
        Errors = $errors
    }
    return
}

if ($Mode -eq 'Status') {
    $completionPath = Join-Path $runRoot 'completion.json'
    $completion = Read-ParityCompletion `
        $runRoot ([string]$manifest.runId)
    $client = Get-ParityClientSnapshot `
        $actualClientRoot $originHash $shimHash $legacyHash
    $observationErrors = @(
        Get-ParityObservationValidationErrors `
            $observations $actualClientRoot $originHash $shimHash $legacyHash `
            $serverEndpoints[1] ([string]$manifest.startedUtc) `
            ([DateTimeOffset]::UtcNow)
    )
    [pscustomobject]@{
        State = if ($observationErrors.Count -gt 0) {
            'InvalidEvidence'
        } elseif ($completion) {
            $completion.result
        } else {
            'Pending'
        }
        EvidencePath = $runRoot
        StartedUtc = $manifest.startedUtc
        InstallState = $client.state
        OriginRunning = @(
            Get-Process -Name Origin -ErrorAction SilentlyContinue
        ).Count -gt 0
        Observations = Get-ObservationSummary $observations
        ObservationValidationErrors = $observationErrors
        CompletionPath = if (Test-Path -LiteralPath $completionPath) {
            $completionPath
        } else {
            $null
        }
    }
    return
}

Assert-OriginClosed
if (Read-ParityCompletion $runRoot ([string]$manifest.runId)) {
    throw 'This evidence run already has an immutable completion.'
}
if ([string]::IsNullOrWhiteSpace($Operator) -or $Operator.Length -gt 64) {
    throw 'Operator must contain 1..64 characters.'
}
if ($Notes.Length -gt 2048) {
    throw 'Notes cannot exceed 2048 characters.'
}

$errors = @()
$repository = Get-ParityRepositorySnapshot $repositoryRoot
if (-not $repository.clean -or
    $repository.head -ne [string]$manifest.repository.head) {
    $errors += 'Repository HEAD/worktree changed during the test.'
}
$server = Get-ParityServerSnapshot `
    $containerName $serverEndpoints -SkipChecks:$SkipServerChecks
if ($manifest.testOverrides.dirtyRepositoryAllowed -or
    $manifest.testOverrides.serverChecksSkipped -or
    $SkipServerChecks) {
    $errors += 'Test-only Begin/Complete overrides prohibit acceptance.'
}
if (-not $SkipServerChecks) {
    if (-not $server.running -or -not $server.endpointsPresent) {
        $errors += 'Server or required listeners are unavailable.'
    }
    if ($server.imageId -ne [string]$manifest.server.imageId -or
        $server.id -ne [string]$manifest.server.id -or
        $server.startedUtc -ne [string]$manifest.server.startedUtc) {
        $errors += 'Server container instance or image changed during the test.'
    }
}
$client = Get-ParityClientSnapshot `
    $actualClientRoot $originHash $shimHash $legacyHash
if (-not $client.originSupported -or
    $client.state -ne 'InstalledExact') {
    $errors += "Final client state is $($client.state), not InstalledExact."
}

$originalBackup = $null
try {
    $originalBackup = Get-ParityBackupSnapshot `
        ([string]$manifest.originalApplyBackup.path) `
        $originHash $shimHash $legacyHash
    if (-not (Test-ParityBackupSnapshot `
            $originalBackup $manifest.originalApplyBackup)) {
        $errors += 'The original Apply backup changed during the test.'
    }
}
catch {
    $errors += "Original Apply backup: $($_.Exception.Message)"
}

$finalBackup = $null
if ([string]::IsNullOrWhiteSpace($FinalApplyBackupPath)) {
    $errors += 'Complete requires -FinalApplyBackupPath.'
} else {
    try {
        $finalBackup = Get-ParityBackupSnapshot `
            $FinalApplyBackupPath $originHash $shimHash $legacyHash
    }
    catch {
        $errors += $_.Exception.Message
    }
}
if ($finalBackup) {
    $errors += @(
        Get-ParityFinalBackupErrors `
            $finalBackup $manifest.originalApplyBackup `
            $actualClientRoot $legacyHash ([string]$manifest.startedUtc) `
            ([DateTimeOffset]::UtcNow)
    )
}

$after = Get-CurrentInventories $actualClientRoot
$dumpDifference = Compare-ParityInventory `
    @($manifest.before.dumps) @($after.dumps)
$logDifference = Compare-ParityInventory `
    @($manifest.before.logs) @($after.logs)
if ($dumpDifference.added.Count -gt 0 -or
    $dumpDifference.changed.Count -gt 0 -or
    $dumpDifference.removed.Count -gt 0) {
    $errors += 'Dump directory or Dump\Error.log changed during the test.'
}

$errors += @(
    Get-ParityObservationValidationErrors `
        $observations $actualClientRoot $originHash $shimHash $legacyHash `
        $serverEndpoints[1] ([string]$manifest.startedUtc) `
        ([DateTimeOffset]::UtcNow)
)
$errors += @(
    Get-ParitySequenceValidationErrors $observations $finalBackup
)
if ($CompletedCycles -lt 5) {
    $errors += 'Manual completed-cycle count is below five.'
}
if ($SoakMinutes -lt 1) {
    $errors += 'Soak duration must be at least one minute.'
}
if (-not $ChecklistPassed) {
    $errors += 'The full gameplay checklist was not attested.'
}
if (-not $LogsReviewed) {
    $errors += 'Changed client logs were not reviewed.'
}
if (-not $NoBehaviorDifference) {
    $errors += 'No-behavior-difference was not attested.'
}

if ($errors.Count -gt 0 -and -not $RecordFailure) {
    throw (
        "Acceptance is incomplete/failed:`n - " +
        ($errors -join "`n - ")
    )
}
$result = if ($errors.Count -eq 0) { 'Pass' } else { 'Fail' }
$completion = [ordered]@{
    schemaVersion = 1
    toolVersion = $toolVersion
    runId = [string]$manifest.runId
    result = $result
    completedUtc = [DateTime]::UtcNow.ToString('O')
    manualAttestation = [ordered]@{
        operator = $Operator
        completedCycles = $CompletedCycles
        soakMinutes = $SoakMinutes
        checklistPassed = [bool]$ChecklistPassed
        logsReviewed = [bool]$LogsReviewed
        noBehaviorDifference = [bool]$NoBehaviorDifference
        notes = $Notes
    }
    repository = $repository
    server = $server
    finalInstall = $client
    finalApplyBackup = $finalBackup
    after = $after
    differences = [ordered]@{
        dumps = $dumpDifference
        logs = $logDifference
    }
    observationSummary = Get-ObservationSummary $observations
    observations = $observations
    validationErrors = $errors
}
$completionPath = Join-Path $runRoot 'completion.json'
Write-ParityJsonNew $completion $completionPath
Write-ParityTextNew (
    (Get-ParitySha256 $completionPath) + [Environment]::NewLine
) (Join-Path $runRoot 'completion.sha256')
$markdownPath = Join-Path $runRoot 'acceptance.md'
Write-ParityTextNew (New-ParityAcceptanceMarkdown $completion) $markdownPath
Write-ParityTextNew (
    (Get-ParitySha256 $markdownPath) + [Environment]::NewLine
) (Join-Path $runRoot 'acceptance.sha256')

[pscustomobject]@{
    Result = $result
    EvidencePath = $runRoot
    CompletionPath = $completionPath
    AcceptanceMarkdownPath = $markdownPath
    Errors = $errors
}
