Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'ControlledHostProcessEnvironment.psm1'
) -Force

$script:DatabaseAccepted = 'CONTROLLED_HOST_DATABASE_SCOPE_VALID'
$script:OptionsAccepted = 'CONTROLLED_HOST_OPTIONS_VALID'
$script:CertificateAccepted = 'CONTROLLED_HOST_CERTIFICATE_VALID'
$script:MaximumConnectionStringCharacters = 4096

function Get-RebornDotnetHost {
    $expected = Join-Path (
        [Environment]::GetFolderPath('ProgramFiles')
    ) 'dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) {
        throw "The trusted x64 .NET host was not found: $expected"
    }
    return Assert-RebornSingleLinkRegularFilePath `
        $expected 'controlled-host .NET runtime host'
}

function Invoke-RebornControlledHostValidationProcess {
    param(
        [Parameter(Mandatory)][string]$ServerAssemblyPath,
        [Parameter(Mandatory)][string]$Arguments,
        [AllowEmptyString()][string]$StandardInput = '',
        [ValidateRange(1000, 30000)][int]$TimeoutMilliseconds = 10000
    )

    $assembly = Assert-RebornSingleLinkRegularFilePath (
        [IO.Path]::GetFullPath($ServerAssemblyPath)
    ) 'controlled-host server validation assembly'
    if ([IO.Path]::GetFileName($assembly) -cne 'Godswar.Server.dll') {
        throw 'The controlled-host validator must use Godswar.Server.dll.'
    }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Get-RebornDotnetHost
    $start.WorkingDirectory = Split-Path -Parent $assembly
    $start.Arguments = '"Godswar.Server.dll" ' + $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    Set-RebornControlledHostSanitizedChildEnvironment $start | Out-Null

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw 'The controlled-host validator did not start.'
        }
        $inputBytes = [Text.UTF8Encoding]::new($false).GetBytes(
            $StandardInput)
        $input = $null
        try {
            $input = $process.StandardInput.BaseStream
            $input.Write($inputBytes, 0, $inputBytes.Length)
            $input.Flush()
        }
        finally {
            [Array]::Clear($inputBytes, 0, $inputBytes.Length)
            if ($null -ne $input) {
                $input.Dispose()
            }
        }
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            try {
                $process.Kill()
                $process.WaitForExit()
            }
            catch {
                # Preserve the bounded-time rejection below.
            }
            throw 'The controlled-host validator exceeded its deadline.'
        }
        $output = $process.StandardOutput.ReadToEnd().Trim()
        $errorOutput = $process.StandardError.ReadToEnd().Trim()
        [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $output
            ErrorOutput = $errorOutput
        }
    }
    finally {
        $process.Dispose()
    }
}

function Read-RebornAcceptanceDatabaseScope {
    param(
        [Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)]
        [ValidatePattern('^godswar_secure_acceptance_\d{8}_\d{6}$')]
        [string]$ExpectedName,
        [Parameter(Mandatory)][string]$ServerAssemblyPath
    )

    if ($ConnectionString.Length -gt
        $script:MaximumConnectionStringCharacters) {
        throw 'The PostgreSQL acceptance connection string is oversized.'
    }
    $result = Invoke-RebornControlledHostValidationProcess `
        -ServerAssemblyPath $ServerAssemblyPath `
        -Arguments (
            '--controlled-host-validate-database-scope ' +
            $ExpectedName) `
        -StandardInput $ConnectionString
    if ($result.ExitCode -ne 0 -or
        $result.Output -cne $script:DatabaseAccepted -or
        -not [string]::IsNullOrEmpty($result.ErrorOutput)) {
        throw 'The PostgreSQL acceptance connection is outside safe scope.'
    }
    [pscustomobject]@{
        DatabaseName = $ExpectedName
        HostIsLoopback = $true
    }
}

function Test-RebornControlledHostServerOptions {
    param(
        [Parameter(Mandatory)][string]$OptionsPath,
        [Parameter(Mandatory)][string]$ServerAssemblyPath,
        [Parameter(Mandatory)][string]$ExpectedCertificatePath,
        [Parameter(Mandatory)][bool]$ExpectedAcceptanceFaults
    )

    $options = Assert-RebornSingleLinkRegularFilePath (
        [IO.Path]::GetFullPath($OptionsPath)
    ) 'controlled-host server options'
    $certificate = Assert-RebornSingleLinkRegularFilePath (
        [IO.Path]::GetFullPath($ExpectedCertificatePath)
    ) 'controlled-host expected TLS certificate'
    $quotedOptions = '"' + $options + '"'
    $quotedCertificate = '"' + $certificate + '"'
    $result = Invoke-RebornControlledHostValidationProcess `
        -ServerAssemblyPath $ServerAssemblyPath `
        -Arguments (
            '--controlled-host-validate-options ' +
            "$quotedOptions $quotedCertificate " +
            $ExpectedAcceptanceFaults.ToString().ToLowerInvariant())
    if ($result.ExitCode -ne 0 -or
        $result.Output -cne $script:OptionsAccepted -or
        -not [string]::IsNullOrEmpty($result.ErrorOutput)) {
        throw 'The controlled-host server options failed exact validation.'
    }
    return $true
}

function Test-RebornControlledHostCertificate {
    param(
        [Parameter(Mandatory)][string]$CertificatePath,
        [Parameter(Mandatory)][string]$RootCertificatePath,
        [Parameter(Mandatory)][string]$TrustReceiptPath,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$ServerAssemblyPath
    )

    $paths = foreach ($input in @(
        @($CertificatePath, 'controlled-host TLS PFX'),
        @($RootCertificatePath, 'controlled-host issued root'),
        @($TrustReceiptPath, 'controlled-host trust receipt')
    )) {
        Assert-RebornSingleLinkRegularFilePath (
            [IO.Path]::GetFullPath($input[0])
        ) $input[1]
    }
    $arguments =
        '--controlled-host-validate-certificate ' +
        (($paths | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $result = Invoke-RebornControlledHostValidationProcess `
        -ServerAssemblyPath $ServerAssemblyPath `
        -Arguments $arguments `
        -StandardInput $Password
    if ($result.ExitCode -ne 0 -or
        $result.Output -cne $script:CertificateAccepted -or
        -not [string]::IsNullOrEmpty($result.ErrorOutput)) {
        throw 'The controlled-host TLS certificate failed exact validation.'
    }
    return $true
}

Export-ModuleMember -Function @(
    'Read-RebornAcceptanceDatabaseScope',
    'Test-RebornControlledHostServerOptions',
    'Test-RebornControlledHostCertificate'
)
