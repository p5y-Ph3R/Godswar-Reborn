[CmdletBinding()]
param(
    [string]$ConfigurationDirectory,
    [switch]$RefreshDatabaseFromMain,
    [switch]$SkipBuild,
    [switch]$MultiRealm
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevelopmentStack.Common.psm1') -Force

$configurationRoot = Get-DevelopmentConfigurationDirectory `
    $ConfigurationDirectory
$environmentPath = Get-DevelopmentEnvironmentPath $ConfigurationDirectory
if (-not (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
    & (Join-Path $PSScriptRoot 'NewDevelopmentStackConfiguration.ps1') `
        -OutputDirectory $configurationRoot | Out-Host
}
else {
    $connectionSecretPath = $null
    try {
        $connectionSecretPath = Get-DotEnvValue `
            $environmentPath `
            'GODSWAR_DEV_POSTGRES_CONNECTION_STRING_FILE'
    }
    catch {
        $connectionSecretPath = $null
    }
    if ([string]::IsNullOrWhiteSpace($connectionSecretPath) -or
        -not (Test-Path -LiteralPath $connectionSecretPath -PathType Leaf)) {
        & (Join-Path $PSScriptRoot 'NewDevelopmentStackConfiguration.ps1') `
            -OutputDirectory $configurationRoot `
            -UpgradeExisting | Out-Host
    }
}

$repositoryRoot = Get-DevelopmentRepositoryRoot
$head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cnotmatch '^[0-9a-f]{40}$') {
    throw 'Could not determine the development source revision.'
}
$dirty = & git -C $repositoryRoot status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw 'Could not determine development worktree state.'
}
$sourceRevision = if ([string]::IsNullOrWhiteSpace(($dirty -join "`n"))) {
    $head
} else {
    "$head-dirty"
}

$savedRevision = $env:GODSWAR_DEV_SOURCE_REVISION
$env:GODSWAR_DEV_SOURCE_REVISION = $sourceRevision
$mainBefore = $null
$mainGuardVerified = $false
try {
    $mainBefore = Get-MainObservationGuard
    $isolationParameters = @{
        ConfigurationDirectory = $configurationRoot
    }
    if ($MultiRealm) {
        $isolationParameters.MultiRealm = $true
    }
    & (Join-Path $PSScriptRoot 'TestDevelopmentStackIsolation.ps1') `
        @isolationParameters | Out-Host

    $compose = Get-DevelopmentComposeArguments $environmentPath
    & docker @($compose + @(
        'up', '--detach', '--wait', '--wait-timeout', '120',
        'postgres', 'redis-coordination')) | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Development PostgreSQL/Redis startup failed.'
    }

    $cloneParameters = @{
        ConfigurationDirectory = $configurationRoot
    }
    if ($RefreshDatabaseFromMain) {
        $cloneParameters.AllowDevelopmentDataReplacement = $true
    }
    $databaseResult = & (
        Join-Path $PSScriptRoot 'CopyMainDatabaseToDevelopment.ps1'
    ) @cloneParameters
    $databaseResult | Out-Host

    $serverArguments = @(
        'up', '--detach', '--wait', '--wait-timeout', '300', '--no-deps'
    )
    if ($SkipBuild) {
        $serverArguments += @('--no-build', '--pull', 'never')
    }
    else {
        $serverArguments += '--build'
    }
    $serverArguments += 'server'
    & docker @($compose + $serverArguments) | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Development game-server startup failed.'
    }

    $realmCatalog = $null
    $enabledRealmCount = 1
    if ($MultiRealm) {
        & docker @($compose + @(
            '--profile', 'multi-realm',
            'up', '--detach', '--wait', '--wait-timeout', '300',
            '--no-deps', '--no-build', '--pull', 'never',
            'server-dwargon')) | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw 'Development Dwargon game-server startup failed.'
        }

        $realmCatalog = & (
            Join-Path $PSScriptRoot 'EnableLocalDevelopmentMultiRealm.ps1'
        ) -ConfigurationDirectory $configurationRoot -AllowMutation
        $realmCatalog | Out-Host
        $enabledRealmCount = @($realmCatalog.realms).Count
    }
    else {
        $enabledRealmCountRaw = & docker exec godswar-dev-postgres `
            psql -U godswar -d godswar -Atqc `
            'SELECT count(*) FROM server WHERE enabled;'
        if ($LASTEXITCODE -ne 0 -or
            $enabledRealmCountRaw -notmatch '^\s*\d+\s*$') {
            throw 'Could not verify the enabled development realm count.'
        }
        $enabledRealmCount = [int]$enabledRealmCountRaw.Trim()
        $dwargonRunning = & docker inspect `
            --format '{{.State.Running}}' `
            godswar-dev-dwargon-openworld-01 2>$null
        $dwargonIsRunning =
            $LASTEXITCODE -eq 0 -and
            $dwargonRunning.Trim() -ceq 'true'
        if ($enabledRealmCount -ne 1 -or $dwargonIsRunning) {
            throw (
                'The development stack is already multi-realm. ' +
                'Rerun with -MultiRealm so Dwargon is converged and ' +
                'health-checked instead of silently leaving mixed state.')
        }
    }

    $liveParameters = @{
        ConfigurationDirectory = $configurationRoot
        RequireLive = $true
    }
    if ($MultiRealm) {
        $liveParameters.MultiRealm = $true
    }
    $isolation = & (
        Join-Path $PSScriptRoot 'TestDevelopmentStackIsolation.ps1'
    ) @liveParameters
    $mainAfter = Assert-MainObservationGuardUnchanged $mainBefore
    $mainGuardVerified = $true

    [pscustomobject]@{
        Status = 'running_isolated'
        SourceRevision = $sourceRevision
        LoginEndpoint = '127.1.1.111:5998'
        GameEndpoint = '127.1.1.111:7000'
        DwargonLoginEndpoint = if ($MultiRealm) {
            '127.1.1.112:5998'
        } else { $null }
        DwargonGameEndpoint = if ($MultiRealm) {
            '127.1.1.112:7000'
        } else { $null }
        EnabledRealms = $enabledRealmCount
        PostgreSqlEndpoint = '127.0.0.1:55432'
        DatabaseStatus = [string]$databaseResult.Status
        B20HStatus = if ($null -eq $mainAfter.ObservationStatus) {
            'not_active'
        } else {
            [string]$mainAfter.ObservationStatus.CurrentStatus
        }
        IsolationStatus = [string]$isolation.Status
        ClientLauncher = 'C:\Godswar Origin\Launch.exe'
    }
}
finally {
    try {
        if ($null -ne $mainBefore -and -not $mainGuardVerified) {
            Assert-MainObservationGuardUnchanged $mainBefore | Out-Null
        }
    }
    finally {
        $env:GODSWAR_DEV_SOURCE_REVISION = $savedRevision
    }
}
