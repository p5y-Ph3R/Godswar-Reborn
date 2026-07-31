[CmdletBinding()]
param(
    [string]$RedisImage = 'redis:7.4.10-alpine',
    [string]$ReportPath = 'artifacts/b17/redis-ci-result.json',
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$protocolChecksAssembly = Join-Path $repositoryRoot (
    'tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/' +
    'Godswar.Server.ProtocolChecks.dll')
$absoluteReportPath =
    if ([IO.Path]::IsPathRooted($ReportPath)) {
        [IO.Path]::GetFullPath($ReportPath)
    }
    else {
        [IO.Path]::GetFullPath(
            (Join-Path $repositoryRoot $ReportPath))
    }
$startedAt = [DateTimeOffset]::UtcNow
$containerName =
    'godswar-b17-ci-' +
    $PID +
    '-' +
    ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$containerStarted = $false
$containerPaused = $false
$credentialDirectory = $null
$aclPath = $null
$applicationUsername = 'godswar_b17'
$adminUsername = 'godswar_b17_ci_admin'
$applicationPassword = $null
$adminPassword = $null
$restartSeedKey = 'godswar:b17-ci:v1:restart-seed'
$failureCategory = $null
$failureMessage = $null
$checks = [System.Collections.Generic.List[object]]::new()
$scenarios = [System.Collections.Generic.List[object]]::new()
$cleanupErrors = [System.Collections.Generic.List[string]]::new()
$previousTestConnection =
    [Environment]::GetEnvironmentVariable(
        'GODSWAR_TEST_REDIS_CONNECTION_STRING')
$previousAdminConnection =
    [Environment]::GetEnvironmentVariable(
        'GODSWAR_TEST_REDIS_ADMIN_CONNECTION_STRING')

. (Join-Path $PSScriptRoot 'InvokeB17RedisCiGate.Docker.ps1')
. (Join-Path $PSScriptRoot 'InvokeB17RedisCiGate.Acl.ps1')

function Invoke-ProtocolChecks {
    param(
        [Parameter(Mandatory)]
        [string]$Scenario
    )

    $required = @(
        'Redis atomic secure game-ticket authority',
        'Redis fenced worker route and player lease authority',
        'B17 Redis semantic gateway cross-process authority'
    )
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(
            & dotnet $protocolChecksAssembly @required 2>&1)
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $exitCode = $LASTEXITCODE
    foreach ($name in $required) {
        $passed = $output -contains "PASS $name"
        $checks.Add([ordered]@{
            scenario = $Scenario
            name = $name
            status = if ($passed) { 'passed' } else { 'failed' }
        })
        if (-not $passed) {
            $failure = $output |
                Where-Object { "$_" -like 'FAIL *' } |
                Select-Object -First 1
            $aclLog = Invoke-RedisCli `
                -Username $adminUsername `
                -Password $adminPassword `
                -Command @('ACL', 'LOG', '3') `
                -Operation 'failed-suite ACL inspection' `
                -AllowFailure
            $aclEvidence = ($aclLog.Output -join ' ').Trim()
            if ($aclEvidence.Length -gt 512) {
                $aclEvidence = $aclEvidence.Substring(0, 512)
            }
            throw (
                "Required Redis protocol check failed in '$Scenario': " +
                ($(if ($failure) { "$failure" } else { 'no failure line' })) +
                ($(if ($aclEvidence) {
                    "; ACL evidence: $aclEvidence"
                } else {
                    '; ACL evidence: none'
                }))
            )
        }
    }
    if ($exitCode -ne 0) {
        throw "Redis protocol process failed in '$Scenario'."
    }
}

function Invoke-PolicyChecks {
    $required = @(
        'B17 Redis coordination architecture ratchet',
        'B17 fenced worker and player coordination runtime',
        'B17 Redis coordination configuration policy',
        'B13 aggregate server operational state',
        'Secure Phase 5A operational state metrics'
    )
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(
            & dotnet $protocolChecksAssembly @required 2>&1)
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $exitCode = $LASTEXITCODE
    foreach ($name in $required) {
        $passed = $output -contains "PASS $name"
        $checks.Add([ordered]@{
            scenario = 'policy'
            name = $name
            status = if ($passed) { 'passed' } else { 'failed' }
        })
        if (-not $passed) {
            throw "Required Redis policy check '$name' failed."
        }
    }
    if ($exitCode -ne 0) {
        throw 'Redis policy-check process failed.'
    }
}

function Assert-RedisUnavailableFailsClosed {
    $name = 'Redis fenced worker route and player lease authority'
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(
            & dotnet $protocolChecksAssembly $name 2>&1)
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0 -or
        -not ($output -match "^FAIL $([regex]::Escape($name)):")) {
        throw 'Redis outage did not fail the required coordination check.'
    }
    $checks.Add([ordered]@{
        scenario = 'paused-unavailable'
        name = 'Redis dependency fails closed'
        status = 'passed'
    })
}

function Assert-NoEviction {
    param(
        [Parameter(Mandatory)]
        [string]$Username,
        [Parameter(Mandatory)]
        [string]$Password
    )

    $result = Invoke-RedisCli `
        -Username $Username `
        -Password $Password `
        -Command @('CONFIG', 'GET', 'maxmemory-policy') `
        -Operation 'noeviction policy inspection'
    if (-not ($result.Output -contains 'noeviction')) {
        throw 'Disposable Redis is not using maxmemory-policy noeviction.'
    }
    $checks.Add([ordered]@{
        scenario = 'configuration'
        name = 'Redis maxmemory policy is noeviction'
        status = 'passed'
    })
}

function Assert-DatabaseEmpty {
    param(
        [Parameter(Mandatory)]
        [string]$Username,
        [Parameter(Mandatory)]
        [string]$Password
    )

    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $result = Invoke-RedisCli `
            -Username $Username `
            -Password $Password `
            -Command @('DBSIZE') `
            -Operation 'Redis cleanup verification'
        $size = 0
        if ([int]::TryParse(
                ($result.Output -join '').Trim(),
                [ref]$size) -and
            $size -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'Redis protocol keys exceeded their bounded cleanup interval.'
}

try {
    $null = Invoke-Docker `
        -Arguments @('version', '--format', '{{.Server.Version}}') `
        -Operation 'Docker availability'

    if (-not $SkipBuild) {
        & dotnet build `
            (Join-Path $repositoryRoot 'GodswarServer.sln') `
            --configuration Release `
            --nologo
        if ($LASTEXITCODE -ne 0) {
            throw 'Release build failed before the Redis gate.'
        }
    }

    $applicationPassword = New-RandomHexSecret
    $adminPassword = New-RandomHexSecret
    if ($applicationPassword -eq $adminPassword) {
        throw 'Disposable Redis credentials unexpectedly collided.'
    }
    $credentialDirectory = Join-Path (
        [IO.Path]::GetTempPath()) (
        'godswar-b17-ci-' +
        $PID +
        '-' +
        [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($credentialDirectory) | Out-Null
    $aclPath = Join-Path $credentialDirectory 'redis.acl'
    $applicationKeyPatterns = @(
        '~godswar:ticket_test_*:v1:*'
        '~godswar:worker_test_*:v1:*'
        '~godswar:sg-*:v1:*'
        '~godswar:b17-ci:v1:*'
    ) -join ' '
    $applicationCommands =
        '-@all +ping +echo +time +info +select ' +
        '+client|id +client|setinfo ' +
        '+client|setname +eval +evalsha +script|load +hget +hmget ' +
        '+hgetall +hset +hdel ' +
        '+hincrby +del +unlink +exists +pexpire +pttl +zadd +zcard +zrem ' +
        '+zrangebyscore +zremrangebyscore +zscore'
    $adminCommands =
        '-@all +ping +echo +info +select +client|id +client|setinfo ' +
        '+client|setname +config|get +dbsize +flushdb +client|pause ' +
        '+exists +acl|getuser +acl|dryrun +acl|log'
    $acl = @(
        'user default off'
        (
            "user $applicationUsername reset on >$applicationPassword " +
            "$applicationKeyPatterns $applicationCommands"
        )
        (
            "user $adminUsername reset on >$adminPassword ~* " +
            $adminCommands
        )
    ) -join "`n"
    [IO.File]::WriteAllText(
        $aclPath,
        $acl + "`n",
        [Text.UTF8Encoding]::new($false))
    $portableAclPath = $aclPath.Replace('\', '/')
    $null = Invoke-Docker `
        -Arguments @(
            'run',
            '--detach',
            '--rm',
            '--name',
            $containerName,
            '--label',
            'godswar.b17.disposable=true',
            '--publish',
            '127.0.0.1::6379',
            '--mount',
            (
                "type=bind,source=$portableAclPath," +
                'target=/run/secrets/redis_acl,readonly'
            ),
            $RedisImage,
            'redis-server',
            '--aclfile',
            '/run/secrets/redis_acl',
            '--bind',
            '0.0.0.0',
            '--protected-mode',
            'yes',
            '--save',
            '__GODSWAR_EMPTY_ARGUMENT__',
            '--appendonly',
            'no',
            '--maxmemory',
            '128mb',
            '--maxmemory-policy',
            'noeviction'
        ) `
        -Operation 'isolated Redis start'
    $containerStarted = $true
    Wait-RedisReady `
        -Username $adminUsername `
        -Password $adminPassword
    $redisPort = Get-RedisPort
    Set-TestRedisConnectionStrings -Port $redisPort

    Invoke-PolicyChecks
    Assert-NoEviction `
        -Username $adminUsername `
        -Password $adminPassword
    Assert-AuthenticationRequired -Scenario 'acl'
    Assert-ApplicationAclBoundary
    Reset-AclDenialLog
    $scenarios.Add([ordered]@{
        name = 'acl'
        status = 'passed'
    })
    Invoke-ProtocolChecks -Scenario 'initial'
    Assert-NoAclDenials -Scenario 'initial'
    Assert-DatabaseEmpty `
        -Username $adminUsername `
        -Password $adminPassword
    $scenarios.Add([ordered]@{
        name = 'initial'
        status = 'passed'
    })

    $null = Invoke-Docker `
        -Arguments @('pause', $containerName) `
        -Operation 'Redis outage pause'
    $containerPaused = $true
    Assert-RedisUnavailableFailsClosed
    $null = Invoke-Docker `
        -Arguments @('unpause', $containerName) `
        -Operation 'Redis outage recovery'
    $containerPaused = $false
    Wait-RedisReady `
        -Username $adminUsername `
        -Password $adminPassword
    $scenarios.Add([ordered]@{
        name = 'paused-unavailable'
        status = 'passed'
    })

    Seed-RestartState
    $null = Invoke-Docker `
        -Arguments @('restart', $containerName) `
        -Operation 'Redis restart'
    Wait-RedisReady `
        -Username $adminUsername `
        -Password $adminPassword
    $redisPort = Get-RedisPort
    Set-TestRedisConnectionStrings -Port $redisPort
    Assert-RestartStateLoss
    Reset-AclDenialLog
    Invoke-ProtocolChecks -Scenario 'restart-state-loss-recovery'
    Assert-NoAclDenials -Scenario 'restart-state-loss-recovery'
    Assert-DatabaseEmpty `
        -Username $adminUsername `
        -Password $adminPassword
    $scenarios.Add([ordered]@{
        name = 'restart-state-loss-recovery'
        status = 'passed'
    })

    $null = Invoke-RedisCli `
        -Username $adminUsername `
        -Password $adminPassword `
        -Command @('FLUSHDB') `
        -Operation 'Redis disposable cache flush'
    Reset-AclDenialLog
    Invoke-ProtocolChecks -Scenario 'flush-recovery'
    Assert-NoAclDenials -Scenario 'flush-recovery'
    Assert-DatabaseEmpty `
        -Username $adminUsername `
        -Password $adminPassword
    $scenarios.Add([ordered]@{
        name = 'flush-recovery'
        status = 'passed'
    })
}
catch {
    $failureCategory = 'redis-gate'
    $failureMessage = $_.Exception.Message
}
finally {
    [Environment]::SetEnvironmentVariable(
        'GODSWAR_TEST_REDIS_CONNECTION_STRING',
        $previousTestConnection)
    [Environment]::SetEnvironmentVariable(
        'GODSWAR_TEST_REDIS_ADMIN_CONNECTION_STRING',
        $previousAdminConnection)
    if ($containerStarted) {
        try {
            if ($containerPaused) {
                $null = Invoke-Docker `
                    -Arguments @('unpause', $containerName) `
                    -Operation 'cleanup unpause' `
                    -AllowFailure
            }
            $owned = Invoke-Docker `
                -Arguments @(
                    'ps',
                    '--all',
                    '--quiet',
                    '--filter',
                    "name=^/$containerName`$",
                    '--filter',
                    'label=godswar.b17.disposable=true'
                ) `
                -Operation 'cleanup ownership verification' `
                -AllowFailure
            if ($owned.ExitCode -eq 0 -and
                -not [string]::IsNullOrWhiteSpace(
                    ($owned.Output -join '').Trim())) {
                $null = Invoke-Docker `
                    -Arguments @('rm', '--force', $containerName) `
                    -Operation 'isolated Redis removal' `
                    -AllowFailure
            }
            else {
                $cleanupErrors.Add(
                    'Disposable container ownership could not be verified.')
            }
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }
    if ($aclPath -and
        [IO.File]::Exists($aclPath)) {
        try {
            [IO.File]::Delete($aclPath)
        }
        catch {
            $cleanupErrors.Add(
                'Disposable ACL file cleanup failed: ' +
                $_.Exception.Message)
        }
    }
    if ($credentialDirectory -and
        [IO.Directory]::Exists($credentialDirectory)) {
        try {
            [IO.Directory]::Delete(
                $credentialDirectory,
                $false)
        }
        catch {
            $cleanupErrors.Add(
                'Disposable credential directory cleanup failed: ' +
                $_.Exception.Message)
        }
    }
    $applicationPassword = $null
    $adminPassword = $null
}

$finishedAt = [DateTimeOffset]::UtcNow
$reportDirectory = Split-Path -Parent $absoluteReportPath
if ($reportDirectory) {
    New-Item `
        -ItemType Directory `
        -Path $reportDirectory `
        -Force | Out-Null
}
$status =
    if ($failureCategory -or $cleanupErrors.Count -gt 0) {
        'failed'
    }
    else {
        'passed'
    }
[ordered]@{
    schemaVersion = 2
    gate = 'B17 mandatory disposable Redis coordination'
    status = $status
    startedAtUtc = $startedAt.ToString('O')
    finishedAtUtc = $finishedAt.ToString('O')
    durationMs = [long]($finishedAt - $startedAt).TotalMilliseconds
    sourceCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null)
    redisImage = $RedisImage
    credentialModel = [ordered]@{
        defaultUserDisabled = $true
        applicationUser = $applicationUsername
        applicationKeyPatterns = @(
            'godswar:ticket_test_*:v1:*'
            'godswar:worker_test_*:v1:*'
            'godswar:sg-*:v1:*'
            'godswar:b17-ci:v1:*'
        )
        disposableAdminUser = $adminUsername
        sharedCredential = $false
    }
    restartSemantics = [ordered]@{
        persistenceEnabled = $false
        expectedOutcome = 'state_loss_requires_fresh_authentication'
        liveTicketContinuityClaimed = $false
    }
    checks = @($checks)
    scenarios = @($scenarios)
    cleanup = [ordered]@{
        status =
            if ($cleanupErrors.Count -eq 0) {
                'passed'
            }
            else {
                'failed'
            }
        errors = @($cleanupErrors)
    }
    failureCategory = $failureCategory
    failureMessage = $failureMessage
} | ConvertTo-Json -Depth 7 |
    Set-Content -LiteralPath $absoluteReportPath -Encoding utf8

if ($status -ne 'passed') {
    throw "B17 Redis CI gate failed. See '$absoluteReportPath'."
}

Write-Host "B17 Redis CI gate passed. Report: $absoluteReportPath"
