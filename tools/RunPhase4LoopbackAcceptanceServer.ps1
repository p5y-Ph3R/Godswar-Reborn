[CmdletBinding()]
param(
    [ValidateSet('Baseline', 'Fallback', 'Soak')]
    [string]$EvidenceProfile = 'Baseline',

    [string]$SecureEnvironmentPath = (
        Join-Path $PSScriptRoot '..\.env.secure.local'),

    [switch]$AllowLoopbackAcceptance
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($moduleName in @(
    'Phase4SecureDockerClientCampaign.psm1',
    'Phase4SecureDockerClientRuntime.psm1',
    'Phase4LoopbackAcceptanceProfile.psm1',
    'SecureNetworkActivationState.psm1',
    'ControlledHostManagedRelease.psm1',
    'ControlledHostPrivacyEvidence.psm1',
    'ControlledHostServerLauncherDependencies.psm1'
)) {
    Import-Module (Join-Path $PSScriptRoot $moduleName) -Force
}

$pins = Get-RebornPhase4SecureDockerPins
$activationModulePath =
    Join-Path $PSScriptRoot 'SecureNetworkActivationState.psm1'
$profilePolicy =
    Get-RebornPhase4AcceptanceProfilePolicy $EvidenceProfile
$EvidenceProfile = $profilePolicy.EvidenceProfile
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$managedRoot = Join-Path $repositoryRoot (
    'src\Godswar.Server\bin\Release\net10.0')
$serverAssembly = Join-Path $managedRoot 'Godswar.Server.dll'
$optionsPath = Join-Path $repositoryRoot 'appsettings.json'
$evidenceDirectory = Join-Path $repositoryRoot (
    'artifacts\controlled-host-acceptance\' +
    '20260727-011921\server-evidence')
$secureTcpPorts = @(6599, 7443)
$secureUdpPort = 7444
$rawPorts = @(5998, 5999, 7000)

function Assert-OrdinaryUser {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($identity.User.Value -ceq 'S-1-5-18' -or
        $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The Phase 4 loopback server requires an ordinary user token.'
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$LiteralPath)

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Required Phase 4 file is absent: $LiteralPath"
    }
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

function Get-Phase4LoopbackActivationState {
    # The launcher-dependency aggregate force-reloads its nested activation
    # module. Re-import here so this script never relies on a stale global
    # export after dependency composition.
    Import-Module $activationModulePath -Force
    return Get-RebornActivationState -Provider Hklm
}

function Read-EnvironmentFile {
    param([Parameter(Mandatory)][string]$LiteralPath)

    $resolved = [IO.Path]::GetFullPath($LiteralPath)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw 'The untracked secure-Docker environment file is absent.'
    }
    if ((Get-Item -LiteralPath $resolved).Length -gt 16KB) {
        throw 'The secure-Docker environment file exceeds its fixed bound.'
    }
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $resolved) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) {
            continue
        }
        $separator = $trimmed.IndexOf('=')
        if ($separator -le 0) {
            throw 'The secure-Docker environment file is malformed.'
        }
        $name = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1).Trim()
        if ($value.Length -ge 2 -and
            (($value[0] -eq '"' -and $value[-1] -eq '"') -or
             ($value[0] -eq "'" -and $value[-1] -eq "'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        if ($values.ContainsKey($name)) {
            throw "Duplicate secure-Docker environment key: $name"
        }
        $values[$name] = $value
    }
    return $values
}

function Get-RequiredEnvironmentValue {
    param(
        [Parameter(Mandatory)][hashtable]$Values,
        [Parameter(Mandatory)][string]$Name
    )

    if (-not $Values.ContainsKey($Name) -or
        [string]::IsNullOrWhiteSpace([string]$Values[$Name])) {
        throw "The secure-Docker environment lacks $Name."
    }
    return [string]$Values[$Name]
}

function Assert-ClientActivation {
    Assert-RebornPhase4PinnedInputs $pins | Out-Null
    $root = Get-RebornPhase4RootStatus $pins
    $activation = Get-Phase4LoopbackActivationState
    if ($root.State -cne 'InstalledExact' -or
        -not $activation.Complete -or
        [UInt64]$activation.Mode -ne 1 -or
        [UInt64]$activation.Environment -ne
            $pins.ActivationEnvironment -or
        [UInt64]$activation.SequenceFloor -ne
            $pins.ManifestSequence) {
        throw 'The Phase 4 client activation is not exact.'
    }
    $clientOrigin = Join-Path $pins.ClientRoot 'Origin.exe'
    $clientNet = Join-Path $pins.ClientRoot 'Net.dll'
    $clientLegacy = Join-Path $pins.ClientRoot 'NetLegacy.dll'
    $clientManifest = Join-Path $pins.ClientRoot 'RebornNetwork.gwem'
    if ((Get-Sha256 $clientOrigin) -cne $pins.OriginSha256 -or
        (Get-Sha256 $clientNet) -cne $pins.CandidateSha256 -or
        (Get-Sha256 $clientLegacy) -cne $pins.StockNetSha256 -or
        (Get-Sha256 $clientManifest) -cne $pins.ManifestSha256) {
        throw 'The installed Phase 4 client file set is not exact.'
    }
    foreach ($dnsName in @('login.reborn.test', 'game.reborn.test')) {
        $addresses = @([Net.Dns]::GetHostAddresses($dnsName))
        if ($addresses.Count -eq 0 -or
            @($addresses | Where-Object {
                -not [Net.IPAddress]::IsLoopback($_)
            }).Count -ne 0) {
            throw "$dnsName is not exact loopback."
        }
    }
}

function Assert-ContainerBoundary {
    $serverRunning = (& docker inspect -f '{{.State.Running}}' `
        $pins.ServerContainer 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $serverRunning -cne 'false') {
        throw 'The secure-Docker server must be stopped for loopback handoff.'
    }
    $postgresRunning = (& docker inspect -f '{{.State.Running}}' `
        $pins.PostgresContainer 2>$null).Trim()
    $postgresHealth = (& docker inspect -f '{{.State.Health.Status}}' `
        $pins.PostgresContainer 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $postgresRunning -cne 'true' -or
        $postgresHealth -cne 'healthy') {
        throw 'The Docker PostgreSQL boundary is not healthy.'
    }

    $tcp = @(Get-NetTCPConnection -State Listen -ErrorAction Stop)
    if (@($tcp | Where-Object {
            $secureTcpPorts -contains $_.LocalPort -or
            $rawPorts -contains $_.LocalPort
        }).Count -ne 0 -or
        @(Get-NetUDPEndpoint -ErrorAction Stop | Where-Object {
            $_.LocalPort -eq $secureUdpPort
        }).Count -ne 0) {
        throw 'A game listener remains before the loopback handoff.'
    }
    $postgresListeners = @(
        $tcp | Where-Object { $_.LocalPort -eq 5432 })
    if ($postgresListeners.Count -ne 1 -or
        $postgresListeners[0].LocalAddress -cne '127.0.0.1') {
        throw 'PostgreSQL is not exposed on exact loopback.'
    }
}

function Read-PostgresContainerEnvironment {
    $lines = @(
        & docker inspect --format `
            '{{range .Config.Env}}{{println .}}{{end}}' `
            $pins.PostgresContainer 2>$null)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -gt 128) {
        throw 'Docker PostgreSQL environment inspection failed.'
    }
    $values = @{}
    foreach ($line in $lines) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) {
            $values[$line.Substring(0, $separator)] =
                $line.Substring($separator + 1)
        }
    }
    return $values
}

function New-PostgresConnectionString {
    param([Parameter(Mandatory)][hashtable]$ContainerEnvironment)

    $user = Get-RequiredEnvironmentValue `
        $ContainerEnvironment 'POSTGRES_USER'
    $password = Get-RequiredEnvironmentValue `
        $ContainerEnvironment 'POSTGRES_PASSWORD'
    $npgsqlPath = Join-Path $managedRoot 'Npgsql.dll'
    [Reflection.Assembly]::LoadFrom($npgsqlPath) | Out-Null
    $builder = [Npgsql.NpgsqlConnectionStringBuilder]::new()
    $builder.Host = '127.0.0.1'
    $builder.Port = 5432
    $builder.Database = $pins.DockerDatabase
    $builder.Username = $user
    $builder.Password = $password
    $builder.Pooling = $true
    return $builder.ConnectionString
}

if (-not $AllowLoopbackAcceptance) {
    throw 'Explicit -AllowLoopbackAcceptance is required.'
}
Assert-OrdinaryUser
Assert-RebornControlledHostSafeProcessEnvironment | Out-Null
Assert-RebornControlledHostUnsetEnvironmentNames (
    Get-RebornPhase4AcceptanceRuntimeEnvironmentNames
) | Out-Null
Assert-ClientActivation
$campaignAuthority = Read-RebornPhase4CampaignReceipt -Pins $pins
$issuedUserSid =
    [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($null -eq $campaignAuthority -or
    $campaignAuthority.Record.state -cne 'InstalledExact' -or
    [string]$campaignAuthority.Record.issuedUserSid -cne
        $issuedUserSid) {
    throw 'The installed Phase 4 campaign authority is not exact.'
}
Assert-ContainerBoundary

$secureEnvironment = Read-EnvironmentFile $SecureEnvironmentPath
$certificatePath = [IO.Path]::GetFullPath(
    (Get-RequiredEnvironmentValue $secureEnvironment `
        'GODSWAR_SECURE_CERTIFICATE_HOST_PATH'))
$certificatePasswordPath = [IO.Path]::GetFullPath(
    (Get-RequiredEnvironmentValue $secureEnvironment `
        'GODSWAR_SECURE_CERTIFICATE_PASSWORD_HOST_PATH'))
$configuredDatabase = Get-RequiredEnvironmentValue `
    $secureEnvironment 'GODSWAR_SECURE_POSTGRES_DB'
if ((Get-Sha256 $certificatePath) -cne $pins.ServerPfxSha256 -or
    $configuredDatabase -cne $pins.DockerDatabase) {
    throw 'The secure-Docker certificate or database pin changed.'
}
if (-not (Test-Path -LiteralPath $certificatePasswordPath -PathType Leaf) -or
    (Get-Item -LiteralPath $certificatePasswordPath).Length -gt 4KB) {
    throw 'The certificate-password file is absent or oversized.'
}

$release = Get-RebornControlledHostManagedReleaseSet $managedRoot
$serverSha256 = Get-Sha256 $serverAssembly
$optionsSha256 = Get-Sha256 $optionsPath
$postgresEnvironment = Read-PostgresContainerEnvironment
$postgresConnection = New-PostgresConnectionString $postgresEnvironment
$evidencePath = New-RebornControlledHostEvidencePath $evidenceDirectory
$faultsEnabled = $profilePolicy.FaultsEnabled

$environment = [ordered]@{
    GODSWAR_STORAGE_PROVIDER = 'postgres'
    GODSWAR_POSTGRES_CONNECTION_STRING = $postgresConnection
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
    GODSWAR_SECURE_CERTIFICATE_PATH = $certificatePath
    GODSWAR_SECURE_CERTIFICATE_PASSWORD_FILE =
        $certificatePasswordPath
    GODSWAR_SECURE_ALLOWED_ORIGIN_SHA256 = $pins.OriginSha256
    GODSWAR_AUTH_ALLOW_REGISTRATION = 'false'
    GODSWAR_AUTH_ALLOW_PLAINTEXT_MIGRATION = 'true'
    GODSWAR_AUTH_MAXIMUM_CONCURRENT_KDFS = '4'
    GODSWAR_CONTROLLED_HOST_EVIDENCE_PATH = $evidencePath
}
foreach ($entry in (
    Get-RebornControlledHostDiagnosticsDisabledEnvironment
).GetEnumerator()) {
    $environment[$entry.Key] = $entry.Value
}
Set-RebornPhase4AcceptanceProfileEnvironment `
    $environment $EvidenceProfile
Assert-RebornControlledHostNoUnreviewedGodswarEnvironment `
    @($environment.Keys) | Out-Null

$previous = @{}
$applied = [Collections.Generic.List[string]]::new()
$timer = [Diagnostics.Stopwatch]::StartNew()
$exitCode = $null
$evidenceResult = $null
try {
    foreach ($entry in $environment.GetEnumerator()) {
        $previous[$entry.Key] = [Environment]::GetEnvironmentVariable(
            $entry.Key,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            $entry.Key,
            $entry.Value,
            [EnvironmentVariableTarget]::Process)
        $applied.Add($entry.Key)
    }

    & (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') `
        $serverAssembly $optionsPath
    $exitCode = $LASTEXITCODE
}
finally {
    $timer.Stop()
    foreach ($key in $applied) {
        [Environment]::SetEnvironmentVariable(
            $key,
            $previous[$key],
            [EnvironmentVariableTarget]::Process)
    }
    if (Test-Path -LiteralPath $evidencePath -PathType Leaf) {
        try {
            $evidenceResult =
                Assert-RebornControlledHostPrivacyEvidence `
                    -Path $evidencePath `
                    -Profile $EvidenceProfile `
                    -ObservedDuration $timer.Elapsed `
                    -RequireStopped
        }
        finally {
            Protect-RebornControlledHostPrivacyEvidence `
                $evidencePath | Out-Null
        }
    }
    $environment.Clear()
    $previous.Clear()
    $secureEnvironment.Clear()
    $postgresEnvironment.Clear()
    $postgresConnection = $null
}

if ($null -eq $exitCode -or $exitCode -ne 0) {
    $numericCode = if ($null -eq $exitCode) { -1 } else { [int]$exitCode }
    throw "The Phase 4 loopback server exited with code $numericCode."
}
if ($null -eq $evidenceResult) {
    throw 'The Phase 4 loopback server did not produce validated evidence.'
}

$evidenceSha256 = Get-Sha256 $evidenceResult.Path
$profileRecord = New-RebornPhase4AcceptanceProfileRecord `
    -CampaignId $campaignAuthority.Record.campaignId `
    -IssuedUserSid $campaignAuthority.Record.issuedUserSid `
    -EvidenceProfile $EvidenceProfile `
    -ObservedDurationSeconds $evidenceResult.ObservedDurationSeconds `
    -EvidencePath $evidenceResult.Path `
    -EvidenceSha256 $evidenceSha256 `
    -EvidenceBytes $evidenceResult.Bytes `
    -EvidenceEvents $evidenceResult.Events `
    -ServerSha256 $serverSha256 `
    -ManagedReleaseSetSha256 $release.SetSha256 `
    -OptionsSha256 $optionsSha256 `
    -CandidateSha256 $pins.CandidateSha256 `
    -ManifestSha256 $pins.ManifestSha256 `
    -DatabaseName $pins.DockerDatabase
$profileResult =
    Write-RebornPhase4AcceptanceProfileResult $profileRecord
if ($null -eq $profileResult -or
    (Get-Sha256 $profileResult.ProfileResultPath) -cne
        $profileResult.ProfileResultSha256 -or
    (Get-Content -LiteralPath `
        $profileResult.ProfileResultChecksumPath -Raw).Trim() -cne
        $profileResult.ProfileResultSha256) {
    throw 'The durable Phase 4 profile result is not exact.'
}

New-RebornPhase4LoopbackAcceptanceResult `
    -EvidenceProfile $EvidenceProfile `
    -EvidenceResult $evidenceResult `
    -ServerSha256 $serverSha256 `
    -ManagedReleaseSetSha256 $release.SetSha256 `
    -OptionsSha256 $optionsSha256 `
    -DatabaseName $pins.DockerDatabase `
    -SecureTcpPorts $secureTcpPorts `
    -SecureUdpPort $secureUdpPort `
    -EvidenceSha256 $evidenceSha256 `
    -ProfileResult $profileResult
