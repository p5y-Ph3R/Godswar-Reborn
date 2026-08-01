[CmdletBinding()]
param(
    [string] $OutputDirectory = (
        Join-Path $PSScriptRoot '..\artifacts\b17-redis-local'
    ),
    [ValidateRange(1024, 65535)]
    [int] $HostPort = 6380,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$isWindowsPlatform =
    $PSVersionTable.PSEdition -eq 'Desktop' -or
    $env:OS -eq 'Windows_NT'

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

$aclPath = Join-Path $resolvedOutput 'redis.acl'
$passwordPath = Join-Path $resolvedOutput 'redis.password'
$environmentPath = Join-Path $resolvedOutput 'redis.local.env'
$targets = @($aclPath, $passwordPath, $environmentPath)

if (-not $Force) {
    $existing = @($targets | Where-Object {
        Test-Path -LiteralPath $_ -PathType Leaf
    })
    if ($existing.Count -ne 0) {
        throw (
            'B17 Redis local configuration already exists. Use -Force only ' +
            'when intentionally rotating these exact generated files: ' +
            ($existing -join ', ')
        )
    }
}

$passwordBytes = [byte[]]::new(32)
$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $generator.GetBytes($passwordBytes)
    $password = -join (
        $passwordBytes |
            ForEach-Object { $_.ToString('x2') }
    )
}
finally {
    [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
    $generator.Dispose()
}

$username = 'godswar_b17'
$utf8 = [Text.UTF8Encoding]::new($false)
$newline = [Environment]::NewLine
$acl = @(
    'user default off'
    (
        "user $username reset on >$password " +
        '~godswar:b17-local:v1:* -@all ' +
        '+ping +echo +time +info +select ' +
        '+client|id +client|setinfo ' +
        '+client|setname ' +
        '+eval +evalsha +script|load +get +set +hget +hgetall +hset +hdel ' +
        '+hincrby +del +unlink +exists ' +
        '+pexpire ' +
        '+zadd +zcard +zrem +zrangebyscore +zremrangebyscore +zscore'
    )
) -join $newline

$portableAclPath = $aclPath.Replace('\', '/')
$portablePasswordPath = $passwordPath.Replace('\', '/')
$connectionString = @(
    "127.0.0.1:$HostPort"
    "user=$username"
    "password=$password"
    'ssl=false'
    'abortConnect=true'
    'connectTimeout=1000'
    'asyncTimeout=250'
    'syncTimeout=250'
) -join ','
$environment = @(
    '# Generated secret-bearing B17 local/CI configuration. Do not commit.'
    'GODSWAR_RUNTIME_PROFILE=LocalDevelopment'
    'GODSWAR_REDIS_IMAGE=redis:7.4.10-alpine'
    'GODSWAR_REDIS_HOST_BIND_ADDRESS=127.0.0.1'
    "GODSWAR_REDIS_HOST_PORT=$HostPort"
    "GODSWAR_REDIS_ACL_FILE=$portableAclPath"
    "GODSWAR_REDIS_PASSWORD_FILE=$portablePasswordPath"
    'GODSWAR_COORDINATION_PROVIDER=Redis'
    'GODSWAR_COORDINATION_ENVIRONMENT=b17-local'
    (
        'GODSWAR_REDIS_CONNECTION_STRING_ENVIRONMENT_VARIABLE=' +
        'GODSWAR_REDIS_CONNECTION_STRING'
    )
    "GODSWAR_REDIS_CONNECTION_STRING=$connectionString"
    'GODSWAR_REDIS_REQUIRE_TLS=false'
    'GODSWAR_REDIS_DATABASE=0'
    'GODSWAR_COORDINATION_CAPACITY=4096'
    'GODSWAR_REDIS_MAXIMUM_CONCURRENT_OPERATIONS=128'
    'GODSWAR_REDIS_QUEUE_ADMISSION_TIMEOUT_MILLISECONDS=25'
    'GODSWAR_REDIS_OPERATION_TIMEOUT_MILLISECONDS=250'
    'GODSWAR_REDIS_CONNECT_TIMEOUT_MILLISECONDS=1000'
    'GODSWAR_REDIS_CIRCUIT_FAILURE_THRESHOLD=5'
    'GODSWAR_REDIS_CIRCUIT_OPEN_MILLISECONDS=5000'
    'GODSWAR_COORDINATION_SERVER_HEARTBEAT_SECONDS=5'
    'GODSWAR_COORDINATION_SERVER_TTL_SECONDS=20'
    'GODSWAR_COORDINATION_PLAYER_LEASE_RENEWAL_SECONDS=10'
    'GODSWAR_COORDINATION_PLAYER_LEASE_TTL_SECONDS=30'
) -join $newline

[IO.File]::WriteAllText($aclPath, $acl + $newline, $utf8)
[IO.File]::WriteAllText($passwordPath, $password, $utf8)
[IO.File]::WriteAllText(
    $environmentPath,
    $environment + $newline,
    $utf8
)

if ($isWindowsPlatform) {
    $identity =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    foreach ($path in $targets) {
        $aclResult = & icacls.exe `
            $path `
            '/inheritance:r' `
            '/grant:r' `
            "*$identity`:(F)" 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw (
                "Could not restrict generated Redis file '$path': " +
                ($aclResult -join ' ')
            )
        }
    }
}
else {
    $ownerReadWrite =
        [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite
    foreach ($path in $targets) {
        [IO.File]::SetUnixFileMode($path, $ownerReadWrite)
    }
}

[pscustomobject]@{
    OutputDirectory = $resolvedOutput
    EnvironmentFile = $environmentPath
    AclFile = $aclPath
    PasswordFile = $passwordPath
    Host = '127.0.0.1'
    Port = $HostPort
    Username = $username
    ConnectionStringVariable = 'GODSWAR_REDIS_CONNECTION_STRING'
    ProductionReady = $false
}
