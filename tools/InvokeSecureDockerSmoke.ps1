[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $RootCertificatePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$baseEnvironmentPath = Join-Path $repositoryRoot '.env'
$secureEnvironmentPath = Join-Path $repositoryRoot '.env.secure.local'
$projectPath = Join-Path `
    $repositoryRoot `
    'tools\Godswar.Server.SecureSmoke\Godswar.Server.SecureSmoke.csproj'

function Read-RebornEnvironmentFile {
    param(
        [Parameter(Mandatory)]
        [string] $LiteralPath
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Required environment file was not found: $LiteralPath"
    }

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $LiteralPath) {
        if ([string]::IsNullOrWhiteSpace($line) -or
            $line.TrimStart().StartsWith('#')) {
            continue
        }
        if ($line -notmatch '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            throw "Environment file contains an unsupported line: $LiteralPath"
        }

        $name = $matches[1]
        if ($values.ContainsKey($name)) {
            throw "Environment file contains a duplicate key: $name"
        }
        $values[$name] = $matches[2].Trim()
    }
    return $values
}

$resolvedRoot = (
    Resolve-Path -LiteralPath $RootCertificatePath -ErrorAction Stop
).Path
$baseValues = Read-RebornEnvironmentFile -LiteralPath $baseEnvironmentPath
$secureValues = Read-RebornEnvironmentFile `
    -LiteralPath $secureEnvironmentPath

$database = $secureValues['GODSWAR_SECURE_POSTGRES_DB']
if ($database -cne 'godswar_secure_dev') {
    throw 'The secure Docker smoke may modify only godswar_secure_dev.'
}

$port = $baseValues['POSTGRES_PORT']
$username = $baseValues['POSTGRES_USER']
$password = $baseValues['POSTGRES_PASSWORD']
if ([string]::IsNullOrWhiteSpace($port) -or
    [string]::IsNullOrWhiteSpace($username) -or
    [string]::IsNullOrEmpty($password)) {
    throw 'The local PostgreSQL smoke configuration is incomplete.'
}

$rootVariable = 'GODSWAR_SECURE_SMOKE_ROOT_CERTIFICATE_PATH'
$postgresVariable =
    'GODSWAR_SECURE_SMOKE_POSTGRES_CONNECTION_STRING'
$priorRoot = [Environment]::GetEnvironmentVariable(
    $rootVariable,
    [EnvironmentVariableTarget]::Process)
$priorPostgres = [Environment]::GetEnvironmentVariable(
    $postgresVariable,
    [EnvironmentVariableTarget]::Process)

try {
    [Environment]::SetEnvironmentVariable(
        $rootVariable,
        $resolvedRoot,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $postgresVariable,
        (
            "Host=127.0.0.1;Port=$port;Database=$database;" +
            "Username=$username;Password=$password;Pooling=true"
        ),
        [EnvironmentVariableTarget]::Process)

    & dotnet run `
        --project $projectPath `
        --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Secure Docker smoke failed with exit code $LASTEXITCODE."
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        $rootVariable,
        $priorRoot,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $postgresVariable,
        $priorPostgres,
        [EnvironmentVariableTarget]::Process)
    $password = $null
}
