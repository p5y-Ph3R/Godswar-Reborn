Set-StrictMode -Version Latest

function Get-RepairSha256Hex([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
        -join ($sha.ComputeHash($bytes) | ForEach-Object {
            $_.ToString('x2')
        })
    }
    finally { $sha.Dispose() }
}

function Get-RepairContainer([string]$Name) {
    $raw = & docker container inspect $Name 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect required container '$Name'."
    }
    try {
        $value = @($raw | ConvertFrom-Json)
        if ($value.Count -ne 1) { throw 'unexpected result count' }
        $value[0]
    }
    catch {
        throw "Docker returned invalid inspection data for '$Name'."
    }
}

function Assert-RepairContainer(
    $Container,
    [string]$Name,
    [string]$DataRole
) {
    if ($Container.Name -ne "/$Name") {
        throw "Refusing unexpected container identity '$($Container.Name)'."
    }
    $labels = $Container.Config.Labels
    if ($labels.'com.reborn.environment.scope' -ne
        'isolated-development') {
        throw "Container '$Name' is not isolated development."
    }
    if ($DataRole -and
        $labels.'com.reborn.data.role' -ne $DataRole) {
        throw "Container '$Name' has an unexpected data role."
    }
}

function Initialize-RebirthRepairEnvironment(
    [string]$PostgresName,
    [string]$ServerName,
    [string]$RedisName
) {
    if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker is required but was not found on PATH.'
    }
    $postgres = Get-RepairContainer $PostgresName
    $server = Get-RepairContainer $ServerName
    $redis = Get-RepairContainer $RedisName
    Assert-RepairContainer $postgres $PostgresName `
        'cloned-nonproduction-authority'
    Assert-RepairContainer $server $ServerName ''
    Assert-RepairContainer $redis $RedisName 'disposable-coordination'
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

function Get-RepairOpaqueId([string]$Domain, [byte[]]$Value) {
    [byte[]]$domainBytes = [Text.Encoding]::ASCII.GetBytes($Domain)
    [byte[]]$hashInput = [byte[]]::new(
        $domainBytes.Length + 1 + $Value.Length)
    [Array]::Copy($domainBytes, 0, $hashInput, 0, $domainBytes.Length)
    [Array]::Copy(
        $Value, 0, $hashInput, $domainBytes.Length + 1, $Value.Length)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hex = -join ($sha.ComputeHash($hashInput) | ForEach-Object {
            $_.ToString('X2')
        })
        $hex.Substring(0, 32)
    }
    finally {
        $sha.Dispose()
        [Array]::Clear($domainBytes, 0, $domainBytes.Length)
        [Array]::Clear($hashInput, 0, $hashInput.Length)
    }
}

function Get-RebirthRepairRedisKeyCount([string]$RedisName) {
    $prefix = 'godswar:tempest-dev:v1'
    $keys = @(
        $prefix + ':player:' +
            (Get-RepairOpaqueId 'character' ([byte[]](0, 0, 0, 2)))
        $prefix + ':login-account:' +
            (Get-RepairOpaqueId 'account' ([byte[]](0, 0, 0, 13)))
        $prefix + ':login-name:' +
            (Get-RepairOpaqueId 'username' (
                [Text.Encoding]::UTF8.GetBytes('test2')))
    )
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
        throw 'Could not verify isolated Redis player/login key absence.'
    }
    $count
}

function Test-OriginRunning {
    $processes = @(Get-Process Origin -ErrorAction SilentlyContinue)
    try { $processes.Count -ne 0 }
    finally {
        foreach ($process in $processes) { $process.Dispose() }
    }
}

function Assert-RebirthRepairOffline(
    $Environment,
    [string]$RedisName
) {
    $serverName = $Environment.Server.Name.TrimStart('/')
    $currentServer = Get-RepairContainer $serverName
    Assert-RepairContainer $currentServer $serverName ''
    if ($currentServer.State.Running) {
        throw "Stop '$serverName' cleanly."
    }
    if (Test-OriginRunning) {
        throw 'Close Origin.exe before applying the pet rebirth repair.'
    }
    if ((Get-RebirthRepairRedisKeyCount $RedisName) -ne 0) {
        throw 'Redis still has a player/login lease for account 13 or pet owner 2.'
    }
}
