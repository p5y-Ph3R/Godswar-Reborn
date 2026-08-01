[CmdletBinding()]
param(
    [string] $EnvironmentFile = (
        Join-Path $PSScriptRoot `
            '..\artifacts\redis-main-local\redis.local.env'
    ),
    [string] $BaseEnvironmentFile = (
        Join-Path $PSScriptRoot '..\.env'
    ),
    [switch] $RequireLivePostgres
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module `
    (Join-Path $PSScriptRoot 'RedisMainComposeValidation.psm1') `
    -Force

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        throw "Redis main Compose validation failed: $Message"
    }
}

function Get-ValidatedPort {
    param(
        [Collections.IDictionary] $Values,
        [string] $Name
    )

    Assert-True $Values.ContainsKey($Name) "base env is missing $Name"
    $port = 0
    Assert-True `
        ([int]::TryParse([string] $Values[$Name], [ref] $port)) `
        "base env contains an invalid $Name"
    Assert-True `
        ($port -ge 1 -and $port -le 65535) `
        "base env contains an out-of-range $Name"
    return $port
}

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..')
)
$environmentPath = [IO.Path]::GetFullPath($EnvironmentFile)
$baseEnvironmentPath = [IO.Path]::GetFullPath($BaseEnvironmentFile)
if (-not (Test-Path -LiteralPath $baseEnvironmentPath -PathType Leaf) -and
    -not $PSBoundParameters.ContainsKey('BaseEnvironmentFile')) {
    $baseEnvironmentPath = Join-Path $repositoryRoot '.env.example'
}
$composePath = Join-Path $repositoryRoot 'docker-compose.yml'
$overridePath = Join-Path $repositoryRoot 'docker-compose.redis.yml'
$baseWorkerOptionsPath = Join-Path $repositoryRoot 'appsettings.docker.json'
$workerOptionsPath = Join-Path `
    $repositoryRoot `
    'deploy\local\redis-coordinated-worker.json'
$redisPolicyPath = Join-Path `
    $repositoryRoot `
    'ops\redis\redis-coordination.local.conf'

foreach ($path in @(
    $baseEnvironmentPath
    $environmentPath
    $composePath
    $overridePath
    $baseWorkerOptionsPath
    $workerOptionsPath
    $redisPolicyPath
)) {
    Assert-True `
        (Test-Path -LiteralPath $path -PathType Leaf) `
        "required file is missing: $path"
}

$redisPolicy = Get-Content -LiteralPath $redisPolicyPath -Raw
Assert-True `
    ($redisPolicy -match '(?m)^save ""\s*$') `
    'local coordination Redis must disable snapshots'
Assert-True `
    ($redisPolicy -match '(?m)^appendonly no\s*$') `
    'local coordination Redis must disable append-only persistence'
Assert-True `
    ($redisPolicy -match '(?m)^maxmemory-policy noeviction\s*$') `
    'coordination Redis must fail closed instead of evicting live keys'

$workerOptions = Get-Content -LiteralPath $workerOptionsPath -Raw |
    ConvertFrom-Json
$baseWorkerOptions = Get-Content -LiteralPath $baseWorkerOptionsPath -Raw |
    ConvertFrom-Json
$normalizedWorker = $workerOptions | ConvertTo-Json -Depth 100 |
    ConvertFrom-Json
$normalizedWorker.game.worldInstances =
    $baseWorkerOptions.game.worldInstances
$normalizedWorker.authentication = $baseWorkerOptions.authentication
$normalizedWorker.PSObject.Properties.Remove('coordination')
Assert-True `
    (($normalizedWorker | ConvertTo-Json -Depth 100 -Compress) -eq
        ($baseWorkerOptions | ConvertTo-Json -Depth 100 -Compress)) `
    (
        'coordinated worker may differ from Docker defaults only in route, ' +
        'coordination, and local legacy-authentication settings'
    )
$routes = @($workerOptions.game.worldInstances.staticOpenWorldInstances)
Assert-True ($routes.Count -eq 23) 'worker must own exactly maps 0 through 22'
$mapIds = @($routes | ForEach-Object { [int] $_.mapId } | Sort-Object)
Assert-True `
    (($mapIds -join ',') -eq ((0..22) -join ',')) `
    'worker routes must contain each supported open-world map exactly once'
Assert-True `
    (@($routes | Where-Object { [int] $_.realmId -ne 1 }).Count -eq 0) `
    'all staged routes must belong to the Tempest realm'
Assert-True `
    ($workerOptions.game.worldInstances.serverNodeId -eq `
        'tempest-openworld-01') `
    'worker node identity must be stable'
Assert-True `
    ($workerOptions.coordination.provider -eq 'Redis') `
    'worker configuration must explicitly activate Redis'
Assert-True `
    ($workerOptions.coordination.environment -eq 'tempest-local') `
    'worker and ACL coordination environments must match'
Assert-True `
    (-not [bool] $workerOptions.coordination.requireTls) `
    'local-only Redis must explicitly disable TLS'

$instanceIds = [Collections.Generic.HashSet[Guid]]::new()
foreach ($route in $routes) {
    $instanceId = [Guid]::Empty
    Assert-True `
        ([Guid]::TryParse([string] $route.worldInstanceId, [ref] $instanceId)) `
        "map $($route.mapId) must have a valid world-instance UUID"
    Assert-True `
        ($instanceId -ne [Guid]::Empty) `
        "map $($route.mapId) must not use the empty UUID"
    Assert-True `
        ($instanceIds.Add($instanceId)) `
        "map $($route.mapId) must have a unique world-instance UUID"
}

$environmentText = Get-Content -LiteralPath $environmentPath -Raw
Assert-True `
    ($environmentText -notmatch '(?im)^GODSWAR_REDIS_CONNECTION_STRING=') `
    'the env file must not contain the Redis connection string'
Assert-True `
    ($environmentText -notmatch '(?im)^.*password=.*$') `
    'the env file must not contain the Redis password'

$environmentValues = Read-RedisMainEnvironmentFile `
    -Path $environmentPath `
    -Description 'Redis env file'
$baseEnvironmentValues = Read-RedisMainEnvironmentFile `
    -Path $baseEnvironmentPath `
    -Description 'base env file'
$allEnvironmentValues = Merge-RedisMainEnvironmentValues `
    -BaseValues $baseEnvironmentValues `
    -RedisValues $environmentValues
$composeSubstitutions = Get-RedisMainComposeSubstitutionNames `
    -ComposePaths @($composePath, $overridePath)
Assert-RedisMainProcessEnvironment `
    -ExpectedValues $allEnvironmentValues `
    -ComposeNames $composeSubstitutions
$environmentRegressionCount = Invoke-RedisMainValidationRegression

$expectedRedisImage =
    'redis:7.4.10-alpine@sha256:' +
    'e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2'
Assert-True `
    $environmentValues.ContainsKey('GODSWAR_REDIS_IMAGE') `
    'env file is missing GODSWAR_REDIS_IMAGE'
Assert-True `
    ($environmentValues['GODSWAR_REDIS_IMAGE'] -eq $expectedRedisImage) `
    'Redis env image must equal the reviewed digest pin'

$expectedLoginHostPort = Get-ValidatedPort `
    -Values $baseEnvironmentValues `
    -Name 'GODSWAR_LOGIN_HOST_PORT'
$expectedGameHostPort = Get-ValidatedPort `
    -Values $baseEnvironmentValues `
    -Name 'GODSWAR_GAME_HOST_PORT'
Assert-True `
    $baseEnvironmentValues.ContainsKey('GODSWAR_DEVELOPER_COMMANDS_ENABLED') `
    'base env is missing GODSWAR_DEVELOPER_COMMANDS_ENABLED'
$expectedDeveloperCommands =
    ([string] $baseEnvironmentValues[
        'GODSWAR_DEVELOPER_COMMANDS_ENABLED'
    ]).Trim().ToLowerInvariant()
Assert-True `
    ($expectedDeveloperCommands -in @('true', 'false')) `
    'base env has an invalid GODSWAR_DEVELOPER_COMMANDS_ENABLED value'

foreach ($name in @(
    'GODSWAR_REDIS_ACL_FILE'
    'GODSWAR_REDIS_PASSWORD_FILE'
    'GODSWAR_REDIS_CONNECTION_STRING_FILE_HOST'
)) {
    Assert-True `
        $environmentValues.ContainsKey($name) `
        "env file is missing $name"
    Assert-True `
        (Test-RedisMainFullyQualifiedPath (
            [string] $environmentValues[$name])) `
        "$name must contain an absolute generated-file path"
    Assert-True `
        (Test-Path -LiteralPath $environmentValues[$name] -PathType Leaf) `
        "$name must reference a generated file"
}

$secretBindings = @(
    [pscustomobject]@{
        SecretName = 'redis-main-acl'
        EnvironmentName = 'GODSWAR_REDIS_ACL_FILE'
    }
    [pscustomobject]@{
        SecretName = 'redis-main-password'
        EnvironmentName = 'GODSWAR_REDIS_PASSWORD_FILE'
    }
    [pscustomobject]@{
        SecretName = 'redis-main-connection-string'
        EnvironmentName = 'GODSWAR_REDIS_CONNECTION_STRING_FILE_HOST'
    }
)

$password = Get-Content `
    -LiteralPath $environmentValues['GODSWAR_REDIS_PASSWORD_FILE'] `
    -Raw
$acl = Get-Content `
    -LiteralPath $environmentValues['GODSWAR_REDIS_ACL_FILE'] `
    -Raw
$connectionString = Get-Content `
    -LiteralPath `
        $environmentValues['GODSWAR_REDIS_CONNECTION_STRING_FILE_HOST'] `
    -Raw
Assert-True ($password -match '^[a-f0-9]{64}$') 'password must be 256-bit hex'
Assert-True ($acl -match '(?m)^user default off\s*$') 'default Redis user must be disabled'
Assert-True `
    ($acl.Contains('user godswar_runtime reset on')) `
    'ACL must contain only the intended runtime identity'
Assert-True `
    ($acl.Contains('~godswar:tempest-local:v1:*')) `
    'ACL must be scoped to the Tempest local key prefix'
Assert-True `
    ($acl.Contains('+hmget') -and $acl.Contains('+pttl')) `
    'ACL must permit every command used by coordinated Lua workflows'
Assert-True ($acl.Contains(">$password ")) 'ACL and password secret must match'
Assert-True `
    ($connectionString.StartsWith('redis-coordination:6379,')) `
    'server must use the private Docker DNS endpoint'
Assert-True `
    ($connectionString.Contains('user=godswar_runtime')) `
    'connection string must select the scoped runtime identity'
Assert-True `
    ($connectionString.Contains("password=$password")) `
    'connection-string and password secrets must match'
Assert-True `
    (-not $connectionString.Contains("`n") -and
        -not $connectionString.Contains("`r")) `
    'connection-string secret must be a single bounded line'
Assert-True `
    ([Text.Encoding]::UTF8.GetByteCount($connectionString) -le 4096) `
    'connection-string secret must satisfy the runtime byte bound'

$docker = Get-Command docker -ErrorAction SilentlyContinue
Assert-True ($null -ne $docker) 'Docker CLI is required for canonical rendering'
$arguments = @(
    'compose'
    '--project-name'
    'reborn'
    '--env-file'
    $baseEnvironmentPath
    '--env-file'
    $environmentPath
    '-f'
    $composePath
    '-f'
    $overridePath
    '--profile'
    'redis-coordinated'
    'config'
    '--format'
    'json'
)
$model = Get-RedisMainComposeModel `
    -DockerPath $docker.Source `
    -Arguments $arguments
Assert-RedisMainRenderedSecrets `
    -Model $model `
    -RedisValues $environmentValues `
    -Bindings $secretBindings
Assert-RedisMainRenderedContinuity `
    -Model $model `
    -BaseValues $baseEnvironmentValues
$precedenceProbeCount = Invoke-RedisMainSecretOverrideProbes `
    -DockerPath $docker.Source `
    -Arguments $arguments `
    -RedisValues $environmentValues `
    -Bindings $secretBindings
$livePostgresValidated = Assert-RedisMainLivePostgres `
    -DockerPath $docker.Source `
    -BaseValues $baseEnvironmentValues `
    -Required:$RequireLivePostgres

Assert-True ($model.name -eq 'reborn') 'Compose project must be named reborn'
$redis = $model.services.'redis-coordination'
$server = $model.services.server
Assert-True ($null -ne $redis) 'Redis service must exist in the main model'
Assert-True ($null -ne $server) 'coordinated server overlay must exist'
$redisPorts = @()
$redisPortsProperty = $redis.PSObject.Properties['ports']
if ($null -ne $redisPortsProperty) {
    $redisPorts = @($redisPortsProperty.Value)
}
Assert-True `
    ($redis.container_name -eq 'godswar-main-redis-coordination') `
    'Redis container identity must not collide with the isolated B17 gate'
Assert-True `
    ($redis.image -eq $expectedRedisImage) `
    'rendered Redis image must equal the reviewed digest pin'
Assert-True ([bool] $redis.read_only) 'Redis root filesystem must be read-only'
Assert-True ($redis.restart -eq 'unless-stopped') 'Redis must survive host restart'
Assert-True ($redisPorts.Count -eq 0) 'Redis must publish no host port'
Assert-True `
    (@($redis.cap_drop) -contains 'ALL') `
    'Redis must drop all Linux capabilities'
Assert-True `
    (@($redis.security_opt) -contains 'no-new-privileges:true') `
    'Redis must deny privilege escalation'
Assert-True `
    (@($redis.profiles) -contains 'redis-coordinated') `
    'Redis must remain opt-in'
Assert-True `
    (@($redis.tmpfs) -match '^/tmp:' -and
        @($redis.tmpfs) -match '^/data:') `
    'Redis writable paths must be disposable tmpfs mounts'
Assert-True `
    (@($server.profiles) -contains 'redis-coordinated') `
    'server coordination overlay must remain opt-in'
Assert-True `
    ($server.environment.GODSWAR_COORDINATION_PROVIDER -ceq 'Redis') `
    'rendered server must use Redis coordination'
Assert-True `
    ($server.environment.GODSWAR_COORDINATION_ENVIRONMENT -ceq `
        'tempest-local') `
    'rendered server must use the reviewed coordination environment'
Assert-True `
    ($server.environment.GODSWAR_WORLD_INSTANCE_SERVER_NODE_ID -ceq `
        'tempest-openworld-01') `
    'rendered server must use the stable world-owner node ID'
Assert-True `
    ($server.labels.'com.reborn.coordination.provider' -ceq 'redis') `
    'rendered server must carry the Redis provider label'
Assert-True `
    ($server.labels.'com.reborn.world.owner' -ceq `
        'tempest-openworld-01') `
    'rendered server must carry the exact world-owner label'
Assert-True `
    ($server.environment.GODSWAR_REDIS_CONNECTION_STRING_FILE -eq `
        '/run/secrets/redis_connection_string') `
    'server must consume the connection string through its secret file'
Assert-True `
    (-not $server.environment.PSObject.Properties[
        'GODSWAR_REDIS_CONNECTION_STRING']) `
    'server environment must not expose the Redis connection string'
Assert-True `
    (@($server.secrets).target -contains 'redis_connection_string') `
    'server must mount the generated connection-string secret'
Assert-True `
    ($null -ne $server.depends_on.'redis-coordination') `
    'server must wait for Redis health'
Assert-True `
    (@($server.entrypoint) -contains `
        '/app/config/redis-coordinated-worker.json') `
    'server must load the exact coordinated-worker configuration'

$workerMounts = @(
    @($server.volumes) | Where-Object {
        $_.target -ceq '/app/config/redis-coordinated-worker.json'
    }
)
Assert-True `
    ($workerMounts.Count -eq 1) `
    'server must mount exactly one coordinated-worker configuration'
$pathComparison = if ($env:OS -eq 'Windows_NT') {
    [StringComparison]::OrdinalIgnoreCase
}
else {
    [StringComparison]::Ordinal
}
Assert-True `
    ([string] $workerMounts[0].source).Equals(
        [IO.Path]::GetFullPath($workerOptionsPath),
        $pathComparison) `
    'worker configuration bind must use the reviewed exact source'
Assert-True `
    ([bool] $workerMounts[0].read_only) `
    'worker configuration bind must be read-only'

$serverPorts = @($server.ports)
$loginPorts = @(
    $serverPorts | Where-Object {
        [int] $_.target -eq 5999 -and $_.protocol -eq 'tcp'
    }
)
$gamePorts = @(
    $serverPorts | Where-Object {
        [int] $_.target -eq 7000 -and $_.protocol -eq 'tcp'
    }
)
Assert-True `
    ($loginPorts.Count -eq 1) `
    'server must publish exactly one TCP login port'
Assert-True `
    ($gamePorts.Count -eq 1) `
    'server must publish exactly one TCP game port'
Assert-True `
    ([int] $loginPorts[0].published -eq $expectedLoginHostPort) `
    'Redis overlay must preserve the base login host port'
Assert-True `
    ([int] $gamePorts[0].published -eq $expectedGameHostPort) `
    'Redis overlay must preserve the base game host port'
Assert-True `
    ($loginPorts[0].host_ip -eq '127.1.1.110' -and
        $gamePorts[0].host_ip -eq '127.1.1.110') `
    'Redis overlay must preserve loopback-only legacy bindings'
Assert-True `
    (([string] $server.environment.GODSWAR_DEVELOPER_COMMANDS_ENABLED).
        ToLowerInvariant() -eq $expectedDeveloperCommands) `
    'Redis overlay must preserve the base developer-command setting'

[pscustomobject]@{
    Status = 'passed'
    Project = $model.name
    RedisService = 'redis-coordination'
    PublishedRedisPorts = $redisPorts.Count
    WorkerNode = $workerOptions.game.worldInstances.serverNodeId
    OwnedMapCount = $routes.Count
    OwnedMapMinimum = $mapIds[0]
    OwnedMapMaximum = $mapIds[-1]
    LoginHostPort = $expectedLoginHostPort
    GameHostPort = $expectedGameHostPort
    DeveloperCommandsEnabled = $expectedDeveloperCommands
    EnvironmentGuardRegressions = $environmentRegressionCount
    EnvironmentPrecedenceProbes = $precedenceProbeCount
    LivePostgresValidated = [bool] $livePostgresValidated
    ConnectionSecretInEnvironment = $false
}
