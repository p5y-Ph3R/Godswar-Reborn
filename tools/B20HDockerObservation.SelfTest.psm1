Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-B20SelfTestCondition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-B20InvalidationSelfTest {
    param(
        [Parameter(Mandatory)][string]$TemporaryRoot,
        [Parameter(Mandatory)][string]$ToolsRoot
    )

    $root = Join-Path $TemporaryRoot 'invalidation'
    $runId = '20260801T000000Z-0123456789ab'
    $run = Join-Path $root $runId
    $tsdb = Join-Path $run 'prometheus'
    $null = [IO.Directory]::CreateDirectory($tsdb)
    $active = Join-Path $root 'active-observation.json'
    $campaign = Join-Path $root '.campaign.lock'
    $tsdbMark = Join-Path $tsdb 'sentinel'
    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($campaign, 'campaign', $encoding)
    [IO.File]::WriteAllText($tsdbMark, 'tsdb', $encoding)
    [IO.File]::WriteAllText($active, (@{
        schemaVersion = 'reborn.b20h.active-observation.v2'
        runId = $runId
        evidenceDirectory = $runId
    } | ConvertTo-Json), $encoding)
    [IO.File]::WriteAllText((Join-Path $run 'observation-start.json'), (@{
        schemaVersion = 'reborn.b20h.docker-observation.v2'
    } | ConvertTo-Json), $encoding)
    $script = Join-Path $ToolsRoot 'InvalidateB20HDockerObservation.ps1'
    $rejected = $false
    try {
        & $script -Reason topology-correction-local-to-redis `
            -EvidenceRoot $root | Out-Null
    } catch {
        $rejected = $true
    }
    Assert-B20SelfTestCondition ($rejected -and
        (Test-Path -LiteralPath $active) -and
        [IO.File]::ReadAllText($campaign) -ceq 'campaign' -and
        [IO.File]::ReadAllText($tsdbMark) -ceq 'tsdb') (
        'Invalidation without approval must preserve all evidence.')
    $result = & $script -Reason topology-correction-local-to-redis `
        -EvidenceRoot $root -AllowMutation | ConvertFrom-Json
    $retired = @(Get-ChildItem -LiteralPath $root `
        -Filter 'retired-active-*.json')
    $receipts = @(Get-ChildItem -LiteralPath $run `
        -Filter 'observation-invalidated-*.json')
    Assert-B20SelfTestCondition ($receipts.Count -eq 1) (
        'Approved invalidation did not create one immutable receipt.')
    $receipt = Get-Content -LiteralPath $receipts[0].FullName -Raw |
        ConvertFrom-Json
    Assert-B20SelfTestCondition (-not (Test-Path -LiteralPath $active) -and
        $retired.Count -eq 1 -and $result.DockerStateChanged -eq $false -and
        $receipt.evidencePreserved -eq $true -and
        $receipt.prometheusTsdbPreserved -eq $true -and
        [IO.File]::ReadAllText($campaign) -ceq 'campaign' -and
        [IO.File]::ReadAllText($tsdbMark) -ceq 'tsdb') (
        'Approved invalidation changed more than the active pointer.')
}

Export-ModuleMember -Function 'Invoke-B20InvalidationSelfTest'
