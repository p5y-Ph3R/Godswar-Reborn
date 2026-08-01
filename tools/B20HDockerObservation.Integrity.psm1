Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ArtifactPaths = @(
    '.dockerignore',
    'Dockerfile',
    'docker-compose.yml',
    'tools/docker/b20h/prometheus.yml',
    'tools/docker/b20h/rules.yml',
    'tools/docker/b20h/rules.test.yml',
    'tools/StartB20HDockerObservation.ps1',
    'tools/GetB20HDockerObservation.ps1',
    'tools/ExportB20HDockerObservationTelemetry.ps1',
    'tools/TestB20HDockerObservation.ps1',
    'tools/B20HDockerObservation.Integrity.psm1'
)

function Invoke-B20Command {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedPreference
    }
    if ($exitCode -ne 0) {
        throw (
            "$FilePath failed with exit code ${exitCode}: " +
            ($output -join [Environment]::NewLine))
    }
    return $output -join [Environment]::NewLine
}

function Assert-B20IntegrityCondition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Get-B20IntegrityProperty {
    param([object]$Value, [string]$Name, [string]$Context)

    Assert-B20IntegrityCondition ($null -ne $Value) "$Context is missing."
    $property = $Value.PSObject.Properties[$Name]
    Assert-B20IntegrityCondition ($null -ne $property) (
        "$Context.$Name is missing.")
    return $property.Value
}

function ConvertFrom-B20IntegrityUtc {
    param([object]$Value, [string]$Context)

    Assert-B20IntegrityCondition (
        $Value -is [string] -and
        $Value -cmatch (
            '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}' +
            '(?:\.\d{1,7})?Z$')) (
        "$Context must be an RFC 3339 UTC timestamp.")
    $parsed = [DateTimeOffset]::MinValue
    $valid = [DateTimeOffset]::TryParseExact(
        $Value,
        [string[]]@(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"),
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal -bor
            [Globalization.DateTimeStyles]::AdjustToUniversal,
        [ref]$parsed)
    Assert-B20IntegrityCondition $valid "$Context is invalid."
    return $parsed
}

function ConvertTo-B20IntegrityInteger {
    param([object]$Value, [string]$Context)

    Assert-B20IntegrityCondition (
        $Value -is [int] -or $Value -is [long]) (
        "$Context must be a JSON integer.")
    return [long]$Value
}

function Convert-B20EpochToUtc {
    param([double]$Epoch)

    return [DateTimeOffset]::FromUnixTimeMilliseconds(
        [long][Math]::Round($Epoch * 1000d)
    ).UtcDateTime.ToString('O')
}

function Get-B20ChangeCount {
    param([double[]]$Values)

    $changes = 0
    for ($index = 1; $index -lt $Values.Count; $index++) {
        if ($Values[$index] -ne $Values[$index - 1]) {
            $changes++
        }
    }
    return $changes
}

function Get-B20DecreaseCount {
    param([double[]]$Values)

    $decreases = 0
    for ($index = 1; $index -lt $Values.Count; $index++) {
        if ($Values[$index] -lt $Values[$index - 1]) {
            $decreases++
        }
    }
    return $decreases
}

function Get-B20TimestampSet {
    param(
        [object[]]$Points,
        [long]$MinimumSampleMilliseconds,
        [long]$MaximumSampleMilliseconds
    )

    $set = [Collections.Generic.HashSet[long]]::new()
    foreach ($point in $Points) {
        $sample = [long][Math]::Round($point.Timestamp * 1000d)
        if ($sample -ge $MinimumSampleMilliseconds -and
            $sample -le $MaximumSampleMilliseconds) {
            $null = $set.Add($sample)
        }
    }
    return $set
}

function Get-B20MissingSampleCount {
    param(
        [Collections.Generic.HashSet[long]]$Expected,
        [Collections.Generic.HashSet[long]]$Actual
    )

    $missing = 0
    foreach ($sample in $Expected) {
        if (-not $Actual.Contains($sample)) {
            $missing++
        }
    }
    return $missing
}

function Get-B20ObservationArtifactPaths {
    return [string[]]$script:ArtifactPaths.Clone()
}

function Get-B20ObservationArtifactHashes {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $result = [ordered]@{}
    foreach ($relativePath in $script:ArtifactPaths) {
        $path = Join-Path $RepositoryRoot $relativePath
        Assert-B20IntegrityCondition (
            Test-Path -LiteralPath $path -PathType Leaf) (
            "Observation artifact is missing: $relativePath")
        $result[$relativePath] = (
            Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    return $result
}

function Test-B20ObservationArtifactHashes {
    param(
        [Parameter(Mandatory)][object]$ExpectedHashes,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )

    $properties = @($ExpectedHashes.PSObject.Properties)
    if ($properties.Count -ne $script:ArtifactPaths.Count) {
        return $false
    }
    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    foreach ($relativePath in $script:ArtifactPaths) {
        $property = $ExpectedHashes.PSObject.Properties[$relativePath]
        if ($null -eq $property -or
            [string]$property.Value -cnotmatch '^[0-9A-F]{64}$') {
            return $false
        }
        $path = [IO.Path]::GetFullPath((Join-Path $root $relativePath))
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne
                [string]$property.Value) {
            return $false
        }
        $cursor = $path
        while ($cursor -cne $root) {
            if (((Get-Item -LiteralPath $cursor).Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0) {
                return $false
            }
            $cursor = Split-Path -Parent $cursor
            if ([string]::IsNullOrWhiteSpace($cursor)) {
                return $false
            }
        }
    }
    return $true
}

function Assert-B20AlphaObservationRecord {
    param([Parameter(Mandatory)][object]$Record)

    Assert-B20IntegrityCondition (
        (Get-B20IntegrityProperty $Record schemaVersion record) -ceq
            'reborn.b20h.docker-observation.v1') 'Unexpected record schema.'
    Assert-B20IntegrityCondition (
        (Get-B20IntegrityProperty $Record status record) -ceq 'running') (
        'The alpha observation is not running.')
    $approval = Get-B20IntegrityProperty $Record approval record
    Assert-B20IntegrityCondition (
        (Get-B20IntegrityProperty $approval approved approval) -is [bool] -and
        (Get-B20IntegrityProperty $approval approved approval)) (
        'The alpha approval Boolean is invalid.')
    Assert-B20IntegrityCondition (
        (Get-B20IntegrityProperty $approval approvalKind approval) -ceq
            'local-alpha-rehearsal' -and
        (Get-B20IntegrityProperty `
            $approval eligibleForRetirementAuthorization approval) -is
            [bool] -and
        -not (Get-B20IntegrityProperty `
            $approval eligibleForRetirementAuthorization approval)) (
        'The record must remain explicitly non-authorizing alpha evidence.')
    Assert-B20IntegrityCondition (
        (Get-B20IntegrityProperty $approval sourceCommit approval) -cmatch
            '^[0-9a-f]{40}$') 'The source commit is invalid.'
    $approvedAt = ConvertFrom-B20IntegrityUtc (
        Get-B20IntegrityProperty $approval approvedAtUtc approval) (
        'approval.approvedAtUtc')
    $window = Get-B20IntegrityProperty $Record window record
    $startedAt = ConvertFrom-B20IntegrityUtc (
        Get-B20IntegrityProperty $window startedAtUtc window) (
        'window.startedAtUtc')
    $targetEnd = ConvertFrom-B20IntegrityUtc (
        Get-B20IntegrityProperty $window targetEndedAtUtc window) (
        'window.targetEndedAtUtc')
    Assert-B20IntegrityCondition (
        $approvedAt -le $startedAt -and
        ($targetEnd - $startedAt).TotalHours -ge 168 -and
        (ConvertTo-B20IntegrityInteger (
            Get-B20IntegrityProperty `
                $window approvedMinimumHours window) `
            'window.approvedMinimumHours') -ge 168) (
        'The approved alpha window is shorter than 168 hours.')
    Assert-B20IntegrityCondition (
        (ConvertTo-B20IntegrityInteger (
            Get-B20IntegrityProperty `
                $Record expectedReplicaCount record) `
            'record.expectedReplicaCount') -eq 1) (
        'The local alpha record must contain exactly one replica.')
    $replica = Get-B20IntegrityProperty $Record replica record
    Assert-B20IntegrityCondition (
        (Get-B20IntegrityProperty $replica name replica) -ceq
            'tempest-world-01') 'The alpha replica identity is invalid.'
    $monitoring = Get-B20IntegrityProperty $Record monitoring record
    Assert-B20IntegrityCondition (
        (ConvertTo-B20IntegrityInteger (
            Get-B20IntegrityProperty `
                $monitoring scrapeIntervalSeconds monitoring) `
            'monitoring.scrapeIntervalSeconds') -eq 30 -and
        (ConvertTo-B20IntegrityInteger (
            Get-B20IntegrityProperty `
                $monitoring maximumScrapeGapSeconds monitoring) `
            'monitoring.maximumScrapeGapSeconds') -eq 300 -and
        (ConvertTo-B20IntegrityInteger (
            Get-B20IntegrityProperty `
                $monitoring retentionDays monitoring) `
            'monitoring.retentionDays') -ge 8) (
        'The alpha monitoring contract is invalid.')
    $null = Get-B20IntegrityProperty `
        $monitoring artifactSha256 monitoring
    return [pscustomobject]@{
        ApprovedAt = $approvedAt
        StartedAt = $startedAt
        TargetEnd = $targetEnd
    }
}

Export-ModuleMember -Function @(
    'Invoke-B20Command',
    'Convert-B20EpochToUtc',
    'Get-B20ChangeCount',
    'Get-B20DecreaseCount',
    'Get-B20TimestampSet',
    'Get-B20MissingSampleCount',
    'Get-B20ObservationArtifactPaths',
    'Get-B20ObservationArtifactHashes',
    'Test-B20ObservationArtifactHashes',
    'Assert-B20AlphaObservationRecord'
)
