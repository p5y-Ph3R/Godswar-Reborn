Set-StrictMode -Version Latest

$script:RootSubject = 'CN=Reborn Development Root CA'

function Get-RebornTrustSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($Bytes))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-RebornTrustFileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-RebornTrustCertificateDescriptor {
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate
    )

    [pscustomobject]@{
        Subject = $Certificate.Subject
        Thumbprint = $Certificate.Thumbprint.ToUpperInvariant()
        RootSha256 = Get-RebornTrustSha256 $Certificate.RawData
    }
}

function Assert-RebornTrustCertificateDescriptor {
    param(
        [Parameter(Mandatory)][object]$Descriptor,
        [Parameter(Mandatory)][object]$Receipt
    )

    if (
        $Descriptor.Subject -cne $script:RootSubject -or
        $Descriptor.Subject -cne [string]$Receipt.subject -or
        $Descriptor.Thumbprint -cne
            ([string]$Receipt.thumbprint).ToUpperInvariant() -or
        $Descriptor.RootSha256 -cne
            ([string]$Receipt.rootSha256).ToUpperInvariant()
    ) {
        throw 'Installed root does not match the exact trust receipt.'
    }
}

function New-RebornTrustReceiptRecord {
    param(
        [Parameter(Mandatory)][object]$Certificate,
        [Parameter(Mandatory)][string]$RootCertificateSha256,
        [Parameter(Mandatory)][string]$ServerPfxSha256,
        [bool]$InstalledByScript,
        [ValidateSet('PendingInstall', 'Installed', 'NoChange')]
        [string]$State
    )

    [ordered]@{
        schemaVersion = 2
        state = $State
        storeLocation = 'CurrentUser'
        storeName = 'Root'
        subject = $script:RootSubject
        thumbprint = $Certificate.Thumbprint.ToUpperInvariant()
        rootSha256 = $Certificate.RootSha256.ToUpperInvariant()
        rootCertificateFile = 'reborn-development-root.cer'
        rootCertificateSha256 =
            $RootCertificateSha256.ToUpperInvariant()
        serverPfxFile = 'reborn-development-server.pfx'
        serverPfxSha256 = $ServerPfxSha256.ToUpperInvariant()
        installedByScript = $InstalledByScript
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        installedUtc = $null
        migrationUtc = $null
        removalStartedUtc = $null
        removedUtc = $null
    }
}

function Write-RebornTrustReceiptAtomic {
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][string]$Path,
        [switch]$NoOverwrite
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if ($NoOverwrite -and (Test-Path -LiteralPath $resolved)) {
        throw 'Trust receipt already exists; refusing overwrite.'
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $resolved)) |
        Out-Null
    $temporary = "$resolved.$([guid]::NewGuid().ToString('N')).tmp"
    $previous = "$resolved.previous"
    if (Test-Path -LiteralPath $previous -PathType Leaf) {
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            [IO.File]::Delete($previous)
        } else {
            [IO.File]::Move($previous, $resolved)
        }
    }
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
            ($Record | ConvertTo-Json -Depth 6))
        try {
            $stream = [IO.FileStream]::new(
                $temporary,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush($true)
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            [IO.File]::Replace($temporary, $resolved, $previous, $true)
        } else {
            [IO.File]::Move($temporary, $resolved)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            [IO.File]::Delete($temporary)
        }
        if (
            (Test-Path -LiteralPath $resolved -PathType Leaf) -and
            (Test-Path -LiteralPath $previous -PathType Leaf)
        ) {
            [IO.File]::Delete($previous)
        }
    }
}

function Test-RebornTrustHash {
    param([object]$Value, [int]$Length)
    return $Value -is [string] -and
        $Value -cmatch "^[0-9A-Fa-f]{$Length}$"
}

function Read-RebornTrustReceipt {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path)
    $item = Get-Item -LiteralPath $resolved -ErrorAction Stop
    if ($item.Length -lt 128 -or $item.Length -gt 16384) {
        throw 'Trust receipt size is outside policy.'
    }
    try {
        $record = Get-Content -LiteralPath $resolved -Raw -Encoding utf8 |
            ConvertFrom-Json
    }
    catch {
        throw 'Trust receipt is not valid JSON.'
    }
    if (
        $record.schemaVersion -ne 2 -or
        $record.state -notin @(
            'PendingInstall',
            'Installed',
            'NoChange',
            'RemovalPending',
            'Removed') -or
        $record.storeLocation -cne 'CurrentUser' -or
        $record.storeName -cne 'Root' -or
        $record.subject -cne $script:RootSubject -or
        $record.rootCertificateFile -cne
            'reborn-development-root.cer' -or
        $record.serverPfxFile -cne
            'reborn-development-server.pfx' -or
        $record.installedByScript -isnot [bool] -or
        -not (Test-RebornTrustHash $record.thumbprint 40) -or
        -not (Test-RebornTrustHash $record.rootSha256 64) -or
        -not (Test-RebornTrustHash (
            $record.rootCertificateSha256) 64) -or
        -not (Test-RebornTrustHash $record.serverPfxSha256 64) -or
        ($record.state -eq 'NoChange' -and
            $record.installedByScript) -or
        ($record.state -ne 'NoChange' -and
            -not $record.installedByScript)
    ) {
        throw 'Trust receipt is malformed or outside cleanup policy.'
    }

    $directory = Split-Path -Parent $resolved
    $rootPath = Join-Path $directory $record.rootCertificateFile
    $pfxPath = Join-Path $directory $record.serverPfxFile
    foreach ($artifact in @($rootPath, $pfxPath)) {
        if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
            throw "Trust receipt artifact is absent: $artifact"
        }
    }
    if (
        (Get-RebornTrustFileSha256 $rootPath) -cne
            ([string]$record.rootCertificateSha256).ToUpperInvariant() -or
        (Get-RebornTrustFileSha256 $pfxPath) -cne
            ([string]$record.serverPfxSha256).ToUpperInvariant()
    ) {
        throw 'Trust receipt artifact hash binding failed.'
    }
    $root = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $rootPath)
    try {
        $descriptor = Get-RebornTrustCertificateDescriptor $root
        Assert-RebornTrustCertificateDescriptor $descriptor $record
        if ($root.HasPrivateKey -or $root.Subject -cne $root.Issuer) {
            throw 'Issued root CER is not the expected public self-signed root.'
        }
    }
    finally {
        $root.Dispose()
    }
    [pscustomobject]@{
        Path = $resolved
        RootPath = $rootPath
        PfxPath = $pfxPath
        Record = $record
    }
}

function Invoke-RebornTrustInstallReceiptTransaction {
    param(
        [object]$Record,
        [string]$ReceiptPath,
        [scriptblock]$AddCertificate,
        [scriptblock]$FindCertificate,
        [ValidateSet('None', 'AfterPendingReceipt', 'AfterStoreAdd')]
        [string]$FailurePoint = 'None'
    )

    $Record.state = 'PendingInstall'
    $Record.installedByScript = $true
    Write-RebornTrustReceiptAtomic $Record $ReceiptPath -NoOverwrite
    Read-RebornTrustReceipt $ReceiptPath | Out-Null
    if ($FailurePoint -eq 'AfterPendingReceipt') {
        throw 'Simulated interruption after pending trust receipt.'
    }
    & $AddCertificate
    if ($FailurePoint -eq 'AfterStoreAdd') {
        throw 'Simulated interruption after trust Store.Add.'
    }
    $certificate = & $FindCertificate
    if ($null -eq $certificate) {
        throw 'Trust Store.Add did not produce the exact installed root.'
    }
    Assert-RebornTrustCertificateDescriptor $certificate $Record
    $Record.state = 'Installed'
    $Record.installedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Write-RebornTrustReceiptAtomic $Record $ReceiptPath
    return Read-RebornTrustReceipt $ReceiptPath
}

function Invoke-RebornTrustRemovalTransaction {
    param(
        [object]$Loaded,
        [scriptblock]$FindCertificate,
        [scriptblock]$RemoveCertificate,
        [ValidateSet('None', 'AfterPendingReceipt', 'AfterStoreRemove')]
        [string]$FailurePoint = 'None'
    )

    $record = $Loaded.Record
    if ($record.state -eq 'NoChange') {
        return [pscustomobject]@{ Result = 'NoChange'; Loaded = $Loaded }
    }
    $certificate = & $FindCertificate
    if ($record.state -eq 'Removed') {
        if ($null -ne $certificate) {
            throw 'Removed trust authority cannot delete a reinstalled root.'
        }
        return [pscustomobject]@{ Result = 'AlreadyAbsent'; Loaded = $Loaded }
    }
    if ($null -ne $certificate) {
        Assert-RebornTrustCertificateDescriptor $certificate $record
    }
    if ($record.state -ne 'RemovalPending') {
        $record.state = 'RemovalPending'
        $record.removalStartedUtc =
            [DateTimeOffset]::UtcNow.ToString('O')
        Write-RebornTrustReceiptAtomic $record $Loaded.Path
    }
    if ($FailurePoint -eq 'AfterPendingReceipt') {
        throw 'Simulated interruption after pending trust removal receipt.'
    }
    if ($null -ne $certificate) {
        & $RemoveCertificate
    }
    if ($FailurePoint -eq 'AfterStoreRemove') {
        throw 'Simulated interruption after trust Store.Remove.'
    }
    if ($null -ne (& $FindCertificate)) {
        throw 'Exact development root remains after Store.Remove.'
    }
    $record.state = 'Removed'
    $record.removedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Write-RebornTrustReceiptAtomic $record $Loaded.Path
    return [pscustomobject]@{
        Result = if ($null -eq $certificate) {
            'AlreadyAbsent'
        } else {
            'Removed'
        }
        Loaded = Read-RebornTrustReceipt $Loaded.Path
    }
}

function ConvertFrom-RebornLegacyTrustReceipt {
    param(
        [Parameter(Mandatory)][object]$Legacy,
        [Parameter(Mandatory)][object]$Certificate,
        [Parameter(Mandatory)][string]$RootCertificateSha256,
        [Parameter(Mandatory)][string]$ServerPfxSha256
    )

    if (
        $Legacy.version -ne 1 -or
        $Legacy.storeLocation -cne 'CurrentUser' -or
        $Legacy.storeName -cne 'Root' -or
        $Legacy.installedByScript -isnot [bool] -or
        -not $Legacy.installedByScript -or
        $Legacy.thumbprint -cne $Certificate.Thumbprint -or
        $Legacy.rootSha256 -cne $Certificate.RootSha256
    ) {
        throw 'Legacy trust receipt cannot issue schema-2 cleanup authority.'
    }
    $record = New-RebornTrustReceiptRecord `
        $Certificate $RootCertificateSha256 $ServerPfxSha256 `
        $true 'Installed'
    $record.createdUtc = [string]$Legacy.createdUtc
    $record.installedUtc = [string]$Legacy.createdUtc
    $record.migrationUtc = [DateTimeOffset]::UtcNow.ToString('O')
    return $record
}

Export-ModuleMember -Function @(
    'Get-RebornTrustFileSha256',
    'Get-RebornTrustCertificateDescriptor',
    'Assert-RebornTrustCertificateDescriptor',
    'New-RebornTrustReceiptRecord',
    'Write-RebornTrustReceiptAtomic',
    'Read-RebornTrustReceipt',
    'Invoke-RebornTrustInstallReceiptTransaction',
    'Invoke-RebornTrustRemovalTransaction',
    'ConvertFrom-RebornLegacyTrustReceipt'
)
