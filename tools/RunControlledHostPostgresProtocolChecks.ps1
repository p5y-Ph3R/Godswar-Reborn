[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^godswar_secure_acceptance_\d{8}_\d{6}$')]
    [string]$ExpectedDatabaseName,

    [Parameter(Mandatory)]
    [string]$PostgresConnectionSecretPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedPostgresConnectionSecretSha256,

    [string]$ServerAssemblyPath = (
        Join-Path $PSScriptRoot `
            '..\src\Godswar.Server\bin\Release\net10.0\Godswar.Server.dll'),

    [string]$ProtocolChecksProject = (
        Join-Path $PSScriptRoot `
            '..\tests\Godswar.Server.ProtocolChecks\Godswar.Server.ProtocolChecks.csproj')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($moduleName in @(
    'ControlledHostRunnerIdentity.psm1',
    'ControlledHostServerValidation.psm1',
    'ControlledHostProcessEnvironment.psm1',
    'SecureNetworkPathSafety.psm1'
)) {
    Import-Module (Join-Path $PSScriptRoot $moduleName) -Force
}

Assert-RebornControlledHostRunnerIdentity | Out-Null
Assert-RebornControlledHostSafeProcessEnvironment | Out-Null

$variable = 'GODSWAR_TEST_POSTGRES_CONNECTION_STRING'
Assert-RebornControlledHostUnsetEnvironmentNames @($variable) | Out-Null
Assert-RebornControlledHostNoUnreviewedGodswarEnvironment @($variable) |
    Out-Null

$assembly = [IO.Path]::GetFullPath($ServerAssemblyPath)
$expectedAssembly = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot `
        '..\src\Godswar.Server\bin\Release\net10.0\Godswar.Server.dll'))
$project = [IO.Path]::GetFullPath($ProtocolChecksProject)
$expectedProject = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot `
        '..\tests\Godswar.Server.ProtocolChecks\Godswar.Server.ProtocolChecks.csproj'))
$secret = [IO.Path]::GetFullPath($PostgresConnectionSecretPath)
$stamp = $ExpectedDatabaseName.Substring(
    'godswar_secure_acceptance_'.Length).Replace('_', '-')
$expectedSecret = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot (
        '..\artifacts\controlled-host-acceptance\' +
        "$stamp\tls\postgres-connection.dpapi.clixml")))
foreach ($scope in @(
    @($assembly, $expectedAssembly, 'server assembly'),
    @($project, $expectedProject, 'protocol-check project'),
    @($secret, $expectedSecret, 'PostgreSQL secret')
)) {
    if (-not $scope[0].Equals(
            $scope[1],
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$($scope[2]) is outside the controlled-host check scope."
    }
}
Assert-RebornSingleLinkRegularFilePath `
    $assembly 'controlled-host server assembly' | Out-Null
Assert-RebornSingleLinkRegularFilePath `
    $project 'controlled-host protocol project' | Out-Null
Assert-RebornSingleLinkRegularFilePath `
    $secret 'controlled-host PostgreSQL secret' | Out-Null
if ((Get-FileHash -LiteralPath $secret -Algorithm SHA256).Hash -cne
        $ExpectedPostgresConnectionSecretSha256.ToUpperInvariant()) {
    throw 'The controlled-host PostgreSQL secret hash is not exact.'
}

$dotnet = Join-Path (
    [Environment]::GetFolderPath('ProgramFiles')
) 'dotnet\dotnet.exe'
Assert-RebornSingleLinkRegularFilePath `
    $dotnet 'controlled-host .NET runtime host' | Out-Null
$windows = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::Windows)
$taskkill = Join-Path $windows 'System32\taskkill.exe'
Assert-RebornProtectedRegularFilePath `
    $taskkill 'controlled-host process-tree terminator' | Out-Null
$taskkillSignature = Get-AuthenticodeSignature -LiteralPath $taskkill
if ($taskkillSignature.Status -ne
        [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $taskkillSignature.SignerCertificate -or
    $taskkillSignature.SignerCertificate.Subject -notmatch
        '(^|, )O=Microsoft Corporation(, |$)') {
    throw 'The controlled-host process-tree terminator is not Microsoft signed.'
}

& $dotnet build $project --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Protocol-check Release build exited with code $LASTEXITCODE."
}

$secure = Import-Clixml -LiteralPath $secret
if ($secure -isnot [Security.SecureString]) {
    throw 'The controlled-host PostgreSQL artifact is not a SecureString.'
}
$pointer = [IntPtr]::Zero
$connection = $null
$start = $null
$process = $null
$processId = 0
$operationFailure = $null
$cleanupFailure = $null
try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
        $secure)
    $connection =
        [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ([string]::IsNullOrEmpty($connection)) {
        throw 'The controlled-host PostgreSQL secret is empty.'
    }
    $scope = Read-RebornAcceptanceDatabaseScope `
        $connection $ExpectedDatabaseName $assembly
    if ($scope.DatabaseName -cne $ExpectedDatabaseName -or
        -not $scope.HostIsLoopback) {
        throw 'The PostgreSQL protocol-check scope is not exact loopback.'
    }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnet
    $start.WorkingDirectory =
        [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $false
    $start.Arguments =
        'run --project "' + $project +
        '" --configuration Release --no-build'
    Set-RebornControlledHostSanitizedChildEnvironment $start |
        Out-Null
    $start.EnvironmentVariables[$variable] = $connection
    $process = [Diagnostics.Process]::Start($start)
    $processId = $process.Id
    if (-not $process.WaitForExit(300000)) {
        throw 'PostgreSQL protocol checks exceeded the five-minute limit.'
    }
    $exitCode = $process.ExitCode
    if ($exitCode -ne 0) {
        throw "PostgreSQL protocol checks exited with code $exitCode."
    }
}
catch {
    $operationFailure = $_
}
finally {
    if ($null -ne $process) {
        $needsTermination = $true
        try {
            $needsTermination = -not $process.HasExited
        }
        catch {
            $cleanupFailure =
                'Could not inspect the PostgreSQL protocol-check process.'
        }
        if ($needsTermination -and $processId -gt 0) {
            try {
                & $taskkill /PID $processId /T /F | Out-Null
                $process.WaitForExit(10000) | Out-Null
                if (-not $process.HasExited) {
                    $cleanupFailure =
                        'The PostgreSQL protocol-check process tree ' +
                        'could not be terminated.'
                }
            }
            catch {
                $cleanupFailure =
                    'The PostgreSQL protocol-check process tree ' +
                    'could not be terminated.'
            }
        }
    }
    if ($null -ne $start) {
        try {
            $start.EnvironmentVariables.Remove($variable) | Out-Null
        }
        catch {
            if ($null -eq $cleanupFailure) {
                $cleanupFailure =
                    'Could not clear the child PostgreSQL environment.'
            }
        }
    }
    $connection = $null
    if ($pointer -ne [IntPtr]::Zero) {
        try {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
            $pointer = [IntPtr]::Zero
        }
        catch {
            if ($null -eq $cleanupFailure) {
                $cleanupFailure =
                    'Could not release the PostgreSQL secret buffer.'
            }
        }
    }
    try {
        $secure.Dispose()
    }
    catch {
        if ($null -eq $cleanupFailure) {
            $cleanupFailure =
                'Could not dispose the PostgreSQL secret.'
        }
    }
    if ($null -ne $process) {
        try {
            $process.Dispose()
        }
        catch {
            if ($null -eq $cleanupFailure) {
                $cleanupFailure =
                    'Could not dispose the PostgreSQL check process.'
            }
        }
    }
}
if ($null -ne $cleanupFailure) {
    if ($null -ne $operationFailure) {
        throw (
            $cleanupFailure +
            ' The protocol-check operation also failed.')
    }
    throw $cleanupFailure
}
if ($null -ne $operationFailure) {
    throw $operationFailure
}

[pscustomobject]@{
    Result = 'Passed'
    DatabaseName = $ExpectedDatabaseName
    HostIsLoopback = $scope.HostIsLoopback
    Port = 5432
    ServerSha256 =
        (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
}
