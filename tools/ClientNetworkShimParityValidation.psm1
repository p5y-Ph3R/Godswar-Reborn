$ErrorActionPreference = 'Stop'

function Get-ObservationSummary {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Observations
    )

    $passing = @($Observations | Where-Object passed)
    $stageCounts = [ordered]@{}
    foreach ($name in @('ShimParity', 'StockRollback', 'FinalReapply')) {
        $stageCounts[$name] = @(
            $passing | Where-Object stage -eq $name
        ).Count
    }
    $launches = @(
        $passing |
            Group-Object { "$($_.process.id)|$($_.process.startedUtc)" }
    )

    return [ordered]@{
        total = $Observations.Count
        passing = $passing.Count
        failed = $Observations.Count - $passing.Count
        distinctPassingLaunches = $launches.Count
        passingByStage = $stageCounts
    }
}

function Get-ParityObservationValidationErrors {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Observations,
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$OriginHash,
        [Parameter(Mandatory)][string]$ShimHash,
        [Parameter(Mandatory)][string]$LegacyHash,
        [Parameter(Mandatory)][string]$GameEndpoint,
        [Parameter(Mandatory)][string]$RunStartedUtc,
        [DateTimeOffset]$NowUtc = [DateTimeOffset]::UtcNow
    )

    $errors = @()
    $root = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
    $runStarted = [DateTimeOffset]$RunStartedUtc
    $latest = $NowUtc.AddSeconds(1)

    function Test-RuntimeEvidenceSource {
        param(
            [string]$Source,
            [object]$Locker,
            [string]$DirectSource,
            [object]$ProcessEvidence,
            [string]$ExpectedResourcePath,
            [bool]$AllowFileUse
        )

        if ($Source -eq $DirectSource -or
            ($DirectSource -eq 'ProcessApi' -and
                $Source -eq 'QueryFullProcessImageName')) {
            return $null -eq $Locker
        }
        if (-not $AllowFileUse -or
            $Source -ne 'RestartManagerFileUse' -or
            -not $Locker) {
            return $false
        }
        return (
            [int]$Locker.processId -eq [int]$ProcessEvidence.id -and
            [long]$Locker.processStartFileTimeUtc -eq
                [long]$ProcessEvidence.startFileTimeUtc -and
            $Locker.applicationName -ieq 'Origin.exe' -and
            ([string]$Locker.resourcePath).Equals(
                $ExpectedResourcePath,
                [StringComparison]::OrdinalIgnoreCase)
        )
    }

    function Test-RuntimeModuleMetadata {
        param([object]$Module)

        if ($Module.evidenceSource -eq 'ProcessModules') {
            return (
                $null -eq $Module.locker -and
                [string]$Module.baseAddress -match
                    '^0x(?=.*[1-9A-Fa-f])[0-9A-Fa-f]{8,16}$' -and
                [long]$Module.memorySize -gt 0
            )
        }
        if ($Module.evidenceSource -eq 'RestartManagerFileUse') {
            return (
                $null -eq $Module.baseAddress -and
                $null -eq $Module.memorySize
            )
        }
        return $false
    }

    foreach ($observation in $Observations) {
        $identity = (
            "$($observation.stage)/$($observation.accountId)/" +
            "$($observation.process.id)"
        )
        if (-not $observation.passed -or
            @($observation.validationErrors).Count -gt 0) {
            $errors += "Observation failed validation: $identity"
            continue
        }

        $expectedState = if ($observation.stage -eq 'StockRollback') {
            'Stock'
        } else {
            'InstalledExact'
        }
        if (-not $observation.install.originSupported -or
            $observation.install.originSha256 -ne $OriginHash -or
            $observation.install.state -ne $expectedState) {
            $errors += "Observation has an invalid install state: $identity"
        }
        if ([int]$observation.process.id -le 0 -or
            -not ([string]$observation.process.path).Equals(
                (Join-Path $root 'Origin.exe'),
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-RuntimeEvidenceSource `
                ([string]$observation.process.pathEvidenceSource) `
                $observation.process.pathLocker `
                'ProcessApi' $observation.process `
                (Join-Path $root 'Origin.exe') $false)) {
            $errors += "Observation has an invalid process: $identity"
        }
        try {
            $started = [DateTimeOffset]$observation.process.startedUtc
            $observed = [DateTimeOffset]$observation.observedUtc
            if ($started -gt $observed -or
                $started -lt $runStarted -or
                $observed -lt $runStarted -or
                $started -gt $latest -or
                $observed -gt $latest -or
                [long]$observation.process.startFileTimeUtc -ne
                    $started.UtcDateTime.ToFileTimeUtc()) {
                throw 'Observation timestamp is outside the run.'
            }
        }
        catch {
            $errors += "Observation has invalid timestamps: $identity"
        }

        $modules = @($observation.modules)
        $net = @($modules | Where-Object name -ieq 'Net.dll')
        $legacy = @($modules | Where-Object name -ieq 'NetLegacy.dll')
        if (@(
                $modules.evidenceSource |
                    Select-Object -Unique
            ).Count -ne 1) {
            $errors += "Observation mixes module evidence sources: $identity"
        }
        $expectedNetHash = if ($observation.stage -eq 'StockRollback') {
            $LegacyHash
        } else {
            $ShimHash
        }
        if ($net.Count -ne 1 -or
            $net[0].diskSha256 -ne $expectedNetHash -or
            -not (Test-RuntimeModuleMetadata $net[0]) -or
            -not ([string]$net[0].path).Equals(
                (Join-Path $root 'Net.dll'),
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-RuntimeEvidenceSource `
                ([string]$net[0].evidenceSource) $net[0].locker `
                'ProcessModules' $observation.process `
                (Join-Path $root 'Net.dll') $true)) {
            $errors += "Observation has invalid Net.dll evidence: $identity"
        }
        if ($observation.stage -eq 'StockRollback') {
            if ($legacy.Count -ne 0) {
                $errors += "Stock observation loaded NetLegacy.dll: $identity"
            }
        } elseif ($legacy.Count -ne 1 -or
            $legacy[0].diskSha256 -ne $LegacyHash -or
            -not (Test-RuntimeModuleMetadata $legacy[0]) -or
            -not ([string]$legacy[0].path).Equals(
                (Join-Path $root 'NetLegacy.dll'),
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-RuntimeEvidenceSource `
                ([string]$legacy[0].evidenceSource) $legacy[0].locker `
                'ProcessModules' $observation.process `
                (Join-Path $root 'NetLegacy.dll') $true)) {
            $errors += "Observation has invalid NetLegacy.dll evidence: $identity"
        }

        $gameConnection = @(
            $observation.connections |
                Where-Object {
                    $_.remote -eq $GameEndpoint -and
                    $_.state -eq 'Established'
                }
        )
        if ($gameConnection.Count -lt 1) {
            $errors += "Observation lacks an established game connection: $identity"
        }
    }
    return $errors
}

function Get-ParitySequenceValidationErrors {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Observations,
        [object]$FinalBackup
    )

    $errors = @()
    $passing = @($Observations | Where-Object passed)
    foreach ($observation in $passing) {
        try {
            [void][DateTimeOffset]$observation.observedUtc
            [void][DateTimeOffset]$observation.process.startedUtc
        }
        catch {
            return @('Observation timestamps cannot be sequenced.')
        }
    }
    $shim = @(
        $passing |
            Where-Object stage -eq 'ShimParity' |
            Sort-Object { [DateTimeOffset]$_.observedUtc }
    )
    $stock = @(
        $passing |
            Where-Object stage -eq 'StockRollback' |
            Sort-Object { [DateTimeOffset]$_.observedUtc }
    )
    $final = @(
        $passing |
            Where-Object stage -eq 'FinalReapply' |
            Sort-Object { [DateTimeOffset]$_.observedUtc }
    )
    $launches = @(
        $shim |
            Group-Object { "$($_.process.id)|$($_.process.startedUtc)" } |
            ForEach-Object { $_.Group[0] } |
            Sort-Object { [DateTimeOffset]$_.observedUtc }
    )

    if ($launches.Count -lt 5) {
        $errors += 'Fewer than five distinct passing shim launches were observed.'
    }
    if ($launches.Count -gt 0 -and $launches[0].accountId -ne 7) {
        $errors += 'Shim launch sequence must start with account 7.'
    }
    if (@($launches.accountId | Select-Object -Unique).Count -lt 2) {
        $errors += 'Both account 7 and account 13 were not observed.'
    }
    for ($index = 1; $index -lt $launches.Count; $index++) {
        if ($launches[$index].accountId -eq
            $launches[$index - 1].accountId) {
            $errors += 'Shim observations do not alternate accounts.'
            break
        }
    }
    if ($stock.Count -lt 1) {
        $errors += 'No passing stock-rollback launch was observed.'
    }
    if ($final.Count -lt 1) {
        $errors += 'No passing final-reapply launch was observed.'
    }
    if ($shim.Count -gt 0 -and $stock.Count -gt 0 -and
        [DateTimeOffset]$stock[0].observedUtc -le
        [DateTimeOffset]$shim[-1].observedUtc) {
        $errors += 'Stock rollback was not observed after shim parity.'
    }
    if ($stock.Count -gt 0 -and $final.Count -gt 0 -and
        [DateTimeOffset]$final[0].observedUtc -le
        [DateTimeOffset]$stock[-1].observedUtc) {
        $errors += 'Final reapply was not observed after stock rollback.'
    }
    if ($FinalBackup -and $stock.Count -gt 0 -and
        [DateTimeOffset]$FinalBackup.createdUtc -le
        [DateTimeOffset]$stock[-1].observedUtc) {
        $errors += 'Final Apply backup was not created after stock rollback.'
    }
    if ($FinalBackup -and $final.Count -gt 0 -and
        [DateTimeOffset]$FinalBackup.createdUtc -ge
        [DateTimeOffset]$final[0].process.startedUtc) {
        $errors += 'Final Apply backup was not created before final reapply.'
    }
    return $errors
}

function New-ParityAcceptanceMarkdown {
    param([Parameter(Mandatory)][object]$Completion)

    function Escape-MarkdownValue {
        param([AllowEmptyString()][string]$Value)

        if ($null -eq $Value) {
            return ''
        }
        return $Value.Replace('|', '\|').
            Replace("`r", ' ').
            Replace("`n", ' ')
    }

    $manual = $Completion.manualAttestation
    $summary = $Completion.observationSummary
    $stockResult = if ($summary.passingByStage.StockRollback -gt 0) {
        'observed'
    } else {
        'missing'
    }
    $reapplyResult = if ($summary.passingByStage.FinalReapply -gt 0) {
        'observed'
    } else {
        'missing'
    }
    $dumpText = "added=$($Completion.differences.dumps.added.Count), " +
        "changed=$($Completion.differences.dumps.changed.Count), " +
        "removed=$($Completion.differences.dumps.removed.Count)"
    $backup = [string]$Completion.finalApplyBackup.path

    return @"
# Phase 1 interactive parity evidence

| Field | Value |
| --- | --- |
| Result | $($Completion.result) |
| Date/operator | $($Completion.completedUtc) / $(Escape-MarkdownValue ([string]$manual.operator)) |
| Repository revision | $($Completion.repository.head) |
| Origin/shim hashes | $($Completion.finalInstall.originSha256) / $($Completion.finalInstall.netSha256) |
| Server endpoints/image | $($Completion.server.endpoints -join ', ') / $($Completion.server.imageId) |
| Accounts/cycles | 7 <-> 13 / $($manual.completedCycles) |
| Passing observed launches | $($summary.distinctPassingLaunches) |
| Soak duration | $($manual.soakMinutes) minutes |
| Dump changes | $dumpText |
| Logs reviewed | $($manual.logsReviewed) |
| Avatar preview loading gate | $($manual.avatarPreviewLoadingGatePassed) |
| No unintended behavior difference | $($manual.noUnintendedBehaviorDifference) |
| Stock rollback / final reapply | $stockResult / $reapplyResult |
| Final Apply backup | $(Escape-MarkdownValue $backup) |
| Notes | $(Escape-MarkdownValue ([string]$manual.notes)) |
"@
}

Export-ModuleMember -Function @(
    'Get-ObservationSummary',
    'Get-ParityObservationValidationErrors',
    'Get-ParitySequenceValidationErrors',
    'New-ParityAcceptanceMarkdown'
)
