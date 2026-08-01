[CmdletBinding(DefaultParameterSetName = 'Evidence')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Evidence')]
    [string]$EvidencePath,

    [Parameter(Mandatory, ParameterSetName = 'SelfTest')]
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:SchemaVersion = 'reborn.b20h.retirement-evidence.v1'
$script:MinimumWindowHours = 168
$script:MaximumReplicaCount = 512
$script:MaximumScrapeGapSeconds = 300
$script:RequiredWorkloads = @(
    'authentication_and_character_load',
    'open_world_gameplay',
    'inventory_and_economy',
    'progression',
    'pets_and_mounts',
    'map_and_zone_transfer',
    'scheduled_world_events'
)
$script:RequiredGates = @(
    'b19_reconciliation',
    'backup_restore',
    'clean_install',
    'upgrade_install',
    'prior_binary_rollback',
    'archive_parity'
)

function Assert-B20Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-B20Property {
    param(
        [object]$InputObject,
        [string]$Name,
        [string]$Context
    )

    Assert-B20Condition ($null -ne $InputObject) "$Context is missing."
    $property = $InputObject.PSObject.Properties[$Name]
    Assert-B20Condition ($null -ne $property) (
        "$Context.$Name is missing.")
    return $property.Value
}

. (Join-Path $PSScriptRoot 'B20RetirementEvidence.StrictJson.ps1')

function Test-B20Sha256 {
    param([object]$Value)

    return $Value -is [string] -and $Value -cmatch '^[0-9A-F]{64}$'
}

function Assert-B20FiniteName {
    param(
        [object]$Value,
        [string]$Context
    )

    Assert-B20Condition (
        $Value -is [string] -and
        $Value -cmatch '^[a-z0-9][a-z0-9._:-]{0,127}$') (
        "$Context must be a finite lower-case identifier.")
}

function Get-B20RelativePathUnderRoot {
    param(
        [string]$Root,
        [string]$Candidate,
        [string]$Context
    )

    $separator = [IO.Path]::DirectorySeparatorChar
    $rootWithSeparator = $Root.TrimEnd(
        $separator,
        [IO.Path]::AltDirectorySeparatorChar) + $separator
    $comparison = if ($env:OS -eq 'Windows_NT') {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    Assert-B20Condition (
        $Candidate.StartsWith($rootWithSeparator, $comparison)) (
        "$Context escapes its allowed root.")
    return $Candidate.Substring($rootWithSeparator.Length)
}

function Test-B20RetirementRecord {
    param(
        [Parameter(Mandatory)]
        [object]$Record,
        [DateTimeOffset]$NowUtc = [DateTimeOffset]::UtcNow
    )

    $schemaVersion = Get-B20Property $Record 'schemaVersion' 'record'
    Assert-B20Condition ($schemaVersion -ceq $script:SchemaVersion) (
        "Unsupported schemaVersion '$schemaVersion'.")
    Assert-B20Condition (
        (Get-B20Property $Record 'status' 'record') -ceq 'approved') (
        'record.status must be approved.')

    $approval = Get-B20Property $Record 'approval' 'record'
    Assert-B20JsonTrue `
        (Get-B20Property $approval 'approved' 'approval') `
        'approval.approved'
    Assert-B20FiniteName `
        -Value (Get-B20Property $approval 'changeId' 'approval') `
        -Context 'approval.changeId'
    Assert-B20FiniteName `
        -Value (Get-B20Property $approval 'approvedByRole' 'approval') `
        -Context 'approval.approvedByRole'
    $approvedAt = ConvertFrom-B20UtcTimestamp `
        -Value (Get-B20Property $approval 'approvedAtUtc' 'approval') `
        -Context 'approval.approvedAtUtc'

    $window = Get-B20Property $Record 'window' 'record'
    $startedAt = ConvertFrom-B20UtcTimestamp `
        -Value (Get-B20Property $window 'startedAtUtc' 'window') `
        -Context 'window.startedAtUtc'
    $endedAt = ConvertFrom-B20UtcTimestamp `
        -Value (Get-B20Property $window 'endedAtUtc' 'window') `
        -Context 'window.endedAtUtc'
    $minimumHours = ConvertFrom-B20JsonInteger `
        (Get-B20Property $window 'approvedMinimumHours' 'window') `
        'window.approvedMinimumHours'

    Assert-B20Condition ($approvedAt -le $startedAt) (
        'Approval must predate the observation window.')
    Assert-B20Condition ($endedAt -gt $startedAt) (
        'The observation window end must follow its start.')
    Assert-B20Condition ($endedAt -le $NowUtc) (
        'The observation window cannot end in the future.')
    Assert-B20Condition (
        $minimumHours -ge $script:MinimumWindowHours) (
        "The approved observation window must be at least " +
        "$($script:MinimumWindowHours) hours.")
    Assert-B20Condition (
        ($endedAt - $startedAt).TotalHours -ge $minimumHours) (
        'The recorded observation is shorter than its approved window.')

    $expectedReplicaCount = ConvertFrom-B20JsonInteger `
        (Get-B20Property $Record 'expectedReplicaCount' 'record') `
        'record.expectedReplicaCount'
    Assert-B20Condition (
        $expectedReplicaCount -ge 1 -and
        $expectedReplicaCount -le $script:MaximumReplicaCount) (
        'expectedReplicaCount is outside the bounded range.')

    $replicas = @((Get-B20Property $Record 'replicas' 'record'))
    Assert-B20Condition ($replicas.Count -eq $expectedReplicaCount) (
        'replicas must contain every expected replica exactly once.')
    $replicaNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($replica in $replicas) {
        $name = Get-B20Property $replica 'name' 'replica'
        Assert-B20FiniteName $name 'replica.name'
        Assert-B20Condition ($replicaNames.Add($name)) (
            "Duplicate replica '$name'.")

        $coverageStart = ConvertFrom-B20UtcTimestamp `
            -Value (Get-B20Property $replica 'coverageStartedAtUtc' $name) `
            -Context "$name.coverageStartedAtUtc"
        $coverageEnd = ConvertFrom-B20UtcTimestamp `
            -Value (Get-B20Property $replica 'coverageEndedAtUtc' $name) `
            -Context "$name.coverageEndedAtUtc"
        Assert-B20Condition (
            $coverageStart -le $startedAt -and $coverageEnd -ge $endedAt) (
            "$name does not cover the complete observation window.")
        $observerReady = ConvertFrom-B20JsonInteger `
            (Get-B20Property $replica 'observerReadyMinimum' $name) `
            "$name.observerReadyMinimum"
        Assert-B20Condition ($observerReady -eq 1) (
            "$name did not continuously publish observer readiness.")
        $legacyDelta = ConvertFrom-B20JsonInteger `
            (Get-B20Property $replica 'legacyInvocationDelta' $name) `
            "$name.legacyInvocationDelta"
        Assert-B20Condition ($legacyDelta -eq 0) (
            "$name observed a legacy persistence invocation.")
        $resetCount = ConvertFrom-B20JsonInteger `
            (Get-B20Property $replica 'counterResetCount' $name) `
            "$name.counterResetCount"
        Assert-B20Condition ($resetCount -eq 0) (
            "$name has an unaccounted counter reset.")
        $maximumGap = ConvertFrom-B20JsonInteger `
            (Get-B20Property $replica 'maximumScrapeGapSeconds' $name) `
            "$name.maximumScrapeGapSeconds"
        Assert-B20Condition (
            $maximumGap -ge 0 -and
            $maximumGap -le $script:MaximumScrapeGapSeconds) (
            "$name exceeds the maximum allowed telemetry gap.")
    }

    $workloads = @((Get-B20Property $Record 'workloadCoverage' 'record'))
    $workloadNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($workload in $workloads) {
        $name = Get-B20Property $workload 'name' 'workloadCoverage'
        Assert-B20FiniteName $name 'workloadCoverage.name'
        Assert-B20Condition ($workloadNames.Add($name)) (
            "Duplicate workload '$name'.")
        Assert-B20Condition (
            (Get-B20Property $workload 'status' $name) -ceq 'passed') (
            "$name workload coverage did not pass.")
    }
    foreach ($required in $script:RequiredWorkloads) {
        Assert-B20Condition ($workloadNames.Contains($required)) (
            "Required workload '$required' is missing.")
    }

    $gates = @((Get-B20Property $Record 'gates' 'record'))
    Assert-B20Condition ($gates.Count -eq $script:RequiredGates.Count) (
        'gates must contain exactly the required retirement gates.')
    $gateNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($gate in $gates) {
        $name = Get-B20Property $gate 'name' 'gate'
        Assert-B20FiniteName $name 'gate.name'
        Assert-B20Condition ($gateNames.Add($name)) "Duplicate gate '$name'."
        Assert-B20Condition (
            (Get-B20Property $gate 'status' $name) -ceq 'passed') (
            "$name did not pass.")
        $reference = Get-B20Property $gate 'evidenceReference' $name
        Assert-B20Condition (
            $reference -is [string] -and
            $reference.Length -ge 1 -and $reference.Length -le 1024) (
            "$name has no bounded evidence reference.")
        Assert-B20Condition (
            Test-B20Sha256 (Get-B20Property $gate 'sha256' $name)) (
            "$name has an invalid SHA-256 receipt.")
    }
    foreach ($required in $script:RequiredGates) {
        Assert-B20Condition ($gateNames.Contains($required)) (
            "Required gate '$required' is missing.")
    }

    return [pscustomobject]@{
        SchemaVersion = $schemaVersion
        ObservationHours = [Math]::Round(
            ($endedAt - $startedAt).TotalHours,
            2)
        ReplicaCount = $replicas.Count
        WorkloadCount = $workloads.Count
        GateCount = $gates.Count
        RetirementAuthorized = $true
    }
}

function Test-B20EvidenceArtifacts {
    param(
        [Parameter(Mandatory)]
        [object]$Record,
        [Parameter(Mandatory)]
        [string]$EvidenceDirectory
    )

    $root = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $seen = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($gate in @((Get-B20Property $Record 'gates' 'record'))) {
        $name = Get-B20Property $gate 'name' 'gate'
        $reference = [string](
            Get-B20Property $gate 'evidenceReference' $name)
        Assert-B20Condition (-not [IO.Path]::IsPathRooted($reference)) (
            "$name evidenceReference must be relative to the evidence file.")
        $candidate = [IO.Path]::GetFullPath(
            (Join-Path $root $reference))
        $relative = Get-B20RelativePathUnderRoot `
            $root $candidate "$name evidenceReference"
        Assert-B20Condition ($seen.Add($candidate)) (
            "$name reuses another gate's evidence artifact.")
        Assert-B20Condition (
            Test-Path -LiteralPath $candidate -PathType Leaf) (
            "$name evidence artifact does not exist: $relative")
        Assert-B20NoReparsePoints $root $candidate (
            "$name evidenceReference")
        $artifact = Get-Item -LiteralPath $candidate
        Assert-B20Condition ($artifact.Length -le 128MB) (
            "$name evidence artifact exceeds the 128 MiB hash limit.")
        $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
        $expected = [string](Get-B20Property $gate 'sha256' $name)
        Assert-B20Condition ($actual -ceq $expected) (
            "$name evidence artifact does not match its SHA-256 receipt.")
    }
}

function New-B20SelfTestRecord {
    $start = [DateTimeOffset]::UtcNow.AddDays(-8)
    $end = $start.AddDays(7)
    $sha = 'A' * 64
    return [pscustomobject]@{
        schemaVersion = $script:SchemaVersion
        status = 'approved'
        approval = [pscustomobject]@{
            approved = $true
            changeId = 'change-b20h-test'
            approvedByRole = 'release-owner'
            approvedAtUtc = $start.AddHours(-1).UtcDateTime.ToString('O')
        }
        window = [pscustomobject]@{
            startedAtUtc = $start.UtcDateTime.ToString('O')
            endedAtUtc = $end.UtcDateTime.ToString('O')
            approvedMinimumHours = 168
        }
        expectedReplicaCount = 1
        replicas = @([pscustomobject]@{
            name = 'tempest-world-01'
            coverageStartedAtUtc =
                $start.AddMinutes(-5).UtcDateTime.ToString('O')
            coverageEndedAtUtc =
                $end.AddMinutes(5).UtcDateTime.ToString('O')
            observerReadyMinimum = 1
            legacyInvocationDelta = 0
            counterResetCount = 0
            maximumScrapeGapSeconds = 30
        })
        workloadCoverage = @(
            $script:RequiredWorkloads | ForEach-Object {
                [pscustomobject]@{ name = $_; status = 'passed' }
            })
        gates = @(
            $script:RequiredGates | ForEach-Object {
                [pscustomobject]@{
                    name = $_
                    status = 'passed'
                    evidenceReference = "artifacts/b20h/$_.json"
                    sha256 = $sha
                }
            })
    }
}

function Copy-B20Record {
    param([object]$Record)
    return $Record | ConvertTo-Json -Depth 20 | ConvertFrom-Json
}

function Invoke-B20SelfTest {
    $valid = New-B20SelfTestRecord
    $result = Test-B20RetirementRecord $valid
    Assert-B20Condition $result.RetirementAuthorized (
        'Valid evidence was rejected.')

    $cases = @(
        @{ Name = 'short window'; Mutate = {
            param($r) $r.window.approvedMinimumHours = 24
        } },
        @{ Name = 'missing replica'; Mutate = {
            param($r) $r.replicas = @()
        } },
        @{ Name = 'observer not ready'; Mutate = {
            param($r) $r.replicas[0].observerReadyMinimum = 0
        } },
        @{ Name = 'legacy invocation'; Mutate = {
            param($r) $r.replicas[0].legacyInvocationDelta = 1
        } },
        @{ Name = 'counter reset'; Mutate = {
            param($r) $r.replicas[0].counterResetCount = 1
        } },
        @{ Name = 'telemetry gap'; Mutate = {
            param($r) $r.replicas[0].maximumScrapeGapSeconds = 301
        } },
        @{ Name = 'missing workload'; Mutate = {
            param($r) $r.workloadCoverage = @($r.workloadCoverage)[1..6]
        } },
        @{ Name = 'failed gate'; Mutate = {
            param($r) $r.gates[0].status = 'failed'
        } },
        @{ Name = 'invalid checksum'; Mutate = {
            param($r) $r.gates[0].sha256 = 'not-a-checksum'
        } },
        @{ Name = 'draft status'; Mutate = {
            param($r) $r.status = 'draft'
        } },
        @{ Name = 'string approval'; Mutate = {
            param($r) $r.approval.approved = 'true'
        } },
        @{ Name = 'fractional replica count'; Mutate = {
            param($r) $r.expectedReplicaCount = 1.4
        } },
        @{ Name = 'fractional observer readiness'; Mutate = {
            param($r) $r.replicas[0].observerReadyMinimum = 0.6
        } },
        @{ Name = 'fractional legacy invocation'; Mutate = {
            param($r) $r.replicas[0].legacyInvocationDelta = 0.4
        } },
        @{ Name = 'fractional counter reset'; Mutate = {
            param($r) $r.replicas[0].counterResetCount = 0.4
        } },
        @{ Name = 'fractional telemetry gap'; Mutate = {
            param($r) $r.replicas[0].maximumScrapeGapSeconds = 300.4
        } },
        @{ Name = 'loose timestamp'; Mutate = {
            param($r) $r.window.startedAtUtc = 'August 1, 2026 Z'
        } },
        @{ Name = 'future observation'; Mutate = {
            param($r) $r.window.endedAtUtc =
                [DateTimeOffset]::UtcNow.AddMinutes(1).ToString('O')
        } }
    )

    foreach ($case in $cases) {
        $candidate = Copy-B20Record $valid
        & $case.Mutate $candidate
        $rejected = $false
        try {
            Test-B20RetirementRecord $candidate | Out-Null
        }
        catch {
            $rejected = $true
        }
        Assert-B20Condition $rejected (
            "Self-test '$($case.Name)' was incorrectly accepted.")
    }

    $duplicateRejected = $false
    try {
        Assert-B20NoDuplicateJsonProperties (
            '{"status":"draft","status":"approved"}')
    }
    catch {
        $duplicateRejected = $true
    }
    Assert-B20Condition $duplicateRejected (
        'Duplicate JSON properties were incorrectly accepted.')

    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $testRoot = [IO.Path]::GetFullPath((Join-Path $systemTemp (
        'reborn-b20h-evidence-' + [Guid]::NewGuid().ToString('N'))
    ))
    $testRelative = Get-B20RelativePathUnderRoot `
        $systemTemp $testRoot 'self-test directory'
    Assert-B20Condition (
        $testRelative -cmatch '^reborn-b20h-evidence-[0-9a-f]{32}$') (
        'The self-test directory escaped the system temporary directory.')
    try {
        New-Item -ItemType Directory -Path $testRoot | Out-Null
        $artifactRecord = Copy-B20Record $valid
        foreach ($gate in $artifactRecord.gates) {
            $gate.evidenceReference = "$($gate.name).json"
            $artifactPath = Join-Path $testRoot $gate.evidenceReference
            [IO.File]::WriteAllText(
                $artifactPath,
                "bounded $($gate.name) evidence")
            $gate.sha256 = (
                Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
        }
        Test-B20EvidenceArtifacts $artifactRecord $testRoot

        $artifactRecord.gates[0].sha256 = 'B' * 64
        $rejected = $false
        try {
            Test-B20EvidenceArtifacts $artifactRecord $testRoot
        }
        catch {
            $rejected = $true
        }
        Assert-B20Condition $rejected (
            'A tampered evidence artifact was incorrectly accepted.')

        $artifactRecord = Copy-B20Record $valid
        $artifactRecord.gates[0].evidenceReference = '../escape.json'
        $rejected = $false
        try {
            Test-B20EvidenceArtifacts $artifactRecord $testRoot
        }
        catch {
            $rejected = $true
        }
        Assert-B20Condition $rejected (
            'An escaping evidence reference was incorrectly accepted.')
    }
    finally {
        if (Test-Path -LiteralPath $testRoot -PathType Container) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }

    Write-Host 'B20H retirement-evidence self-tests passed.'
}

if ($SelfTest) {
    Invoke-B20SelfTest
    exit 0
}

$resolvedPath = [IO.Path]::GetFullPath($EvidencePath)
Assert-B20Condition (
    Test-Path -LiteralPath $resolvedPath -PathType Leaf) (
    "Evidence file does not exist: $resolvedPath")
$evidenceDirectory = Split-Path -Parent $resolvedPath
Assert-B20NoReparsePoints `
    $evidenceDirectory $resolvedPath 'Evidence file'
$fileInfo = Get-Item -LiteralPath $resolvedPath
Assert-B20Condition ($fileInfo.Length -le 1MB) (
    'Evidence file exceeds the 1 MiB parser limit.')
$json = Get-Content -LiteralPath $resolvedPath -Raw
Assert-B20NoDuplicateJsonProperties $json
$record = $json | ConvertFrom-Json
$validation = Test-B20RetirementRecord $record
Test-B20EvidenceArtifacts $record $evidenceDirectory
$validation | ConvertTo-Json -Depth 4
