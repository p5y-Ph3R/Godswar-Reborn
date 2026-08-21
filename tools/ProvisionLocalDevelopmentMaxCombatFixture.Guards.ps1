Set-StrictMode -Version Latest

function Get-MaxFixtureContainer([string]$Name) {
    $raw = & docker container inspect $Name 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect required container '$Name'."
    }
    try {
        $containers = @($raw | ConvertFrom-Json)
        if ($containers.Count -ne 1) { throw 'unexpected result count' }
        return $containers[0]
    }
    catch {
        throw "Docker returned invalid inspection data for '$Name'."
    }
}

function Assert-MaxFixtureContainer(
    $Container,
    [string]$Name,
    [string]$DataRole
) {
    if ($Container.Name -ne "/$Name") {
        throw "Refusing unexpected container identity '$($Container.Name)'."
    }
    if ($Container.Config.Labels.'com.reborn.environment.scope' -ne
        'isolated-development') {
        throw "Container '$Name' is not isolated development."
    }
    if ($DataRole -and
        $Container.Config.Labels.'com.reborn.data.role' -ne $DataRole) {
        throw "Container '$Name' has an unexpected data role."
    }
}

function Initialize-MaxFixtureEnvironment(
    [string]$PostgresName,
    [string]$ServerName,
    [string]$RedisName
) {
    if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker is required but was not found on PATH.'
    }
    $postgres = Get-MaxFixtureContainer $PostgresName
    $server = Get-MaxFixtureContainer $ServerName
    $redis = Get-MaxFixtureContainer $RedisName
    Assert-MaxFixtureContainer $postgres $PostgresName `
        'cloned-nonproduction-authority'
    Assert-MaxFixtureContainer $server $ServerName ''
    Assert-MaxFixtureContainer $redis $RedisName `
        'disposable-coordination'
    foreach ($container in @($postgres, $redis)) {
        if (-not $container.State.Running) {
            throw "Required data container '$($container.Name)' is stopped."
        }
        $health = $container.State.PSObject.Properties['Health']
        if ($null -ne $health -and $null -ne $health.Value -and
            $health.Value.Status -ne 'healthy') {
            throw "Required data container '$($container.Name)' is unhealthy."
        }
    }
    if (-not (@($server.Config.Env) -contains
            'GODSWAR_RUNTIME_PROFILE=LocalDevelopment')) {
        throw "Server container '$ServerName' is not LocalDevelopment."
    }
    $dataMount = @($postgres.Mounts | Where-Object {
        $_.Destination -eq '/var/lib/postgresql/data'
    })
    if ($dataMount.Count -ne 1 -or
        $dataMount[0].Name -ne 'godswar-dev-postgres-data') {
        throw 'PostgreSQL is not using the isolated development volume.'
    }
    [pscustomobject]@{
        Postgres = $postgres
        Server = $server
        Redis = $redis
    }
}

function Get-MaxFixtureOpaqueId([string]$Domain, [byte[]]$Value) {
    [byte[]]$domainBytes = [Text.Encoding]::ASCII.GetBytes($Domain)
    [byte[]]$opaqueInput = [byte[]]::new(
        $domainBytes.Length + 1 + $Value.Length)
    [Array]::Copy($domainBytes, 0, $opaqueInput, 0, $domainBytes.Length)
    [Array]::Copy(
        $Value, 0, $opaqueInput, $domainBytes.Length + 1, $Value.Length)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hex = -join ($sha.ComputeHash($opaqueInput) | ForEach-Object {
            $_.ToString('X2')
        })
        return $hex.Substring(0, 32)
    }
    finally {
        $sha.Dispose()
        [Array]::Clear($domainBytes, 0, $domainBytes.Length)
        [Array]::Clear($opaqueInput, 0, $opaqueInput.Length)
    }
}

function Get-MaxFixtureLeaseKeys([object[]]$Identities) {
    $prefix = 'godswar:tempest-dev:v1'
    $keys = [Collections.Generic.List[string]]::new()
    foreach ($identity in $Identities) {
        $expectedIdProperty = $identity.PSObject.Properties['expectedId']
        if ($null -eq $expectedIdProperty -or
            [int]$expectedIdProperty.Value -notin 7001..7005) {
            throw 'Status omitted a reserved max-combat fixture ID.'
        }
        $reservedId = [int]$expectedIdProperty.Value
        $usernameBytes = [Text.Encoding]::UTF8.GetBytes($identity.Username)
        try {
            $keys.Add($prefix + ':login-name:' +
                (Get-MaxFixtureOpaqueId 'username' $usernameBytes))
        }
        finally { [Array]::Clear($usernameBytes, 0, $usernameBytes.Length) }
        $bytes = [BitConverter]::GetBytes(
            [Net.IPAddress]::HostToNetworkOrder($reservedId))
        $keys.Add($prefix + ':login-account:' +
            (Get-MaxFixtureOpaqueId 'account' $bytes))
        $keys.Add($prefix + ':player:' +
            (Get-MaxFixtureOpaqueId 'character' $bytes))
    }
    if ($keys.Count -ne 15 -or @($keys | Select-Object -Unique).Count -ne 15) {
        throw 'Status did not yield exactly five unique reserved identities.'
    }
    return $keys.ToArray()
}

function Get-MaxFixtureRedisKeyCount(
    [string]$RedisName,
    [object[]]$Identities
) {
    $keys = Get-MaxFixtureLeaseKeys $Identities
    $passwordPath = Join-Path $PSScriptRoot `
        '..\artifacts\development-stack\redis.password'
    if (-not (Test-Path -LiteralPath $passwordPath -PathType Leaf)) {
        throw 'The isolated-development Redis secret is missing.'
    }
    $password = (Get-Content -Raw -LiteralPath $passwordPath).Trim()
    if ($password -notmatch '^[a-f0-9]{64}$') {
        throw 'The isolated-development Redis secret is malformed.'
    }
    try {
        $output = & docker exec --env "REDISCLI_AUTH=$password" `
            $RedisName redis-cli --user godswar_runtime `
            --no-auth-warning -n 0 EXISTS $keys 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally { $password = $null }
    $count = 0
    if ($exitCode -ne 0 -or
        -not [int]::TryParse(
            (@($output)[-1].ToString().Trim()), [ref]$count)) {
        throw 'Could not verify isolated Redis player/login lease absence.'
    }
    return $count
}

function Test-MaxFixtureOriginRunning {
    $processes = @(Get-Process Origin -ErrorAction SilentlyContinue)
    try { return $processes.Count -ne 0 }
    finally { foreach ($process in $processes) { $process.Dispose() } }
}

function Assert-MaxFixtureOffline(
    $Environment,
    [string]$RedisName,
    [object[]]$Identities
) {
    $serverName = $Environment.Server.Name.TrimStart('/')
    $server = Get-MaxFixtureContainer $serverName
    Assert-MaxFixtureContainer $server $serverName ''
    if ($server.State.Running) { throw "Stop '$serverName' cleanly." }
    if (Test-MaxFixtureOriginRunning) {
        throw 'Close Origin.exe before applying the max-combat fixture.'
    }
    if ((Get-MaxFixtureRedisKeyCount $RedisName $Identities) -ne 0) {
        throw 'Redis still has a target player or login lease.'
    }
}
