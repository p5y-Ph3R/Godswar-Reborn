[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9][a-z0-9._:-]{0,127}$')]
    [string]$Reason,

    [string]$EvidenceRoot,

    [switch]$AllowMutation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-B20Condition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

. (Join-Path $PSScriptRoot 'B20RetirementEvidence.StrictJson.ps1')

if (-not $AllowMutation) {
    throw (
        'Invalidation retires the active observation pointer. Pass ' +
        '-AllowMutation after confirming this exact run must not continue.')
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repositoryRoot 'artifacts/b20h-observation'
}
$resolvedRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$pathRoot = [IO.Path]::GetPathRoot($resolvedRoot)
if ($resolvedRoot.Length -gt $pathRoot.Length) {
    $resolvedRoot = $resolvedRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}
$activePath = Join-Path $resolvedRoot 'active-observation.json'
Assert-B20Condition (
    Test-Path -LiteralPath $activePath -PathType Leaf) (
    'No active B20H observation exists to invalidate.')
Assert-B20NoReparsePoints $resolvedRoot $activePath 'Active observation record'

$campaignLock = [IO.File]::Open(
    (Join-Path $resolvedRoot '.campaign.lock'),
    [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::ReadWrite,
    [IO.FileShare]::None)
try {
    $item = Get-Item -LiteralPath $activePath
    Assert-B20Condition ($item.Length -le 64KB) (
        'The active observation record exceeds its bounded size.')
    $raw = Get-Content -LiteralPath $activePath -Raw
    Assert-B20NoDuplicateJsonProperties $raw
    $active = $raw | ConvertFrom-Json
    Assert-B20Condition (
        [string]$active.schemaVersion -in @(
            'reborn.b20h.active-observation.v1',
            'reborn.b20h.active-observation.v2')) (
        'The active observation schema is invalid.')
    Assert-B20Condition (
        [string]$active.runId -cmatch
            '^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{12}$') (
        'The active observation run ID is invalid.')
    $evidenceDirectory = [IO.Path]::GetFullPath((Join-Path `
        $resolvedRoot ([string]$active.evidenceDirectory)))
    $comparison = if ($env:OS -eq 'Windows_NT') {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    $prefix = $resolvedRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    Assert-B20Condition (
        $evidenceDirectory.StartsWith($prefix, $comparison) -and
        (Test-Path -LiteralPath $evidenceDirectory -PathType Container)) (
        'The active observation directory escapes its evidence root.')
    Assert-B20NoReparsePoints `
        $resolvedRoot $evidenceDirectory 'Observation directory'
    $startPath = Join-Path $evidenceDirectory 'observation-start.json'
    Assert-B20Condition (
        (Test-Path -LiteralPath $startPath -PathType Leaf) -and
        (Get-Item -LiteralPath $startPath).Length -le 256KB) (
        'The observation start record is missing or oversized.')
    Assert-B20NoReparsePoints `
        $resolvedRoot $startPath 'Observation start record'
    $startRaw = Get-Content -LiteralPath $startPath -Raw
    Assert-B20NoDuplicateJsonProperties $startRaw
    $startRecord = $startRaw | ConvertFrom-Json
    Assert-B20Condition (
        [string]$startRecord.schemaVersion -in @(
            'reborn.b20h.docker-observation.v1',
            'reborn.b20h.docker-observation.v2')) (
        'The observation start record schema is invalid.')
    $prometheusDirectory = Join-Path $evidenceDirectory 'prometheus'
    Assert-B20Condition (
        Test-Path -LiteralPath $prometheusDirectory -PathType Container) (
        'The preserved Prometheus TSDB directory is missing.')
    Assert-B20NoReparsePoints `
        $resolvedRoot $prometheusDirectory 'Prometheus TSDB directory'

    $invalidatedAt = [DateTimeOffset]::UtcNow
    $timestamp = $invalidatedAt.UtcDateTime.ToString('yyyyMMddTHHmmssfffZ')
    $receiptPath = Join-Path $evidenceDirectory (
        "observation-invalidated-$timestamp-" +
        "$([Guid]::NewGuid().ToString('N')).json")
    $retiredPointerPath = Join-Path $resolvedRoot (
        "retired-active-$timestamp-" +
        "$([Guid]::NewGuid().ToString('N')).json")
    $receipt = [ordered]@{
        schemaVersion = 'reborn.b20h.docker-observation-invalidation.v1'
        status = 'invalidated'
        eligibleForRetirementAuthorization = $false
        invalidatedAtUtc = $invalidatedAt.UtcDateTime.ToString('O')
        reason = $Reason
        runId = [string]$active.runId
        priorSchemaVersion = [string]$active.schemaVersion
        activeRecordSha256 = (Get-FileHash `
            -LiteralPath $activePath -Algorithm SHA256).Hash
        evidencePreserved = $true
        prometheusTsdbPreserved = $true
    }
    $json = $receipt | ConvertTo-Json -Depth 6
    $encoding = [Text.UTF8Encoding]::new($false)
    $stream = [IO.File]::Open(
        $receiptPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new($stream, $encoding)
        try {
            $writer.Write($json + "`n")
            $writer.Flush()
        } finally {
            $writer.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
    [IO.File]::Move($activePath, $retiredPointerPath)

    [pscustomobject]@{
        Status = 'invalidated'
        RunId = [string]$active.runId
        Reason = $Reason
        InvalidationReceipt = $receiptPath
        RetiredActivePointer = $retiredPointerPath
        EvidenceDirectory = $evidenceDirectory
        PrometheusTsdbPreserved = $true
        DockerStateChanged = $false
    } | ConvertTo-Json
} finally {
    $campaignLock.Dispose()
}
