[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerAssemblyPath,
    [Parameter(Mandatory)][string]$CertificatePath,
    [Parameter(Mandatory)][string]$RootCertificatePath,
    [Parameter(Mandatory)][string]$TrustReceiptPath,
    [Parameter(Mandatory)][string]$CertificatePasswordSecretPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostServerValidation.psm1'
) -Force

function Read-DpapiSecret {
    param([Parameter(Mandatory)][string]$Path)

    $secure = Import-Clixml -LiteralPath $Path
    if ($secure -isnot [Security.SecureString]) {
        throw 'Certificate secret is not a DPAPI SecureString.'
    }
    $pointer = [IntPtr]::Zero
    try {
        $pointer =
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
                $secure)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
            $pointer)
    }
    finally {
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
        $secure.Dispose()
    }
}

$password = Read-DpapiSecret $CertificatePasswordSecretPath
try {
    Test-RebornControlledHostCertificate `
        $CertificatePath `
        $RootCertificatePath `
        $TrustReceiptPath `
        $password `
        $ServerAssemblyPath | Out-Null
}
finally {
    $password = $null
}

Write-Host 'Installed controlled-host certificate validation passed.'
