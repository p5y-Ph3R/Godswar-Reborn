[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'

$resolvedReceipt = [IO.Path]::GetFullPath($ReceiptPath)
if (-not (Test-Path -LiteralPath $resolvedReceipt -PathType Leaf)) {
    throw "Trust receipt not found: $resolvedReceipt"
}

$receipt = Get-Content -LiteralPath $resolvedReceipt -Raw |
    ConvertFrom-Json
if ($receipt.version -ne 1 -or
    $receipt.storeLocation -ne 'CurrentUser' -or
    $receipt.storeName -ne 'Root' -or
    $receipt.thumbprint -notmatch '^[0-9A-Fa-f]{40}$' -or
    $receipt.rootSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
    throw 'The trust receipt is malformed or targets an unsupported store.'
}

if (-not $receipt.installedByScript) {
    [pscustomobject]@{
        Result = 'NoChange'
        Reason = 'The certificate predated this receipt or was not installed.'
        Thumbprint = $receipt.thumbprint
    }
    return
}

$store = New-Object Security.Cryptography.X509Certificates.X509Store(
    [Security.Cryptography.X509Certificates.StoreName]::Root,
    [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
)

try {
    $store.Open(
        [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
    )
    $matches = $store.Certificates.Find(
        [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
        $receipt.thumbprint,
        $false
    )
    if ($matches.Count -eq 0) {
        [pscustomobject]@{
            Result = 'AlreadyAbsent'
            Thumbprint = $receipt.thumbprint
        }
        return
    }
    if ($matches.Count -ne 1) {
        throw 'More than one CurrentUser root matched the receipt thumbprint.'
    }

    $certificate = $matches[0]
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $actualSha256 = (
            [BitConverter]::ToString(
                $sha256.ComputeHash($certificate.RawData)
            )
        ).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
    if ($certificate.Subject -ne 'CN=Reborn Development Root CA' -or
        $actualSha256 -ne $receipt.rootSha256.ToUpperInvariant()) {
        throw 'The installed certificate does not match the guarded receipt.'
    }

    if ($PSCmdlet.ShouldProcess(
            "Cert:\CurrentUser\Root\$($receipt.thumbprint)",
            'Remove the exact Reborn development root installed by the generator'
        )) {
        $store.Remove($certificate)
        $receipt |
            Add-Member -NotePropertyName removedUtc `
                -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) `
                -Force
        $receipt.installedByScript = $false
        $receipt |
            ConvertTo-Json |
            Set-Content -LiteralPath $resolvedReceipt -Encoding UTF8

        [pscustomobject]@{
            Result = 'Removed'
            Thumbprint = $receipt.thumbprint
            Recoverable = $true
            RootCertificatePath = Join-Path (
                Split-Path -Parent $resolvedReceipt
            ) 'reborn-development-root.cer'
        }
    }
}
finally {
    $store.Dispose()
}
