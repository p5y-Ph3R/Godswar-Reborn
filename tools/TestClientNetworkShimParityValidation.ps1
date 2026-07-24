[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$validationModule = Join-Path `
    $PSScriptRoot 'ClientNetworkShimParityValidation.psm1'
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

$failedMarkdown = New-ParityAcceptanceMarkdown ([pscustomobject]@{
    result = 'Fail'
    completedUtc = $sequenceBase.ToString('O')
    manualAttestation = [pscustomobject]@{
        operator = 'test'
        completedCycles = 0
        soakMinutes = 0
        logsReviewed = $false
        avatarPreviewLoadingGatePassed = $false
        noUnintendedBehaviorDifference = $false
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
    $failedMarkdown -notmatch 'passed / passed' -and
    $failedMarkdown -match 'Avatar preview loading gate \| False' -and
    $failedMarkdown -match 'No unintended behavior difference \| False'
) 'Failure Markdown falsely reported rollback/reapply success.'

Write-Host 'Client network shim parity validation unit tests passed.'
