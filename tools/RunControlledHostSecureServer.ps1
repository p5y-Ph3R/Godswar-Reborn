[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerAssembly,
    [Parameter(Mandatory)][string]$OptionsPath,
    [Parameter(Mandatory)][string]$CertificatePath,
    [Parameter(Mandatory)][string]$RootCertificatePath,
    [Parameter(Mandatory)][string]$TrustReceiptPath,
    [Parameter(Mandatory)][string]$ManifestTrustPath,
    [Parameter(Mandatory)][string]$ManifestKeyReceiptPath,
    [Parameter(Mandatory)][string]$NativeChecksPath,
    [Parameter(Mandatory)][string]$CertificatePasswordSecretPath,
    [Parameter(Mandatory)][string]$PostgresConnectionSecretPath,
    [Parameter(Mandatory)][string]$ClientInventoryReceiptPath,
    [Parameter(Mandatory)][string]$EvidenceDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedServerSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedManagedReleaseSetSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedOptionsSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCandidateSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedManifestSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCertificateSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCertificateSecretSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedPostgresSecretSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedRootCertificateSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedTrustReceiptSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedManifestTrustSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedManifestKeyReceiptSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedNativeChecksSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedClientInventoryReceiptSha256,
    [Parameter(Mandatory)]
    [ValidatePattern('^godswar_secure_acceptance_\d{8}_\d{6}$')]
    [string]$ExpectedDatabaseName,

    [string]$ClientRoot = 'C:\RebornNetworkAcceptanceClient',
    [ValidateSet('Baseline', 'Fallback', 'Soak')]
    [string]$EvidenceProfile = 'Baseline',
    [switch]$PreflightOnly,
    [switch]$EnablePhase4AcceptanceFaults,
    [switch]$AllowControlledHostActivation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$stockOriginSha256 =
    '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
$candidateOriginSha256 =
    'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C'
$stockNetSha256 =
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
$expectedClientRoot = 'C:\RebornNetworkAcceptanceClient'
$rawPorts = @(5998, 5999, 7000)
$secureTcpPorts = @(6599, 7443)
$secureUdpPort = 7444
$dnsNames = @('login.reborn.test', 'game.reborn.test')

Import-Module (
    Join-Path $PSScriptRoot `
        'ControlledHostServerLauncherDependencies.psm1'
) -Force

if ($EvidenceProfile -eq 'Fallback' -and
    -not $EnablePhase4AcceptanceFaults) {
    throw 'Fallback evidence requires -EnablePhase4AcceptanceFaults.'
}
if ($EvidenceProfile -ne 'Fallback' -and
    $EnablePhase4AcceptanceFaults) {
    throw "$EvidenceProfile evidence forbids Phase 4 acceptance faults."
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

Assert-RebornControlledHostRunnerIdentity | Out-Null

function Read-DpapiSecret {
    param([Parameter(Mandatory)][string]$Path)

    Assert-RebornSingleLinkRegularFilePath $Path 'DPAPI secret' |
        Out-Null
    $secure = Import-Clixml -LiteralPath $Path
    if ($secure -isnot [Security.SecureString]) {
        throw "DPAPI secret did not contain a SecureString: $Path"
    }
    $pointer = [IntPtr]::Zero
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
            $secure)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
        $secure.Dispose()
    }
}

function Assert-LoopbackDns {
    param([Parameter(Mandatory)][string]$Name)

    $addresses = @([Net.Dns]::GetHostAddresses($Name))
    if ($addresses.Count -eq 0) {
        throw "Development DNS name did not resolve: $Name"
    }
    foreach ($address in $addresses) {
        if (-not [Net.IPAddress]::IsLoopback($address)) {
            throw "$Name resolved outside loopback: $address"
        }
    }
}

function Get-TcpListeners {
    param([Parameter(Mandatory)][int[]]$Ports)
    return @(
        Get-NetTCPConnection -State Listen -ErrorAction Stop |
            Where-Object { $Ports -contains $_.LocalPort }
    )
}

function Get-UdpListeners {
    param([Parameter(Mandatory)][int[]]$Ports)
    return @(
        Get-NetUDPEndpoint -ErrorAction Stop |
            Where-Object { $Ports -contains $_.LocalPort }
    )
}

if (-not $AllowControlledHostActivation) {
    throw 'Explicit -AllowControlledHostActivation is required.'
}

$assembly = [IO.Path]::GetFullPath($ServerAssembly)
$options = [IO.Path]::GetFullPath($OptionsPath)
$client = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
$certificate = [IO.Path]::GetFullPath($CertificatePath)
$rootCertificate = [IO.Path]::GetFullPath($RootCertificatePath)
$trustReceipt = [IO.Path]::GetFullPath($TrustReceiptPath)
$manifestTrust = [IO.Path]::GetFullPath($ManifestTrustPath)
$manifestKeyReceipt = [IO.Path]::GetFullPath($ManifestKeyReceiptPath)
$nativeChecks = [IO.Path]::GetFullPath($NativeChecksPath)
$certificateSecret =
    [IO.Path]::GetFullPath($CertificatePasswordSecretPath)
$postgresSecret =
    [IO.Path]::GetFullPath($PostgresConnectionSecretPath)
$inventoryReceipt =
    [IO.Path]::GetFullPath($ClientInventoryReceiptPath)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
$acceptanceStamp = $ExpectedDatabaseName.Substring(
    'godswar_secure_acceptance_'.Length).Replace('_', '-')
$acceptanceRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot (
        '..\artifacts\controlled-host-acceptance\' +
        $acceptanceStamp))).TrimEnd('\')
$runtimeRoot =
    Get-RebornControlledHostRuntimeRoot $ExpectedDatabaseName
$expectedEvidence = Join-Path $acceptanceRoot 'server-evidence'
$expectedPaths = [ordered]@{
    ServerAssembly =
        Join-Path $runtimeRoot 'managed\Godswar.Server.dll'
    OptionsPath =
        Join-Path $runtimeRoot 'appsettings.json'
    CertificatePath =
        Join-Path $runtimeRoot 'tls\reborn-development-server.pfx'
    RootCertificatePath =
        Join-Path $runtimeRoot 'tls\reborn-development-root.cer'
    TrustReceiptPath =
        Join-Path $runtimeRoot 'tls\current-user-trust-receipt.json'
    ManifestTrustPath =
        Join-Path $runtimeRoot 'bundle\development-manifest-trust.json'
    ManifestKeyReceiptPath =
        Join-Path $runtimeRoot `
            'bundle\development-manifest-key-receipt.json'
    NativeChecksPath =
        Join-Path $runtimeRoot 'bundle\Godswar.NetShim.Checks.exe'
    CertificatePasswordSecretPath =
        Join-Path $runtimeRoot 'tls\certificate-password.dpapi.clixml'
    PostgresConnectionSecretPath =
        Join-Path $runtimeRoot 'tls\postgres-connection.dpapi.clixml'
    ClientRoot = $expectedClientRoot
    EvidenceDirectory = $expectedEvidence
}
$actualPaths = [ordered]@{
    ServerAssembly = $assembly
    OptionsPath = $options
    CertificatePath = $certificate
    RootCertificatePath = $rootCertificate
    TrustReceiptPath = $trustReceipt
    ManifestTrustPath = $manifestTrust
    ManifestKeyReceiptPath = $manifestKeyReceipt
    NativeChecksPath = $nativeChecks
    CertificatePasswordSecretPath = $certificateSecret
    PostgresConnectionSecretPath = $postgresSecret
    ClientRoot = $client
    EvidenceDirectory = $evidence
}
foreach ($name in $expectedPaths.Keys) {
    if (-not ([string]$actualPaths[$name]).Equals(
            [string]$expectedPaths[$name],
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$name is outside the exact controlled-host scope."
    }
}

$runtimeLock = $null
$hostsLock = $null
$runtimeLease = $null
$clientLease = $null
$certificatePassword = $null
$postgresConnection = $null
$environment = $null
$previous = $null
$evidenceFile = $null
try {
    $hostsLock = Enter-RebornDevelopmentHostsRuntimeLease
    $hostsAuthority =
        Assert-RebornDevelopmentHostsInstalledExact
    $runtimeLock = Enter-RebornControlledHostRuntimeLock `
        $runtimeRoot 'secure server lifetime'
    $runtimeLease =
        Enter-RebornControlledHostDirectoryLease $runtimeRoot
    $runtime = Assert-RebornControlledHostRuntime `
        $runtimeRoot `
        $ExpectedManagedReleaseSetSha256 `
        $ExpectedOptionsSha256 `
        $ExpectedCertificateSha256 `
        $ExpectedCertificateSecretSha256 `
        $ExpectedPostgresSecretSha256 `
        $ExpectedRootCertificateSha256 `
        $ExpectedTrustReceiptSha256 `
        $ExpectedManifestSha256 `
        $ExpectedManifestTrustSha256 `
        $ExpectedManifestKeyReceiptSha256 `
        $ExpectedNativeChecksSha256
    Assert-RebornControlledHostDirectoryLease $runtimeLease |
        Out-Null

    $clientLease =
        Enter-RebornControlledHostClientRootLease $client
    Assert-RebornControlledHostSafeProcessEnvironment | Out-Null
    Assert-RebornControlledHostUnsetEnvironmentNames @(
        'DOTNET_ENVIRONMENT',
        'ASPNETCORE_ENVIRONMENT',
        'GODSWAR_CONTROLLED_HOST_EVIDENCE_PATH'
    ) | Out-Null

    if ((Get-Sha256 $assembly) -cne
            $ExpectedServerSha256.ToUpperInvariant()) {
        throw 'Release server assembly hash does not match its pin.'
    }
    $managedRelease =
        Get-RebornControlledHostManagedReleaseSet (
            Split-Path -Parent $assembly)
    if ($managedRelease.SetSha256 -cne
        $ExpectedManagedReleaseSetSha256.ToUpperInvariant()) {
        throw 'Managed release set hash does not match its pin.'
    }
    $activation = Assert-RebornControlledHostClientActivation `
        $client `
        $inventoryReceipt `
        $ExpectedClientInventoryReceiptSha256 `
        $manifestTrust `
        $nativeChecks `
        $candidateOriginSha256 `
        $stockNetSha256 `
        $ExpectedCandidateSha256 `
        $ExpectedManifestSha256 `
        $ExpectedManifestTrustSha256 `
        $ExpectedNativeChecksSha256 `
        -ExpectedStockOriginSha256 $stockOriginSha256
    Assert-RebornControlledHostClientRootLease $clientLease |
        Out-Null

    foreach ($name in $dnsNames) {
        Assert-LoopbackDns $name
    }
    if (@(Get-TcpListeners $rawPorts).Count -ne 0) {
        throw 'Raw listener 5998, 5999, or 7000 is still active.'
    }
    if (@(Get-TcpListeners $secureTcpPorts).Count -ne 0 -or
        @(Get-UdpListeners @($secureUdpPort)).Count -ne 0) {
        throw 'A secure acceptance port is already in use.'
    }

    $certificatePassword = Read-DpapiSecret $certificateSecret
    $postgresConnection = Read-DpapiSecret $postgresSecret
    if ([string]::IsNullOrEmpty($certificatePassword) -or
        [string]::IsNullOrEmpty($postgresConnection)) {
        throw 'Controlled-host secrets cannot be empty.'
    }
    $environment = [ordered]@{
        GODSWAR_RUNTIME_PROFILE = 'LocalDevelopment'
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
        GODSWAR_SECURE_CERTIFICATE_PATH = $certificate
        GODSWAR_SECURE_CERTIFICATE_PASSWORD = $certificatePassword
        GODSWAR_SECURE_ALLOWED_ORIGIN_SHA256 =
            $candidateOriginSha256
        GODSWAR_AUTH_ALLOW_REGISTRATION = 'false'
        GODSWAR_AUTH_ALLOW_PLAINTEXT_MIGRATION = 'true'
        GODSWAR_AUTH_MAXIMUM_CONCURRENT_KDFS = '4'
        GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED =
            $EnablePhase4AcceptanceFaults.ToString().ToLowerInvariant()
    }
    foreach ($entry in (
        Get-RebornControlledHostDiagnosticsDisabledEnvironment
    ).GetEnumerator()) {
        $environment[$entry.Key] = $entry.Value
    }
    if ($EnablePhase4AcceptanceFaults) {
        $environment.DOTNET_ENVIRONMENT = 'Development'
        $environment.ASPNETCORE_ENVIRONMENT = 'Development'
    }
    Assert-RebornControlledHostNoUnreviewedGodswarEnvironment `
        (@($environment.Keys) +
            @('GODSWAR_CONTROLLED_HOST_EVIDENCE_PATH')) | Out-Null

    $previous = @{}
    $appliedEnvironmentKeys =
        [Collections.Generic.List[string]]::new()
    try {
        foreach ($entry in $environment.GetEnumerator()) {
            $previous[$entry.Key] =
                [Environment]::GetEnvironmentVariable(
                    $entry.Key,
                    [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable(
                $entry.Key,
                $entry.Value,
                [EnvironmentVariableTarget]::Process)
            $appliedEnvironmentKeys.Add($entry.Key)
        }

        Test-RebornControlledHostCertificate `
            $certificate `
            $rootCertificate `
            $trustReceipt `
            $certificatePassword `
            $assembly | Out-Null
        $databaseScope = Read-RebornAcceptanceDatabaseScope `
            $postgresConnection `
            $ExpectedDatabaseName `
            $assembly
        Test-RebornControlledHostServerOptions `
            $options `
            $assembly `
            $certificate `
            ([bool]$EnablePhase4AcceptanceFaults) | Out-Null
        Assert-RebornControlledHostDirectoryLease $runtimeLease |
            Out-Null
        Assert-RebornControlledHostClientRootLease $clientLease |
            Out-Null

        [pscustomobject]@{
            Result =
                if ($PreflightOnly) { 'PreflightPassed' } else { 'Starting' }
            ServerSha256 = Get-Sha256 $assembly
            ManagedReleaseSetSha256 = $managedRelease.SetSha256
            OptionsSha256 = Get-Sha256 $options
            CandidateSha256 =
                $ExpectedCandidateSha256.ToUpperInvariant()
            ManifestSha256 =
                $ExpectedManifestSha256.ToUpperInvariant()
            CertificateSha256 = Get-Sha256 $certificate
            DatabaseName = $databaseScope.DatabaseName
            ClientBundleState = $activation.State
            HostsState = $hostsAuthority.State
            ClientInventorySetSha256 =
                $activation.InstalledInventorySetSha256
            ManifestSequence = $activation.ManifestSequence
            Phase4AcceptanceFaults =
                [bool]$EnablePhase4AcceptanceFaults
            EvidenceProfile = $EvidenceProfile
            TlsPorts = $secureTcpPorts -join ','
            UdpPort = $secureUdpPort
            RawListeners = 0
        } | Format-List
        if ($PreflightOnly) {
            return
        }

        Assert-RebornDirectoryPath `
            $acceptanceRoot 'controlled-host acceptance root' | Out-Null
        if (-not (Test-Path -LiteralPath $evidence -PathType Container)) {
            New-Item -ItemType Directory -Path $evidence | Out-Null
        }
        Assert-RebornDirectoryPath `
            $evidence 'controlled-host evidence directory' | Out-Null
        $dotnet = Join-Path (
            [Environment]::GetFolderPath('ProgramFiles')
        ) 'dotnet\dotnet.exe'
        Assert-RebornSingleLinkRegularFilePath `
            $dotnet 'controlled-host .NET runtime host' | Out-Null

        $evidenceFile =
            New-RebornControlledHostEvidencePath $evidence
        $evidenceVariable =
            'GODSWAR_CONTROLLED_HOST_EVIDENCE_PATH'
        $previous[$evidenceVariable] =
            [Environment]::GetEnvironmentVariable(
                $evidenceVariable,
                [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            $evidenceVariable,
            $evidenceFile,
            [EnvironmentVariableTarget]::Process)
        $appliedEnvironmentKeys.Add($evidenceVariable)

        $serverExitCode = $null
        $evidenceResult = $null
        $runTimer = [Diagnostics.Stopwatch]::StartNew()
        try {
            & $dotnet $assembly $options
            $serverExitCode = $LASTEXITCODE
        }
        finally {
            $runTimer.Stop()
            if (Test-Path -LiteralPath $evidenceFile -PathType Leaf) {
                try {
                    $evidenceResult =
                        Assert-RebornControlledHostPrivacyEvidence `
                            $evidenceFile `
                            -Profile $EvidenceProfile `
                            -ObservedDuration $runTimer.Elapsed `
                            -RequireStopped
                }
                finally {
                    Protect-RebornControlledHostPrivacyEvidence `
                        $evidenceFile | Out-Null
                }
            }
            else {
                throw (
                    'The controlled-host server did not create its ' +
                    'privacy-safe evidence file.')
            }
        }
        if ($null -eq $serverExitCode -or $serverExitCode -ne 0) {
            $numericCode =
                if ($null -eq $serverExitCode) { -1 }
                else { [int]$serverExitCode }
            throw "Secure server exited with code $numericCode."
        }
        [pscustomobject]@{
            Result = 'Stopped'
            ExitCode = [int]$serverExitCode
            EvidencePath = $evidenceResult.Path
            EvidenceBytes = $evidenceResult.Bytes
            EvidenceEvents = $evidenceResult.Events
            EvidenceProfile = $evidenceResult.Profile
            ObservedDurationSeconds =
                $evidenceResult.ObservedDurationSeconds
        } | Format-List
    }
    finally {
        foreach ($key in $appliedEnvironmentKeys) {
            [Environment]::SetEnvironmentVariable(
                $key,
                $previous[$key],
                [EnvironmentVariableTarget]::Process)
        }
    }
}
finally {
    if ($environment -is [Collections.IDictionary]) {
        $environment.Clear()
    }
    if ($previous -is [Collections.IDictionary]) {
        $previous.Clear()
    }
    $certificatePassword = $null
    $postgresConnection = $null
    if ($null -ne $clientLease) {
        Exit-RebornControlledHostClientRootLease $clientLease
    }
    if ($null -ne $runtimeLease) {
        Exit-RebornControlledHostDirectoryLease $runtimeLease
    }
    if ($null -ne $runtimeLock) {
        Exit-RebornControlledHostRuntimeLock $runtimeLock
    }
    if ($null -ne $hostsLock) {
        Exit-RebornDevelopmentHostsRuntimeLock $hostsLock
    }
}
