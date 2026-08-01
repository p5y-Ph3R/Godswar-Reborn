function New-RandomHexSecret {
    $bytes = [byte[]]::new(32)
    $generator =
        [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
        return -join ($bytes | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $generator.Dispose()
    }
}

function Invoke-RedisCli {
    param(
        [Parameter(Mandatory)]
        [string]$Username,
        [Parameter(Mandatory)]
        [string]$Password,
        [Parameter(Mandatory)]
        [string[]]$Command,
        [Parameter(Mandatory)]
        [string]$Operation,
        [switch]$AllowFailure
    )

    return Invoke-Docker `
        -Arguments (@(
                'exec',
                $containerName,
                'redis-cli',
                '--no-auth-warning',
                '--user',
                $Username,
                '-a',
                $Password,
                '--raw'
            ) + $Command) `
        -Operation $Operation `
        -AllowFailure:$AllowFailure
}

function Set-TestRedisConnectionStrings {
    param(
        [Parameter(Mandatory)]
        [int]$Port
    )

    $common =
        'ssl=False,abortConnect=True,connectTimeout=3000,' +
        'asyncTimeout=2000,syncTimeout=2000'
    [Environment]::SetEnvironmentVariable(
        'GODSWAR_TEST_REDIS_CONNECTION_STRING',
        "127.0.0.1:$Port,user=$applicationUsername," +
        "password=$applicationPassword,$common")
    [Environment]::SetEnvironmentVariable(
        'GODSWAR_TEST_REDIS_ADMIN_CONNECTION_STRING',
        "127.0.0.1:$Port,user=$adminUsername," +
        "password=$adminPassword,$common")
}

function Assert-Noperm {
    param(
        [Parameter(Mandatory)]
        [string[]]$Command,
        [Parameter(Mandatory)]
        [string]$Description
    )

    $result = Invoke-RedisCli `
        -Username $applicationUsername `
        -Password $applicationPassword `
        -Command $Command `
        -Operation $Description `
        -AllowFailure
    if (($result.Output -join "`n") -notmatch '(?i)\bNOPERM\b') {
        throw "Application ACL unexpectedly allowed $Description."
    }
}

function Assert-AuthenticationRequired {
    param(
        [Parameter(Mandatory)]
        [string]$Scenario
    )

    $result = Invoke-Docker `
        -Arguments @(
            'exec',
            $containerName,
            'redis-cli',
            '--raw',
            'PING'
        ) `
        -Operation 'unauthenticated Redis probe' `
        -AllowFailure
    if (($result.Output -join "`n") -notmatch '(?i)\bNOAUTH\b') {
        throw 'Disposable Redis accepted an unauthenticated command.'
    }
    $checks.Add([ordered]@{
        scenario = $Scenario
        name = 'Redis requires explicit authentication'
        status = 'passed'
    })
}

function Assert-ApplicationAclBoundary {
    $probeKey = 'godswar:b17-ci:v1:acl-probe'
    $stringProbeKey = 'godswar:b17-ci:v1:acl-string-probe'
    $set = Invoke-RedisCli `
        -Username $applicationUsername `
        -Password $applicationPassword `
        -Command @('HSET', $probeKey, 'value', 'allowed') `
        -Operation 'in-scope application write'
    $read = Invoke-RedisCli `
        -Username $applicationUsername `
        -Password $applicationPassword `
        -Command @('HGET', $probeKey, 'value') `
        -Operation 'in-scope application read'
    if (($set.Output -join '').Trim() -ne '1' -or
        ($read.Output -join '').Trim() -ne 'allowed') {
        throw 'Application ACL rejected its in-scope keyspace.'
    }
    $stringSet = Invoke-RedisCli `
        -Username $applicationUsername `
        -Password $applicationPassword `
        -Command @('SET', $stringProbeKey, 'allowed') `
        -Operation 'in-scope realm-content write'
    $stringRead = Invoke-RedisCli `
        -Username $applicationUsername `
        -Password $applicationPassword `
        -Command @('GET', $stringProbeKey) `
        -Operation 'in-scope realm-content read'
    if (($stringSet.Output -join '').Trim() -ne 'OK' -or
        ($stringRead.Output -join '').Trim() -ne 'allowed') {
        throw 'Application ACL rejected realm-content coordination.'
    }
    $time = Invoke-RedisCli `
        -Username $applicationUsername `
        -Password $applicationPassword `
        -Command @('TIME') `
        -Operation 'application Redis clock access'
    $timeParts = @($time.Output | Where-Object {
        -not [string]::IsNullOrWhiteSpace("$_")
    })
    $seconds = 0L
    $microseconds = 0L
    if ($timeParts.Count -ne 2 -or
        -not [long]::TryParse($timeParts[0], [ref]$seconds) -or
        -not [long]::TryParse($timeParts[1], [ref]$microseconds)) {
        throw 'Application ACL rejected bounded Redis TIME access.'
    }
    $null = Invoke-RedisCli `
        -Username $applicationUsername `
        -Password $applicationPassword `
        -Command @('DEL', $probeKey, $stringProbeKey) `
        -Operation 'application ACL probe cleanup'
    $checks.Add([ordered]@{
        scenario = 'acl'
        name = 'Application ACL permits only its in-scope workflow'
        status = 'passed'
    })

    Assert-Noperm `
        -Command @(
            'HSET',
            'godswar:outside-b17-ci:v1:forbidden',
            'value',
            'denied'
        ) `
        -Description 'out-of-scope key write'
    $checks.Add([ordered]@{
        scenario = 'acl'
        name = 'Application ACL denies out-of-scope keys'
        status = 'passed'
    })

    Assert-Noperm `
        -Command @('FLUSHDB') `
        -Description 'application FLUSHDB'
    Assert-Noperm `
        -Command @('CONFIG', 'GET', 'maxmemory-policy') `
        -Description 'application CONFIG'
    Assert-Noperm `
        -Command @('CLIENT', 'PAUSE', '1', 'ALL') `
        -Description 'application CLIENT PAUSE'
    Assert-Noperm `
        -Command @('KEYS', 'godswar:*') `
        -Description 'application KEYS'
    Assert-Noperm `
        -Command @('MSET', $probeKey, 'denied') `
        -Description 'unapproved application MSET'
    Assert-Noperm `
        -Command @('SCRIPT', 'FLUSH') `
        -Description 'application SCRIPT FLUSH'
    Assert-Noperm `
        -Command @('ACL', 'WHOAMI') `
        -Description 'application ACL'
    Assert-Noperm `
        -Command @('PUBLISH', 'b17-ci', 'denied') `
        -Description 'application PUBLISH'
    $checks.Add([ordered]@{
        scenario = 'acl'
        name = 'Application ACL denies administrative commands'
        status = 'passed'
    })
}

function Seed-RestartState {
    $result = Invoke-RedisCli `
        -Username $applicationUsername `
        -Password $applicationPassword `
        -Command @('HSET', $restartSeedKey, 'value', 'must-be-lost') `
        -Operation 'pre-restart disposable state seed'
    if (($result.Output -join '').Trim() -ne '1') {
        throw 'Could not seed disposable pre-restart coordination state.'
    }
}

function Reset-AclDenialLog {
    $result = Invoke-RedisCli `
        -Username $adminUsername `
        -Password $adminPassword `
        -Command @('ACL', 'LOG', 'RESET') `
        -Operation 'ACL denial-log reset'
    if (($result.Output -join '').Trim() -ne 'OK') {
        throw 'Could not reset the disposable Redis ACL denial log.'
    }
}

function Assert-NoAclDenials {
    param(
        [Parameter(Mandatory)]
        [string]$Scenario
    )

    $result = Invoke-RedisCli `
        -Username $adminUsername `
        -Password $adminPassword `
        -Command @('ACL', 'LOG', '10') `
        -Operation 'ACL denial-log inspection'
    $evidence = ($result.Output -join ' ').Trim()
    if (-not [string]::IsNullOrWhiteSpace($evidence)) {
        if ($evidence.Length -gt 512) {
            $evidence = $evidence.Substring(0, 512)
        }
        throw (
            "Application checks produced an ACL denial in '$Scenario': " +
            $evidence)
    }
    $checks.Add([ordered]@{
        scenario = $Scenario
        name = 'Application checks produce no unexpected ACL denials'
        status = 'passed'
    })
}

function Assert-RestartStateLoss {
    $exists = Invoke-RedisCli `
        -Username $adminUsername `
        -Password $adminPassword `
        -Command @('EXISTS', $restartSeedKey) `
        -Operation 'post-restart state-loss verification'
    if (($exists.Output -join '').Trim() -ne '0') {
        throw 'Non-persistent Redis retained state across restart.'
    }
    $checks.Add([ordered]@{
        scenario = 'restart-state-loss'
        name = 'Restart loses disposable coordination state'
        status = 'passed'
    })

    Assert-AuthenticationRequired -Scenario 'restart-state-loss'
    $authenticated = Invoke-RedisCli `
        -Username $applicationUsername `
        -Password $applicationPassword `
        -Command @('PING') `
        -Operation 'post-restart application re-authentication'
    if (($authenticated.Output -join '').Trim() -ne 'PONG') {
        throw 'Application credential could not re-authenticate after restart.'
    }
    $checks.Add([ordered]@{
        scenario = 'restart-state-loss'
        name = 'Application re-authenticates after state-loss restart'
        status = 'passed'
    })
}
