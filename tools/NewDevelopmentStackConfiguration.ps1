[CmdletBinding()]
param(
    [string]$OutputDirectory = (
        Join-Path $PSScriptRoot '..\artifacts\development-stack'
    ),
    [switch]$Force,
    [switch]$UpgradeExisting
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevelopmentStack.Common.psm1') -Force

function New-RandomHexSecret {
    param([ValidateRange(16, 128)][int]$ByteCount = 32)

    $bytes = [byte[]]::new($ByteCount)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
        return -join ($bytes | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $generator.Dispose()
    }
}

function Write-Utf8File {
    param([string]$LiteralPath, [string]$Value)

    [IO.File]::WriteAllText(
        $LiteralPath,
        $Value,
        [Text.UTF8Encoding]::new($false))
}

$resolvedCandidate = [IO.Path]::GetFullPath($OutputDirectory)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($resolvedCandidate.TrimEnd('\', '/') -ceq
        $repositoryRoot.TrimEnd('\', '/')) {
    throw 'The repository root cannot be used as a secret output directory.'
}
$allowedEntries = @(
    'client-config-backups',
    'clone-receipts',
    'clone-work',
    'development.local.env',
    'postgres.connection-string',
    'postgres.password',
    'redis.acl',
    'redis.connection-string',
    'redis.password'
)
if (Test-Path -LiteralPath $resolvedCandidate -PathType Container) {
    $candidate = Get-Item -LiteralPath $resolvedCandidate -Force
    if (($candidate.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Development configuration directory cannot be a reparse point.'
    }
    $unexpectedEntries = @(Get-ChildItem -LiteralPath $resolvedCandidate -Force |
        Where-Object { $_.Name -cnotin $allowedEntries })
    if ($unexpectedEntries.Count -ne 0) {
        throw (
            'Development configuration directory contains unexpected entries: ' +
            (($unexpectedEntries.Name | Sort-Object) -join ', '))
    }
}
$resolvedOutput = Protect-DevelopmentPrivateDirectory $resolvedCandidate

$postgresPasswordPath = Join-Path $resolvedOutput 'postgres.password'
$postgresConnectionPath = Join-Path `
    $resolvedOutput 'postgres.connection-string'
$redisPasswordPath = Join-Path $resolvedOutput 'redis.password'
$redisAclPath = Join-Path $resolvedOutput 'redis.acl'
$redisConnectionPath = Join-Path $resolvedOutput 'redis.connection-string'
$environmentPath = Join-Path $resolvedOutput 'development.local.env'
$targets = @(
    $postgresPasswordPath,
    $postgresConnectionPath,
    $redisPasswordPath,
    $redisAclPath,
    $redisConnectionPath,
    $environmentPath
)
foreach ($target in $targets) {
    if (Test-Path -LiteralPath $target) {
        $targetItem = Get-Item -LiteralPath $target -Force
        if ($targetItem.PSIsContainer -or
            ($targetItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Development configuration target is not a regular file: $target"
        }
    }
}

if ($Force -and $UpgradeExisting) {
    throw '-Force and -UpgradeExisting are mutually exclusive.'
}
if ($UpgradeExisting) {
    $required = @(
        $postgresPasswordPath,
        $redisPasswordPath,
        $redisAclPath,
        $redisConnectionPath,
        $environmentPath
    )
    $missing = @($required | Where-Object {
        -not (Test-Path -LiteralPath $_ -PathType Leaf)
    })
    if ($missing.Count -ne 0) {
        throw (
            'Cannot upgrade incomplete development configuration: ' +
            ($missing -join ', '))
    }

    $postgresPassword = Read-DevelopmentSecretFile $postgresPasswordPath
    try {
        $postgresConnection =
            'Host=postgres;Port=5432;Database=godswar;' +
            "Username=godswar;Password=$postgresPassword;Pooling=true"
        Write-Utf8File $postgresConnectionPath $postgresConnection
        Protect-DevelopmentPrivateFile $postgresConnectionPath | Out-Null

        $portableConnection = $postgresConnectionPath.Replace('\', '/')
        $retainedLines = @(Get-Content -LiteralPath $environmentPath |
            Where-Object {
                $_ -cnotmatch '^GODSWAR_DEV_POSTGRES_PASSWORD=' -and
                $_ -cnotmatch
                    '^GODSWAR_DEV_POSTGRES_CONNECTION_STRING_FILE='
            })
        $retainedLines +=
            "GODSWAR_DEV_POSTGRES_CONNECTION_STRING_FILE=$portableConnection"
        $temporaryEnvironment = Join-Path $resolvedOutput (
            '.development.local.env.' + [Guid]::NewGuid().ToString('N'))
        $backupEnvironment = Join-Path $resolvedOutput (
            '.development.local.env.backup.' + [Guid]::NewGuid().ToString('N'))
        try {
            Write-Utf8File $temporaryEnvironment (
                ($retainedLines -join [Environment]::NewLine) +
                [Environment]::NewLine)
            Protect-DevelopmentPrivateFile $temporaryEnvironment | Out-Null
            [IO.File]::Replace(
                $temporaryEnvironment,
                $environmentPath,
                $backupEnvironment)
            Protect-DevelopmentPrivateFile $environmentPath | Out-Null
        }
        finally {
            Remove-Item -LiteralPath $temporaryEnvironment `
                -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $backupEnvironment `
                -Force -ErrorAction SilentlyContinue
        }
    }
    finally {
        $postgresPassword = $null
    }

    [pscustomobject]@{
        Status = 'upgraded_existing'
        OutputDirectory = $resolvedOutput
        EnvironmentFile = $environmentPath
        PostgresConnectionStringFile = $postgresConnectionPath
        CredentialsRotated = $false
    }
    return
}

$existing = @($targets | Where-Object {
    Test-Path -LiteralPath $_ -PathType Leaf
})
if ($existing.Count -ne 0 -and -not $Force) {
    throw (
        'Development-stack configuration already exists. Use -Force only ' +
        'while all godswar-dev-* containers are stopped: ' +
        ($existing -join ', '))
}
if ($Force) {
    $containers = @(& docker ps --all `
        --filter 'name=^/godswar-dev-' --format '{{.Names}}')
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not verify development-container state.'
    }
    if ($containers.Count -ne 0) {
        throw (
            'Refusing to rotate development secrets while dev containers ' +
            'exist: ' + ($containers -join ', '))
    }
    $volume = @(& docker volume ls `
        --filter 'name=^godswar-dev-postgres-data$' --format '{{.Name}}')
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not verify the development-volume state.'
    }
    if ($volume.Count -ne 0) {
        throw (
            'Refusing to rotate the PostgreSQL credential while the ' +
            'development data volume exists.')
    }
}

$postgresPassword = New-RandomHexSecret
$redisPassword = New-RandomHexSecret
$username = 'godswar_runtime'
$coordinationEnvironment = 'tempest-dev'
$newline = [Environment]::NewLine

$postgresConnection =
    'Host=postgres;Port=5432;Database=godswar;' +
    "Username=godswar;Password=$postgresPassword;Pooling=true"

$redisAcl = @(
    'user default off'
    (
        "user $username reset on >$redisPassword " +
        "~godswar:$coordinationEnvironment`:v1:* -@all " +
        '+ping +echo +time +info +select ' +
        '+client|id +client|setinfo +client|setname ' +
        '+eval +evalsha +script|load +get +set ' +
        '+hget +hmget +hgetall +hset +hdel +hincrby ' +
        '+del +unlink +exists +pexpire +pttl ' +
        '+zadd +zcard +zrem +zrangebyscore ' +
        '+zremrangebyscore +zscore'
    )
) -join $newline

$redisConnection = @(
    'redis-coordination:6379'
    "user=$username"
    "password=$redisPassword"
    'ssl=false'
    'abortConnect=true'
    'connectTimeout=1000'
    'asyncTimeout=250'
    'syncTimeout=250'
) -join ','

$portable = @{}
foreach ($path in $targets) {
    $portable[$path] = $path.Replace('\', '/')
}
$environment = @(
    '# Generated isolated development configuration. Do not commit.'
    "GODSWAR_DEV_POSTGRES_PASSWORD_FILE=$($portable[$postgresPasswordPath])"
    (
        'GODSWAR_DEV_POSTGRES_CONNECTION_STRING_FILE=' +
        $portable[$postgresConnectionPath]
    )
    "GODSWAR_DEV_REDIS_ACL_FILE=$($portable[$redisAclPath])"
    "GODSWAR_DEV_REDIS_PASSWORD_FILE=$($portable[$redisPasswordPath])"
    (
        'GODSWAR_DEV_REDIS_CONNECTION_STRING_FILE=' +
        $portable[$redisConnectionPath]
    )
    'GODSWAR_DEV_POSTGRES_HOST_PORT=55432'
    'GODSWAR_DEV_LOGIN_HOST_PORT=5998'
    'GODSWAR_DEV_GAME_HOST_PORT=7000'
    'GODSWAR_DEV_DEVELOPER_ACCOUNT_IDS=3,7,13,347'
) -join $newline

Write-Utf8File $postgresPasswordPath $postgresPassword
Write-Utf8File $postgresConnectionPath $postgresConnection
Write-Utf8File $redisPasswordPath $redisPassword
Write-Utf8File $redisAclPath ($redisAcl + $newline)
Write-Utf8File $redisConnectionPath $redisConnection
Write-Utf8File $environmentPath ($environment + $newline)

foreach ($path in $targets) {
    Protect-DevelopmentPrivateFile $path | Out-Null
}

[pscustomobject]@{
    Status = 'created'
    OutputDirectory = $resolvedOutput
    EnvironmentFile = $environmentPath
    PostgresConnectionStringFile = $postgresConnectionPath
    CoordinationEnvironment = $coordinationEnvironment
    PostgresHostPort = 55432
    LoginEndpoint = '127.1.1.111:5998'
    GameEndpoint = '127.1.1.111:7000'
    ProductionReady = $false
}
