[CmdletBinding()]
param(
    [string] $OutputDirectory = (
        Join-Path $PSScriptRoot '..\artifacts\redis-main-local'
    ),
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
$connectionStringPath =
    Join-Path $resolvedOutput 'redis.connection-string'
$environmentPath = Join-Path $resolvedOutput 'redis.local.env'
$targets = @(
    $aclPath
    $passwordPath
    $connectionStringPath
    $environmentPath
)

if (-not $Force) {
    $existing = @($targets | Where-Object {
        Test-Path -LiteralPath $_ -PathType Leaf
    })
    if ($existing.Count -ne 0) {
        throw (
            'Main Redis local configuration already exists. Use -Force ' +
            'only when intentionally rotating these exact generated files: ' +
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

$username = 'godswar_runtime'
$coordinationEnvironment = 'tempest-local'
$utf8 = [Text.UTF8Encoding]::new($false)
$newline = [Environment]::NewLine
$acl = @(
    'user default off'
    (
        "user $username reset on >$password " +
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

$connectionString = @(
    'redis-coordination:6379'
    "user=$username"
    "password=$password"
    'ssl=false'
    'abortConnect=true'
    'connectTimeout=1000'
    'asyncTimeout=250'
    'syncTimeout=250'
) -join ','

$portableAclPath = $aclPath.Replace('\', '/')
$portablePasswordPath = $passwordPath.Replace('\', '/')
$portableConnectionStringPath = $connectionStringPath.Replace('\', '/')
$environment = @(
    '# Generated paths for main local Redis coordination. Do not commit.'
    (
        'GODSWAR_REDIS_IMAGE=redis:7.4.10-alpine@sha256:' +
        'e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2'
    )
    "GODSWAR_REDIS_ACL_FILE=$portableAclPath"
    "GODSWAR_REDIS_PASSWORD_FILE=$portablePasswordPath"
    (
        'GODSWAR_REDIS_CONNECTION_STRING_FILE_HOST=' +
        $portableConnectionStringPath
    )
    'GODSWAR_COORDINATION_PROVIDER=Redis'
    "GODSWAR_COORDINATION_ENVIRONMENT=$coordinationEnvironment"
    'GODSWAR_REDIS_REQUIRE_TLS=false'
) -join $newline

[IO.File]::WriteAllText($aclPath, $acl + $newline, $utf8)
[IO.File]::WriteAllText($passwordPath, $password, $utf8)
[IO.File]::WriteAllText(
    $connectionStringPath,
    $connectionString,
    $utf8
)
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
    ConnectionStringFile = $connectionStringPath
    DockerHost = 'redis-coordination'
    Port = 6379
    Username = $username
    CoordinationEnvironment = $coordinationEnvironment
    ProductionReady = $false
}
