[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$ApplyBackupPath =
        'C:\Reborn\backups\client-network-shim-v1-Apply-20260724-213354864',

    [string]$CandidateShimPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $PSScriptRoot 'InvokeClientNetworkShimParity.ps1'
$expectedToolVersion = '1.5.0'
$expectedShimHash =
    'EF531F8CB20A4FCA8D1DBA979FD131ECA002383AE862890435426DF948817597'
if ([string]::IsNullOrWhiteSpace($CandidateShimPath)) {
    $CandidateShimPath = Join-Path `
        $repoRoot 'client\network-shim\bin\Release\Win32\Net.dll'
}
Import-Module (
    Join-Path $PSScriptRoot 'ClientNetworkShimParityEvidence.psm1'
) -Force

function New-TestApplyBackup {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ClientPath,
        [Parameter(Mandatory)][string]$CreatedUtc,
        [Parameter(Mandatory)][string]$OriginSha256,
        [Parameter(Mandatory)][string]$ShimSha256,
        [Parameter(Mandatory)][string]$LegacySha256,
        [Parameter(Mandatory)][string]$StockSource
    )

    New-Item -ItemType Directory -Path $Path | Out-Null
    Copy-Item -LiteralPath $StockSource `
        -Destination (Join-Path $Path 'Net.dll')
    Write-ParityJsonNew ([ordered]@{
        schemaVersion = 1
        installerVersion = 'test'
        mode = 'Apply'
        createdUtc = $CreatedUtc
        clientRoot = [IO.Path]::GetFullPath($ClientPath).TrimEnd('\')
        originSha256 = $OriginSha256
        before = [ordered]@{
            state = 'Stock'
            netSha256 = $LegacySha256
            netLegacySha256 = $null
        }
        after = [ordered]@{
            netSha256 = $ShimSha256
            netLegacySha256 = $LegacySha256
        }
    }) (Join-Path $Path 'manifest.json')
}

function Write-TestObservation {
    param(
        [Parameter(Mandatory)][string]$RunRoot,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$ClientPath,
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][int]$AccountId,
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$StartedUtc,
        [Parameter(Mandatory)][string]$ObservedUtc,
        [Parameter(Mandatory)][string]$OriginSha256,
        [Parameter(Mandatory)][string]$ShimSha256,
        [Parameter(Mandatory)][string]$LegacySha256
    )

    $isStock = $Stage -eq 'StockRollback'
    $startFileTime = (
        [DateTimeOffset]$StartedUtc
    ).UtcDateTime.ToFileTimeUtc()
    $netPath = Join-Path $ClientPath 'Net.dll'
    $modules = @(
        [ordered]@{
            name = 'Net.dll'
            path = $netPath
            baseAddress = $null
            memorySize = $null
            diskSha256 = if ($isStock) {
                $LegacySha256
            } else {
                $ShimSha256
            }
            evidenceSource = 'RestartManagerFileUse'
            locker = [ordered]@{
                resourcePath = $netPath
                processId = $ProcessId
                processStartFileTimeUtc = $startFileTime
                applicationName = 'Origin.exe'
                applicationType = 1
                terminalSessionId = 1
                restartable = $false
            }
        }
    )
    if (-not $isStock) {
        $legacyPath = Join-Path $ClientPath 'NetLegacy.dll'
        $modules += [ordered]@{
            name = 'NetLegacy.dll'
            path = $legacyPath
            baseAddress = $null
            memorySize = $null
            diskSha256 = $LegacySha256
            evidenceSource = 'RestartManagerFileUse'
            locker = [ordered]@{
                resourcePath = $legacyPath
                processId = $ProcessId
                processStartFileTimeUtc = $startFileTime
                applicationName = 'Origin.exe'
                applicationType = 1
                terminalSessionId = 1
                restartable = $false
            }
        }
    }
    $path = Join-Path (
        Join-Path $RunRoot 'observations'
    ) ("synthetic-$ProcessId.json")
    Write-ParityJsonNew ([ordered]@{
        schemaVersion = 1
        runId = $RunId
        observedUtc = $ObservedUtc
        stage = $Stage
        accountId = $AccountId
        process = [ordered]@{
            id = $ProcessId
            startedUtc = $StartedUtc
            startFileTimeUtc = $startFileTime
            path = Join-Path $ClientPath 'Origin.exe'
            pathEvidenceSource = 'ProcessApi'
            pathLocker = $null
        }
        install = [ordered]@{
            state = if ($isStock) { 'Stock' } else { 'InstalledExact' }
            originSupported = $true
            originSha256 = $OriginSha256
        }
        modules = $modules
        connections = @(
            [ordered]@{
                remote = '127.1.1.110:7000'
                state = 'Established'
            }
        )
        passed = $true
        validationErrors = @()
    }) $path
    Write-ParityTextNew (
        (Get-ParitySha256 $path) + [Environment]::NewLine
    ) "$path.sha256"
}

if (@(Get-Process -Name Origin -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close Origin.exe before running the Complete integration test.'
}
$changes = @(& git -C $repoRoot status --porcelain=v1)
if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) {
    throw 'The Complete integration test requires a clean repository.'
}

$artifactParent = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'artifacts\network-shim-parity-tests')
).TrimEnd('\')
$testRoot = Join-Path $artifactParent (
    'complete-' + [guid]::NewGuid().ToString('N')
)
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

try {
    $client = Join-Path $testRoot 'client'
    New-Item -ItemType Directory -Path $client | Out-Null
    foreach ($name in @('Origin.exe', 'NetLegacy.dll')) {
        Copy-Item -LiteralPath (Join-Path $ClientRoot $name) `
            -Destination (Join-Path $client $name)
    }
    Copy-Item -LiteralPath $CandidateShimPath `
        -Destination (Join-Path $client 'Net.dll')
    $hashes = @(
        Get-ParitySha256 (Join-Path $client 'Origin.exe')
        Get-ParitySha256 (Join-Path $client 'Net.dll')
        Get-ParitySha256 (Join-Path $client 'NetLegacy.dll')
    )
    if ($hashes[1] -ne $expectedShimHash) {
        throw (
            "Candidate shim hash is $($hashes[1]); expected " +
            "$expectedShimHash."
        )
    }
    $stock = Join-Path $ApplyBackupPath 'Net.dll'
    $original = Join-Path $testRoot 'original-backup'
    New-TestApplyBackup `
        $original $client '2026-07-23T00:00:00Z' `
        $hashes[0] $hashes[1] $hashes[2] $stock
    $begin = & $tool `
        -Mode Begin `
        -ClientRoot $client `
        -OriginalApplyBackupPath $original `
        -EvidenceRoot (Join-Path $testRoot 'evidence') `
        -Operator 'automated-test'
    $manifest = Get-Content -LiteralPath (
        Join-Path $begin.EvidencePath 'manifest.json'
    ) -Raw | ConvertFrom-Json
    if ($manifest.toolVersion -ne $expectedToolVersion -or
        $manifest.client.netSha256 -ne $expectedShimHash -or
        $manifest.originalApplyBackup.afterNetSha256 -ne
            $expectedShimHash) {
        throw 'Begin did not pin the V2 candidate.'
    }
    $base = [DateTimeOffset]$manifest.startedUtc
    for ($index = 0; $index -lt 5; $index++) {
        Write-TestObservation `
            $begin.EvidencePath $manifest.runId $client 'ShimParity' `
            $(if ($index % 2 -eq 0) { 7 } else { 13 }) `
            (300 + $index) `
            $base.AddMilliseconds(($index * 2) + 1).ToString('O') `
            $base.AddMilliseconds(($index * 2) + 2).ToString('O') `
            $hashes[0] $hashes[1] $hashes[2]
    }
    Write-TestObservation `
        $begin.EvidencePath $manifest.runId $client 'StockRollback' 7 400 `
        $base.AddMilliseconds(11).ToString('O') `
        $base.AddMilliseconds(12).ToString('O') `
        $hashes[0] $hashes[1] $hashes[2]
    $final = Join-Path $testRoot 'final-backup'
    New-TestApplyBackup `
        $final $client `
        $base.AddMilliseconds(13).ToString('O') `
        $hashes[0] $hashes[1] $hashes[2] $stock
    Write-TestObservation `
        $begin.EvidencePath $manifest.runId $client 'FinalReapply' 7 401 `
        $base.AddMilliseconds(14).ToString('O') `
        $base.AddMilliseconds(15).ToString('O') `
        $hashes[0] $hashes[1] $hashes[2]
    Start-Sleep -Milliseconds 25
    $complete = & $tool `
        -Mode Complete `
        -EvidencePath $begin.EvidencePath `
        -FinalApplyBackupPath $final `
        -Operator 'automated-test' `
        -CompletedCycles 5 `
        -SoakMinutes 10 `
        -ChecklistPassed `
        -LogsReviewed `
        -AvatarPreviewLoadingGatePassed `
        -NoUnintendedBehaviorDifference
    if ($complete.Result -ne 'Pass') {
        throw 'A valid synthetic Complete integration was rejected.'
    }
    $status = & $tool -Mode Status -EvidencePath $begin.EvidencePath
    if ($status.State -ne 'Pass') {
        throw 'Status did not verify the checksummed completion.'
    }
    foreach ($name in @(
        'completion.json',
        'completion.sha256',
        'acceptance.md',
        'acceptance.sha256'
    )) {
        if (-not (Test-Path -LiteralPath (
                Join-Path $begin.EvidencePath $name
            ) -PathType Leaf)) {
            throw "Complete did not create $name."
        }
    }
    $writtenCompletion = Get-Content -LiteralPath (
        Join-Path $begin.EvidencePath 'completion.json'
    ) -Raw | ConvertFrom-Json
    $writtenAttestation = $writtenCompletion.manualAttestation
    if (-not $writtenAttestation.avatarPreviewLoadingGatePassed -or
        -not $writtenAttestation.noUnintendedBehaviorDifference) {
        throw 'Complete did not persist both avatar-preview attestations.'
    }
    $acceptanceMarkdown = Get-Content -LiteralPath (
        Join-Path $begin.EvidencePath 'acceptance.md'
    ) -Raw
    if ($acceptanceMarkdown -notmatch
            'Avatar preview loading gate \| True' -or
        $acceptanceMarkdown -notmatch
            'No unintended behavior difference \| True') {
        throw 'Acceptance Markdown omitted the avatar-preview attestations.'
    }
    Write-Host 'Clean-worktree Complete integration passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ($resolved.StartsWith(
            $artifactParent + '\',
            [StringComparison]::OrdinalIgnoreCase) -and
        $resolved -ne $artifactParent -and
        (Test-Path -LiteralPath $resolved -PathType Container)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
