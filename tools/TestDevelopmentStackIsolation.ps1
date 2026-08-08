[CmdletBinding()]
param(
    [string]$ConfigurationDirectory,
    [switch]$RequireLive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevelopmentStack.Common.psm1') -Force

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$environmentPath = Get-DevelopmentEnvironmentPath $ConfigurationDirectory
$compose = Get-DevelopmentComposeArguments $environmentPath
$renderedRaw = & docker @($compose + @('config', '--format', 'json'))
if ($LASTEXITCODE -ne 0) {
    throw 'The isolated development Compose configuration did not render.'
}
$rendered = $renderedRaw | ConvertFrom-Json
$serviceNames = @($rendered.services.PSObject.Properties.Name | Sort-Object)
Assert-Condition `
    (($serviceNames -join ',') -ceq 'postgres,redis-coordination,server') `
    'Development Compose must contain exactly three isolated services.'

$postgres = $rendered.services.postgres
$redis = $rendered.services.'redis-coordination'
$server = $rendered.services.server
Assert-Condition ($postgres.container_name -ceq 'godswar-dev-postgres') `
    'Development PostgreSQL container name is not isolated.'
Assert-Condition `
    ($redis.container_name -ceq 'godswar-dev-redis-coordination') `
    'Development Redis container name is not isolated.'
Assert-Condition ($server.container_name -ceq 'godswar-dev-server') `
    'Development server container name is not isolated.'
Assert-Condition ($server.image -ceq 'reborn-server:dev') `
    'Development server must use only the dev image tag.'
Assert-Condition `
    ($server.environment.GODSWAR_GAME_PUBLIC_HOST -ceq '127.1.1.111') `
    'Development game redirects must target the dev loopback IP.'
Assert-Condition `
    ($server.environment.GODSWAR_COORDINATION_ENVIRONMENT -ceq 'tempest-dev') `
    'Development Redis keys must use the dev coordination namespace.'
Assert-Condition `
    ($server.environment.GODSWAR_WORLD_INSTANCE_SERVER_NODE_ID -ceq
        'tempest-dev-openworld-01') `
    'Development world ownership must use the dev node identity.'
Assert-Condition `
    ($null -eq $server.environment.PSObject.Properties[
        'GODSWAR_POSTGRES_CONNECTION_STRING']) `
    'Development server must not expose PostgreSQL credentials in its environment.'
Assert-Condition `
    ($server.environment.GODSWAR_POSTGRES_CONNECTION_STRING_FILE -ceq
        '/run/secrets/postgres_connection_string') `
    'Development server must load PostgreSQL through its file secret.'
Assert-Condition `
    ([string]$server.environment.GODSWAR_GAME_PUBLIC_PORT -ceq
        [string]$server.ports.Where({ $_.target -eq 7000 })[0].published) `
    'Development public game port must match its published host port.'

$postgresConnectionSecret = @($server.secrets | Where-Object {
    $_.source -ceq 'dev-postgres-connection-string' -and
    $_.target -ceq 'postgres_connection_string'
})
Assert-Condition ($postgresConnectionSecret.Count -eq 1) `
    'Development server PostgreSQL secret mapping is missing or duplicated.'
$postgresConnectionPath = Get-DotEnvValue `
    $environmentPath 'GODSWAR_DEV_POSTGRES_CONNECTION_STRING_FILE'
$postgresConnection = Read-DevelopmentSecretFile $postgresConnectionPath
Assert-Condition ($postgresConnection -match '^Host=postgres;') `
    'Development PostgreSQL secret must target the project-local service.'
$postgresPasswordPath = Get-DotEnvValue `
    $environmentPath 'GODSWAR_DEV_POSTGRES_PASSWORD_FILE'
$postgresPassword = Read-DevelopmentSecretFile $postgresPasswordPath
try {
    Assert-Condition `
        (($renderedRaw -join "`n").IndexOf(
            $postgresPassword,
            [StringComparison]::Ordinal) -lt 0) `
        'Rendered Compose must not expose the development PostgreSQL password.'
}
finally {
    $postgresPassword = $null
    $postgresConnection = $null
}

$published = @($server.ports | ForEach-Object {
    "$($_.host_ip):$($_.published):$($_.target)/$($_.protocol)"
})
Assert-Condition `
    ($published -contains '127.1.1.111:5998:5999/tcp') `
    'Development login endpoint is not 127.1.1.111:5998.'
Assert-Condition `
    ($published -contains '127.1.1.111:7000:7000/tcp') `
    'Development game endpoint is not 127.1.1.111:7000.'
Assert-Condition `
    (-not ($published -match '127\.1\.1\.110')) `
    'Development Compose overlaps the monitored loopback IP.'
$postgresPublished = @($postgres.ports | ForEach-Object {
    "$($_.host_ip):$($_.published):$($_.target)/$($_.protocol)"
})
Assert-Condition `
    ($postgresPublished -contains '127.0.0.1:55432:5432/tcp') `
    'Development PostgreSQL host port must be 127.0.0.1:55432.'

Assert-Condition `
    ($rendered.volumes.'godswar-dev-postgres-data'.name -ceq
        'godswar-dev-postgres-data') `
    'Development PostgreSQL volume name is not isolated.'
Assert-Condition `
    ($rendered.networks.default.name -ceq 'reborn_dev_runtime') `
    'Development network name is not isolated.'

$aclPath = Get-DotEnvValue $environmentPath 'GODSWAR_DEV_REDIS_ACL_FILE'
$acl = Get-Content -LiteralPath $aclPath -Raw
Assert-Condition ($acl -match '~godswar:tempest-dev:v1:\*') `
    'Development Redis ACL does not use the dev key prefix.'
Assert-Condition (-not ($acl -match '~godswar:tempest-local:v1:\*')) `
    'Development Redis ACL overlaps the monitored key prefix.'

if ($RequireLive) {
    $live = @(
        @{ Name = 'godswar-dev-postgres'; Service = 'postgres' }
        @{ Name = 'godswar-dev-redis-coordination'; Service = 'redis-coordination' }
        @{ Name = 'godswar-dev-server'; Service = 'server' }
    )
    foreach ($entry in $live) {
        $container = Assert-DevelopmentContainer `
            $entry.Name $entry.Service
        Assert-Condition ($container.State.Status -ceq 'running') `
            "$($entry.Name) is not running."
        Assert-Condition ($container.State.Health.Status -ceq 'healthy') `
            "$($entry.Name) is not healthy."
    }
    $devPostgres = Get-DockerContainer 'godswar-dev-postgres'
    $devVolumes = @($devPostgres.Mounts | Where-Object {
        $_.Destination -ceq '/var/lib/postgresql/data'
    })
    Assert-Condition `
        ($devVolumes.Count -eq 1 -and
            $devVolumes[0].Name -ceq 'godswar-dev-postgres-data') `
        'Live development PostgreSQL is not using its isolated volume.'
    $devServer = Get-DockerContainer 'godswar-dev-server'
    $serverEnvironment = @($devServer.Config.Env)
    Assert-Condition `
        (-not ($serverEnvironment -match
            '^GODSWAR_POSTGRES_CONNECTION_STRING=')) `
        'Live development server exposes PostgreSQL credentials in its environment.'
    Assert-Condition `
        ($serverEnvironment -contains
            'GODSWAR_POSTGRES_CONNECTION_STRING_FILE=/run/secrets/postgres_connection_string') `
        'Live development server is not configured for the PostgreSQL file secret.'
    $livePostgresSecrets = @($devServer.Mounts | Where-Object {
        $_.Destination -ceq '/run/secrets/postgres_connection_string' -and
        -not $_.RW
    })
    Assert-Condition ($livePostgresSecrets.Count -eq 1) `
        'Live development PostgreSQL file secret is missing or writable.'
    Assert-Condition `
        ((Test-NetConnection 127.1.1.111 -Port 5998 `
            -InformationLevel Quiet -WarningAction SilentlyContinue)) `
        'Development login endpoint is unreachable.'
    Assert-Condition `
        ((Test-NetConnection 127.1.1.111 -Port 7000 `
            -InformationLevel Quiet -WarningAction SilentlyContinue)) `
        'Development game endpoint is unreachable.'
}

[pscustomobject]@{
    Status = if ($RequireLive) { 'live_isolated' } else { 'config_isolated' }
    Project = 'reborn-dev'
    LoginEndpoint = '127.1.1.111:5998'
    GameEndpoint = '127.1.1.111:7000'
    PostgreSqlEndpoint = '127.0.0.1:55432'
    Network = 'reborn_dev_runtime'
    Volume = 'godswar-dev-postgres-data'
}
