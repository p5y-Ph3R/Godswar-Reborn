[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostServerValidation.psm1'
) -Force

$projectRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$server = Join-Path $projectRoot (
    'src\Godswar.Server\bin\Release\net10.0\Godswar.Server.dll')
if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
    throw (
        'Build the current Release server before this suite: ' +
        'dotnet build tests\Godswar.Server.ProtocolChecks\' +
        'Godswar.Server.ProtocolChecks.csproj -c Release')
}
$releaseTime = (Get-Item -LiteralPath $server).LastWriteTimeUtc
$sourceFiles = @(
    Get-ChildItem (Join-Path $projectRoot 'src\Godswar.Server') `
        -Recurse -File |
        Where-Object {
            $_.Extension -in @('.cs', '.csproj') -and
            $_.FullName -notmatch '\\(bin|obj)\\'
        }
)
if (@($sourceFiles | Where-Object {
        $_.LastWriteTimeUtc -gt $releaseTime
    }).Count -ne 0) {
    throw 'The Release validation assembly is older than current source.'
}

function Assert-DatabaseRejected {
    param(
        [Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)][string]$ExpectedName
    )

    $accepted = $true
    try {
        Read-RebornAcceptanceDatabaseScope `
            $ConnectionString $ExpectedName $server | Out-Null
    }
    catch {
        $accepted = $false
    }
    if ($accepted) {
        throw 'Unsafe controlled-host database scope was accepted.'
    }
}

$database = 'godswar_secure_acceptance_20260726_141154'
$valid = Read-RebornAcceptanceDatabaseScope (
    "Host=127.0.0.1;Port=5432;Database=$database;" +
    'Username=test;Password=not-a-real-secret;Pooling=true'
) $database $server
if ($valid.DatabaseName -cne $database -or
    -not $valid.HostIsLoopback) {
    throw 'Literal-loopback acceptance scope did not round-trip.'
}
$ipv6 = Read-RebornAcceptanceDatabaseScope (
    "Host=::1;Database=$database;Username=test;Password=fake"
) $database $server
if (-not $ipv6.HostIsLoopback) {
    throw 'IPv6 loopback acceptance scope was rejected.'
}

foreach ($connection in @(
    'Host=127.0.0.1;Database=godswar;Username=test;Password=fake',
    (
        "Host=127.0.0.1;Server=192.0.2.1;Database=$database;" +
        'Username=test;Password=fake'
    ),
    (
        "Host=127.0.0.1;Database=$database;DB=godswar;" +
        'Username=test;Password=fake'
    ),
    (
        "Host=127.0.0.1;Host=192.0.2.1;Database=$database;" +
        'Username=test;Password=fake'
    ),
    (
        "Host=127.0.0.1;Port=5433;Database=$database;" +
        'Username=test;Password=fake'
    ),
    "Host=localhost;Database=$database;Username=test;Password=fake",
    "Host=192.0.2.1;Database=$database;Username=test;Password=fake",
    "Database=$database;Username=test;Password=fake",
    'Host=127.0.0.1;Username=test;Password=fake',
    'not a connection string',
    (
        "Host=127.0.0.1;Database=$database;" +
        'Username=test;Password=' + ('x' * 4097)
    )
)) {
    Assert-DatabaseRejected $connection $database
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-server-validation-$([Guid]::NewGuid().ToString('N'))")
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$previousGodswar = @{}
$runtimeEnvironmentNames = @(
    'DOTNET_ENVIRONMENT',
    'ASPNETCORE_ENVIRONMENT'
)
$previousRuntimeEnvironment = @{}
try {
    $certificate = Join-Path $temporaryRoot 'fixture-server.pfx'
    [IO.File]::WriteAllBytes(
        $certificate,
        [byte[]](1, 2, 3, 4))
    $options = Join-Path $temporaryRoot 'appsettings.json'
    $configuration =
        Get-Content (Join-Path $projectRoot 'appsettings.json') -Raw |
        ConvertFrom-Json
    $configuration.secure.enabled = $true
    $configuration.secure.udp.enabled = $true
    $configuration.secure.udp.gameplayMovementEnabled = $true
    $configuration.secure.certificatePath = $certificate
    $configuration.authentication.maximumConcurrentKdfs = 4
    $configuration.authentication.allowRegistration = $false
    $configuration.authentication.allowPlaintextMigration = $true
    $configuration.storage.provider = 'postgres'
    $configuration.storage.postgresConnectionString =
        "Host=127.0.0.1;Database=$database"
    [IO.File]::WriteAllText(
        $options,
        ($configuration | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))

    $environment =
        [Environment]::GetEnvironmentVariables(
            [EnvironmentVariableTarget]::Process)
    foreach ($key in @($environment.Keys)) {
        if ($key -is [string] -and
            $key.StartsWith(
                'GODSWAR_',
                [StringComparison]::OrdinalIgnoreCase)) {
            $previousGodswar[$key] = [string]$environment[$key]
            [Environment]::SetEnvironmentVariable(
                $key,
                $null,
                [EnvironmentVariableTarget]::Process)
        }
    }
    foreach ($name in $runtimeEnvironmentNames) {
        $previousRuntimeEnvironment[$name] =
            [Environment]::GetEnvironmentVariable(
                $name,
                [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            $name,
            $null,
            [EnvironmentVariableTarget]::Process)
    }
    $issuedEnvironment = [ordered]@{
        GODSWAR_RUNTIME_PROFILE = 'LocalDevelopment'
        GODSWAR_SECURE_ENABLED = 'true'
        GODSWAR_SECURE_LOGIN_BIND_HOST = '127.0.0.1'
        GODSWAR_SECURE_LOGIN_PORT = '6599'
        GODSWAR_SECURE_LOGIN_DNS_HOST = 'login.reborn.test'
        GODSWAR_SECURE_GAME_BIND_HOST = '127.0.0.1'
        GODSWAR_SECURE_GAME_PORT = '7443'
        GODSWAR_SECURE_GAME_DNS_HOST = 'game.reborn.test'
        GODSWAR_SECURE_GAME_ROUTE_HOST = 'game.reborn.test'
        GODSWAR_SECURE_GAME_ROUTE_PORT = '7000'
        GODSWAR_SECURE_GAME_AUDIENCE = 'reborn-game'
        GODSWAR_SECURE_GAME_SERVER_ID = '100'
        GODSWAR_SECURE_GAME_PERMISSIONS = '1'
        GODSWAR_SECURE_UDP_ENABLED = 'true'
        GODSWAR_SECURE_UDP_GAMEPLAY_MOVEMENT_ENABLED = 'true'
        GODSWAR_SECURE_UDP_BIND_HOST = '127.0.0.1'
        GODSWAR_SECURE_UDP_PORT = '7444'
        GODSWAR_SECURE_CERTIFICATE_PATH = $certificate
        GODSWAR_SECURE_CERTIFICATE_PASSWORD =
            'portable-validation-only'
        GODSWAR_SECURE_ALLOWED_ORIGIN_SHA256 =
            '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
        GODSWAR_AUTH_ALLOW_REGISTRATION = 'false'
        GODSWAR_AUTH_ALLOW_PLAINTEXT_MIGRATION = 'true'
        GODSWAR_AUTH_MAXIMUM_CONCURRENT_KDFS = '4'
        GODSWAR_STORAGE_PROVIDER = 'postgres'
        GODSWAR_POSTGRES_CONNECTION_STRING =
            "Host=127.0.0.1;Database=$database"
        GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED = 'false'
    }
    foreach ($entry in $issuedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable(
            $entry.Key,
            $entry.Value,
            [EnvironmentVariableTarget]::Process)
    }

    if (-not (Test-RebornControlledHostServerOptions `
            $options $server $certificate $false)) {
        throw 'Portable exact server options were not accepted.'
    }
    [Environment]::SetEnvironmentVariable(
        'GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED',
        'true',
        [EnvironmentVariableTarget]::Process)
    foreach ($name in $runtimeEnvironmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            'Development',
            [EnvironmentVariableTarget]::Process)
    }
    if (-not (Test-RebornControlledHostServerOptions `
            $options $server $certificate $true)) {
        throw 'Portable acceptance-fault options were not accepted.'
    }

    $root = Join-Path $temporaryRoot 'fixture-root.cer'
    $receipt = Join-Path $temporaryRoot 'fixture-receipt.json'
    [IO.File]::WriteAllBytes($root, [byte[]](5, 6, 7, 8))
    [IO.File]::WriteAllText($receipt, '{}')
    $malformedAccepted = $true
    try {
        Test-RebornControlledHostCertificate `
            $certificate $root $receipt 'fixture-password' $server |
            Out-Null
    }
    catch {
        $malformedAccepted = $false
    }
    if ($malformedAccepted) {
        throw 'Malformed portable certificate fixture was accepted.'
    }
}
finally {
    $currentEnvironment =
        [Environment]::GetEnvironmentVariables(
            [EnvironmentVariableTarget]::Process)
    foreach ($key in @($currentEnvironment.Keys)) {
        if ($key -is [string] -and
            $key.StartsWith(
                'GODSWAR_',
                [StringComparison]::OrdinalIgnoreCase)) {
            [Environment]::SetEnvironmentVariable(
                $key,
                $null,
                [EnvironmentVariableTarget]::Process)
        }
    }
    foreach ($entry in $previousGodswar.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable(
            $entry.Key,
            $entry.Value,
            [EnvironmentVariableTarget]::Process)
    }
    foreach ($name in $runtimeEnvironmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $previousRuntimeEnvironment[$name],
            [EnvironmentVariableTarget]::Process)
    }
    $resolved = [IO.Path]::GetFullPath($temporaryRoot)
    $temporary = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith(
            $temporary + 'reborn-server-validation-',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unsafe validation fixture cleanup: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

Write-Host 'Controlled-host server validation checks passed.'
