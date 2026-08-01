Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-B20TopologyCondition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Get-B20ContainerEnvironmentValue {
    param(
        [Parameter(Mandatory)][object]$Container,
        [Parameter(Mandatory)][string]$Name
    )

    $prefix = "$Name="
    $matches = @($Container.Config.Env | Where-Object {
        $_.StartsWith($prefix, [StringComparison]::Ordinal)
    })
    if ($matches.Count -ne 1) {
        return $null
    }
    return $matches[0].Substring($prefix.Length)
}

function Get-B20ComposeNetworkIdentity {
    param([Parameter(Mandatory)][object]$Container)

    $networks = @($Container.NetworkSettings.Networks.PSObject.Properties)
    Assert-B20TopologyCondition ($networks.Count -eq 1) (
        'The container must have exactly one Compose network.')
    $network = $networks[0]
    Assert-B20TopologyCondition (
        [string]$network.Name -ceq 'reborn_default' -and
        [string]$network.Value.NetworkID -cmatch '^[0-9a-f]{64}$' -and
        [string]$network.Value.EndpointID -cmatch '^[0-9a-f]{64}$') (
        'The container Compose network identity is invalid.')
    return [pscustomobject]@{
        Name = [string]$network.Name
        Id = [string]$network.Value.NetworkID
    }
}

function Test-B20PostgresEnvironment {
    param(
        [Parameter(Mandatory)][object]$Postgres,
        [Parameter(Mandatory)][object]$RenderedPostgres
    )

    foreach ($name in @('POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD')) {
        if ((Get-B20ContainerEnvironmentValue $Postgres $name) -cne
            [string]$RenderedPostgres.environment.$name) {
            return $false
        }
    }
    return $true
}

function Test-B20RenderedRedisSecrets {
    param(
        [Parameter(Mandatory)][object]$RenderedCompose,
        [Parameter(Mandatory)][string]$RedisEnvironmentFile
    )

    try {
        $values = @{}
        foreach ($line in ((Get-Content `
                -LiteralPath $RedisEnvironmentFile -Raw) -split "`r?`n")) {
            if ([string]::IsNullOrWhiteSpace($line) -or
                $line.TrimStart().StartsWith('#')) {
                continue
            }
            $separator = $line.IndexOf('=')
            if ($separator -le 0) {
                return $false
            }
            $name = $line.Substring(0, $separator)
            if ($values.ContainsKey($name)) {
                return $false
            }
            $values[$name] = $line.Substring($separator + 1)
        }
        $bindings = [ordered]@{
            'redis-main-acl' = 'GODSWAR_REDIS_ACL_FILE'
            'redis-main-password' = 'GODSWAR_REDIS_PASSWORD_FILE'
            'redis-main-connection-string' =
                'GODSWAR_REDIS_CONNECTION_STRING_FILE_HOST'
        }
        if (@($RenderedCompose.secrets.PSObject.Properties).Count -ne
            $bindings.Count) {
            return $false
        }
        $comparison = if ($env:OS -eq 'Windows_NT') {
            [StringComparison]::OrdinalIgnoreCase
        } else {
            [StringComparison]::Ordinal
        }
        foreach ($secretName in $bindings.Keys) {
            $variable = $bindings[$secretName]
            $secret = $RenderedCompose.secrets.PSObject.Properties[
                $secretName]
            if ($null -eq $secret -or -not $values.ContainsKey($variable) -or
                $null -eq $secret.Value.PSObject.Properties['file'] -or
                -not [IO.Path]::GetFullPath(
                    [string]$secret.Value.file).Equals(
                    [IO.Path]::GetFullPath([string]$values[$variable]),
                    $comparison)) {
                return $false
            }
        }
        return $true
    } catch {
        return $false
    }
}

function Test-B20RenderedObservationTopology {
    param(
        [Parameter(Mandatory)][object]$RenderedCompose,
        [Parameter(Mandatory)][string]$RedisEnvironmentFile
    )

    $server = $RenderedCompose.services.server
    $redis = $RenderedCompose.services.'redis-coordination'
    return (
        $RenderedCompose.name -ceq 'reborn' -and
        (Test-B20RenderedRedisSecrets `
            $RenderedCompose $RedisEnvironmentFile) -and
        $server.environment.GODSWAR_COORDINATION_PROVIDER -ceq 'Redis' -and
        $server.environment.GODSWAR_COORDINATION_ENVIRONMENT -ceq
            'tempest-local' -and
        $server.environment.GODSWAR_RUNTIME_PROFILE -ceq
            'LocalDevelopment' -and
        $server.environment.GODSWAR_GAME_PUBLIC_HOST -ceq '127.1.1.110' -and
        $server.environment.GODSWAR_MONSTER_RUNTIME -ceq 'Ecs' -and
        $server.environment.GODSWAR_PLAYER_RUNTIME -ceq 'Ecs' -and
        $server.environment.GODSWAR_REDIS_CONNECTION_STRING_FILE -ceq
            '/run/secrets/redis_connection_string' -and
        $null -eq $server.environment.PSObject.Properties[
            'GODSWAR_REDIS_CONNECTION_STRING'] -and
        $redis.image -ceq (
            'redis:7.4.10-alpine@sha256:' +
            'e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2'))
}

function Test-B20ServerRedisTopology {
    param(
        [Parameter(Mandatory)][object]$Server,
        [Parameter(Mandatory)][object]$Coordination,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )

    try {
        $serverNetwork = Get-B20ComposeNetworkIdentity $Server
    } catch {
        return $false
    }
    $entrypoint = @($Server.Config.Entrypoint)
    $workerPath = [IO.Path]::GetFullPath((Join-Path `
        $RepositoryRoot 'deploy/local/redis-coordinated-worker.json'))
    $comparison = if ($env:OS -eq 'Windows_NT') {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    $workerMount = @($Server.Mounts | Where-Object {
        $_.Destination -ceq '/app/config/redis-coordinated-worker.json' -and
        $_.Type -ceq 'bind' -and -not $_.RW -and
        [IO.Path]::GetFullPath([string]$_.Source).Equals(
            $workerPath, $comparison)
    })
    $secretMount = @($Server.Mounts | Where-Object {
        $_.Destination -ceq '/run/secrets/redis_connection_string' -and
        -not $_.RW
    })
    return (
        ($entrypoint -join "`n") -ceq
            ("dotnet`nGodswar.Server.dll`n" +
                '/app/config/redis-coordinated-worker.json') -and
        (Get-B20ContainerEnvironmentValue `
            $Server 'GODSWAR_COORDINATION_PROVIDER') -ceq 'Redis' -and
        (Get-B20ContainerEnvironmentValue `
            $Server 'GODSWAR_COORDINATION_ENVIRONMENT') -ceq
                [string]$Coordination.environment -and
        (Get-B20ContainerEnvironmentValue `
            $Server 'GODSWAR_RUNTIME_PROFILE') -ceq
                [string]$Coordination.runtimeProfile -and
        (Get-B20ContainerEnvironmentValue `
            $Server 'GODSWAR_REDIS_CONNECTION_STRING_FILE') -ceq
                '/run/secrets/redis_connection_string' -and
        $null -eq (Get-B20ContainerEnvironmentValue `
            $Server 'GODSWAR_REDIS_CONNECTION_STRING') -and
        (Get-B20ContainerEnvironmentValue `
            $Server 'GODSWAR_WORLD_INSTANCE_SERVER_NODE_ID') -ceq
                [string]$Coordination.serverNodeId -and
        [string]$Server.Config.Labels.'com.docker.compose.project' -ceq
            [string]$Coordination.composeProject -and
        [string]$Server.Config.Labels.'com.docker.compose.service' -ceq
            'server' -and
        [string]$Server.Config.Labels.'com.docker.compose.config-hash' -ceq
            [string]$Coordination.serverComposeConfigHash -and
        [string]$Server.Config.Labels.'com.reborn.coordination.provider' -ceq
            'redis' -and
        [string]$Server.Config.Labels.'com.reborn.world.owner' -ceq
            [string]$Coordination.serverNodeId -and
        $serverNetwork.Name -ceq [string]$Coordination.networkName -and
        $serverNetwork.Id -ceq [string]$Coordination.networkId -and
        $workerMount.Count -eq 1 -and
        $secretMount.Count -eq 1)
}

function Test-B20RedisIdentity {
    param(
        [Parameter(Mandatory)][object]$Redis,
        [Parameter(Mandatory)][object]$Coordination,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )

    try {
        $redisNetwork = Get-B20ComposeNetworkIdentity $Redis
    } catch {
        return $false
    }
    $published = @($Redis.NetworkSettings.Ports.PSObject.Properties |
        Where-Object { $null -ne $_.Value })
    $comparison = if ($env:OS -eq 'Windows_NT') {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    $configPath = [IO.Path]::GetFullPath((Join-Path `
        $RepositoryRoot 'ops/redis/redis-coordination.local.conf'))
    $configMount = @($Redis.Mounts | Where-Object {
        $_.Type -ceq 'bind' -and -not $_.RW -and
        $_.Destination -ceq '/usr/local/etc/redis/redis.conf' -and
        [IO.Path]::GetFullPath([string]$_.Source).Equals(
            $configPath, $comparison)
    })
    $aclMount = @($Redis.Mounts | Where-Object {
        $_.Type -ceq 'bind' -and -not $_.RW -and
        $_.Destination -ceq '/run/secrets/redis_acl'
    })
    $passwordMount = @($Redis.Mounts | Where-Object {
        $_.Type -ceq 'bind' -and -not $_.RW -and
        $_.Destination -ceq '/run/secrets/redis_password'
    })
    $network = $Redis.NetworkSettings.Networks.reborn_default
    $aliases = @($network.Aliases)
    return (
        [string]$Redis.Id -ceq [string]$Coordination.redisContainerId -and
        [string]$Redis.Image -ceq [string]$Coordination.redisImageId -and
        [string]$Redis.Config.Image -ceq
            [string]$Coordination.redisImageReference -and
        [long]$Redis.RestartCount -eq
            [long]$Coordination.redisRestartCount -and
        ([DateTimeOffset]::Parse([string]$Redis.State.StartedAt).
            UtcDateTime.ToString('O')) -ceq
            [string]$Coordination.redisStartedAtUtc -and
        [string]$Redis.State.Health.Status -ceq 'healthy' -and
        [string]$Redis.Config.Labels.'com.docker.compose.project' -ceq
            [string]$Coordination.composeProject -and
        [string]$Redis.Config.Labels.'com.docker.compose.service' -ceq
            [string]$Coordination.redisComposeService -and
        [string]$Redis.Config.Labels.'com.docker.compose.config-hash' -ceq
            [string]$Coordination.redisComposeConfigHash -and
        (@($Redis.Config.Cmd) -join "`n") -ceq
            "redis-server`n/usr/local/etc/redis/redis.conf" -and
        (@($Redis.Config.Entrypoint) -join "`n") -ceq
            'docker-entrypoint.sh' -and
        [string]$Redis.Config.User -ceq 'redis' -and
        $Redis.HostConfig.ReadonlyRootfs -eq $true -and
        @($Redis.HostConfig.CapDrop).Count -eq 1 -and
        @($Redis.HostConfig.CapDrop) -ccontains 'ALL' -and
        @($Redis.HostConfig.SecurityOpt).Count -eq 1 -and
        @($Redis.HostConfig.SecurityOpt) -ccontains
            'no-new-privileges:true' -and
        [long]$Redis.HostConfig.PidsLimit -eq 128 -and
        [long]$Redis.HostConfig.Memory -eq 402653184 -and
        [long]$Redis.HostConfig.NanoCpus -eq 1000000000 -and
        [string]$Redis.HostConfig.RestartPolicy.Name -ceq 'unless-stopped' -and
        [string]$Redis.HostConfig.Tmpfs.'/data' -ceq
            'rw,noexec,nosuid,nodev,size=16m' -and
        [string]$Redis.HostConfig.Tmpfs.'/tmp' -ceq
            'rw,noexec,nosuid,nodev,size=16m' -and
        $configMount.Count -eq 1 -and
        $aclMount.Count -eq 1 -and
        $passwordMount.Count -eq 1 -and
        $redisNetwork.Name -ceq [string]$Coordination.networkName -and
        $redisNetwork.Id -ceq [string]$Coordination.networkId -and
        $aliases.Count -eq 2 -and
        $aliases -ccontains 'godswar-main-redis-coordination' -and
        $aliases -ccontains 'redis-coordination' -and
        [string]$network.NetworkID -cmatch '^[0-9a-f]{64}$' -and
        [string]$network.EndpointID -cmatch '^[0-9a-f]{64}$' -and
        $published.Count -eq 0)
}

function Get-B20ObservationInputHashes {
    param(
        [Parameter(Mandatory)][string]$BaseEnvironmentFile,
        [Parameter(Mandatory)][string]$RedisEnvironmentFile
    )

    $files = [ordered]@{
        baseEnvironment = [IO.Path]::GetFullPath($BaseEnvironmentFile)
        redisEnvironment = [IO.Path]::GetFullPath($RedisEnvironmentFile)
    }
    foreach ($path in $files.Values) {
        Assert-B20TopologyCondition (
            (Test-Path -LiteralPath $path -PathType Leaf) -and
            (Get-Item -LiteralPath $path).Length -le 64KB) (
            'A required Compose environment file is missing or oversized.')
    }
    $values = @{}
    foreach ($line in ((Get-Content `
            -LiteralPath $files.redisEnvironment -Raw) -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line) -or
            $line.TrimStart().StartsWith('#')) {
            continue
        }
        $separator = $line.IndexOf('=')
        Assert-B20TopologyCondition ($separator -gt 0) (
            'The Redis environment file contains a malformed line.')
        $name = $line.Substring(0, $separator)
        Assert-B20TopologyCondition (-not $values.ContainsKey($name)) (
            "The Redis environment file repeats '$name'.")
        $values[$name] = $line.Substring($separator + 1)
    }
    foreach ($mapping in @(
        [pscustomobject]@{
            Output = 'redisAcl'
            Variable = 'GODSWAR_REDIS_ACL_FILE'
            MaximumBytes = 16KB
        },
        [pscustomobject]@{
            Output = 'redisPassword'
            Variable = 'GODSWAR_REDIS_PASSWORD_FILE'
            MaximumBytes = 256
        },
        [pscustomobject]@{
            Output = 'redisConnectionString'
            Variable = 'GODSWAR_REDIS_CONNECTION_STRING_FILE_HOST'
            MaximumBytes = 4KB
        })) {
        Assert-B20TopologyCondition ($values.ContainsKey($mapping.Variable)) (
            "The Redis environment file is missing $($mapping.Variable).")
        $path = [IO.Path]::GetFullPath([string]$values[$mapping.Variable])
        Assert-B20TopologyCondition (
            (Test-Path -LiteralPath $path -PathType Leaf) -and
            (Get-Item -LiteralPath $path).Length -le $mapping.MaximumBytes) (
            "The generated Redis input '$($mapping.Output)' is invalid.")
        $files[$mapping.Output] = $path
    }
    $hashes = [ordered]@{}
    foreach ($name in $files.Keys) {
        $hashes[$name] = (
            Get-FileHash -LiteralPath $files[$name] -Algorithm SHA256).Hash
    }
    return ,$hashes
}

function Test-B20ObservationInputHashes {
    param(
        [Parameter(Mandatory)][object]$ExpectedHashes,
        [Parameter(Mandatory)][string]$BaseEnvironmentFile,
        [Parameter(Mandatory)][string]$RedisEnvironmentFile
    )

    try {
        $actual = Get-B20ObservationInputHashes `
            $BaseEnvironmentFile $RedisEnvironmentFile
    } catch {
        return $false
    }
    $expected = @($ExpectedHashes.PSObject.Properties)
    if ($expected.Count -ne @($actual.Keys).Count) {
        return $false
    }
    foreach ($name in $actual.Keys) {
        $property = $ExpectedHashes.PSObject.Properties[$name]
        if ($null -eq $property -or
            [string]$property.Value -cnotmatch '^[0-9A-F]{64}$' -or
            [string]$property.Value -cne [string]$actual[$name]) {
            return $false
        }
    }
    return $true
}

Export-ModuleMember -Function @(
    'Get-B20ContainerEnvironmentValue',
    'Get-B20ComposeNetworkIdentity',
    'Test-B20PostgresEnvironment',
    'Test-B20RenderedRedisSecrets',
    'Test-B20RenderedObservationTopology',
    'Test-B20ServerRedisTopology',
    'Test-B20RedisIdentity',
    'Get-B20ObservationInputHashes',
    'Test-B20ObservationInputHashes'
)
