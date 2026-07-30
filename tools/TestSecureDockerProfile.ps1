[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Condition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$repositoryRoot =
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$baseCompose = Join-Path $repositoryRoot 'docker-compose.yml'
$secureCompose = Join-Path $repositoryRoot 'docker-compose.secure.yml'
$certificatePath =
    Join-Path ([IO.Path]::GetTempPath()) 'reborn-compose-certificate.pfx'
$passwordPath =
    Join-Path ([IO.Path]::GetTempPath()) 'reborn-compose-certificate-password'

$savedCertificate =
    [Environment]::GetEnvironmentVariable(
        'GODSWAR_SECURE_CERTIFICATE_HOST_PATH',
        'Process')
$savedPassword =
    [Environment]::GetEnvironmentVariable(
        'GODSWAR_SECURE_CERTIFICATE_PASSWORD_HOST_PATH',
        'Process')

try {
    $env:GODSWAR_SECURE_CERTIFICATE_HOST_PATH = $certificatePath
    $env:GODSWAR_SECURE_CERTIFICATE_PASSWORD_HOST_PATH = $passwordPath

    $baseJson =
        & docker compose -f $baseCompose config --format json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Base Compose render failed: $($baseJson -join [Environment]::NewLine)"
    }

    $secureJson =
        & docker compose `
            -f $baseCompose `
            -f $secureCompose `
            --profile secure `
            config `
            --format json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Secure Compose render failed: $($secureJson -join [Environment]::NewLine)"
    }

    $base = ($baseJson -join [Environment]::NewLine) | ConvertFrom-Json
    $secure =
        ($secureJson -join [Environment]::NewLine) | ConvertFrom-Json
    $baseServer = $base.services.server
    $postgres = $secure.services.postgres
    $server = $secure.services.server

    Assert-Condition `
        (@($baseServer.environment.PSObject.Properties.Name) -cnotcontains
            'GODSWAR_SECURE_ENABLED') `
        'The base Compose profile unexpectedly enables secure networking.'
    Assert-Condition `
        (@($server.profiles).Count -eq 1 -and
            $server.profiles[0] -ceq 'secure') `
        'The secure server must require only the secure profile.'
    Assert-Condition `
        ($server.container_name -ceq 'godswar-server') `
        'The override must replace the existing server container.'
    Assert-Condition `
        ($server.environment.GODSWAR_SECURE_ENABLED -ceq 'true' -and
            $server.environment.GODSWAR_RUNTIME_PROFILE -ceq
                'LocalDevelopment' -and
            $server.environment.GODSWAR_SECURE_UDP_ENABLED -ceq 'true' -and
            $server.environment.
                GODSWAR_SECURE_UDP_GAMEPLAY_MOVEMENT_ENABLED -ceq 'true') `
        'The local secure profile, TLS, protected UDP, and authoritative movement must be exact.'

    $containerAddress =
        [string]$server.networks.'secure-runtime'.ipv4_address
    $serverNetworkNames =
        @($server.networks.PSObject.Properties.Name)
    Assert-Condition `
        ($serverNetworkNames.Count -eq 1 -and
            $serverNetworkNames[0] -ceq 'secure-runtime') `
        'The secure server must use only the fixed secure-runtime network.'
    Assert-Condition `
        (@($postgres.networks.PSObject.Properties.Name) -ccontains
            'secure-runtime') `
        'PostgreSQL must share secure-runtime with the fixed-address server.'
    foreach ($name in @(
        'GODSWAR_SECURE_LOGIN_BIND_HOST',
        'GODSWAR_SECURE_GAME_BIND_HOST',
        'GODSWAR_SECURE_UDP_BIND_HOST'
    )) {
        Assert-Condition `
            ([string]$server.environment.$name -ceq $containerAddress) `
            "$name must equal the fixed secure-runtime address."
    }

    $expectedPorts = @(
        '127.0.0.1|6599|6599|tcp',
        '127.0.0.1|7443|7443|tcp',
        '127.0.0.1|7444|7444|udp'
    )
    $actualPorts = @(
        $server.ports | ForEach-Object {
            '{0}|{1}|{2}|{3}' -f
                $_.host_ip,
                $_.target,
                $_.published,
                $_.protocol
        }
    )
    Assert-Condition `
        (@(Compare-Object $expectedPorts $actualPorts).Count -eq 0) `
        'Secure Docker host ports must be the exact loopback TLS/UDP set.'
    Assert-Condition `
        (-not ($actualPorts -match '\|(5998|5999|7000)\|')) `
        'Raw legacy host ports must not be published by the secure override.'

    Assert-Condition `
        ($server.environment.GODSWAR_SECURE_CERTIFICATE_PATH -ceq
            '/run/secrets/reborn-secure-certificate') `
        'The server certificate must come from the read-only Compose secret.'
    Assert-Condition `
        ($server.environment.
            GODSWAR_SECURE_CERTIFICATE_PASSWORD_FILE -ceq
            '/run/secrets/reborn-secure-certificate-password') `
        'The certificate password must come from the file-backed secret.'
    Assert-Condition `
        (@($server.environment.PSObject.Properties.Name) -cnotcontains
            'GODSWAR_SECURE_CERTIFICATE_PASSWORD') `
        'The certificate password must not be exposed in container environment.'
    $secretModes = @{}
    foreach ($secret in @($server.secrets)) {
        $secretModes[[string]$secret.target] = [string]$secret.mode
    }
    Assert-Condition `
        ($secretModes['reborn-secure-certificate'] -ceq '0444' -and
            $secretModes['reborn-secure-certificate-password'] -ceq '0400') `
        'Certificate and password secret mounts must remain read-only.'
    Assert-Condition `
        ([string]$server.environment.GODSWAR_POSTGRES_CONNECTION_STRING -match
            'Database=godswar;') `
        'The secure profile must default to the durable godswar database.'

    $health = @($server.healthcheck.test)
    Assert-Condition `
        ($health.Count -eq 2 -and
            $health[0] -ceq 'CMD' -and
            $health[1] -ceq '/app/secure-healthcheck.sh') `
        'The secure container must use the bounded management readiness probe.'

    Write-Host 'Secure Docker Compose profile checks passed.'
}
finally {
    [Environment]::SetEnvironmentVariable(
        'GODSWAR_SECURE_CERTIFICATE_HOST_PATH',
        $savedCertificate,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'GODSWAR_SECURE_CERTIFICATE_PASSWORD_HOST_PATH',
        $savedPassword,
        'Process')
}
