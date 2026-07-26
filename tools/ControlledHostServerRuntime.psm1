Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'ControlledHostManagedRelease.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureEndpointManifestValidation.psm1'
)

function Get-RebornControlledHostRuntimeRoot {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^godswar_secure_acceptance_\d{8}_\d{6}$')]
        [string]$DatabaseName
    )

    $stamp = $DatabaseName.Substring(
        'godswar_secure_acceptance_'.Length).Replace('_', '-')
    [IO.Path]::GetFullPath(
        (Join-Path (
            [Environment]::GetFolderPath('CommonApplicationData')
        ) "RebornSecureNetworkRuntime\$stamp")).TrimEnd('\')
}

function New-RebornControlledHostRuntimeSecurity {
    $administrators =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $currentUser =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    $inheritance =
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $none = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administrators)
    foreach ($principal in @($administrators, $system)) {
        $security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $principal,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                $none,
                $allow))
    }
    $security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $currentUser,
            [Security.AccessControl.FileSystemRights]::ReadAndExecute,
            $inheritance,
            $none,
            $allow))
    return $security
}

function Read-RebornControlledHostRuntimeReceipt {
    param([Parameter(Mandatory)][string]$ReceiptPath)

    $receipt = [IO.Path]::GetFullPath($ReceiptPath)
    if ([IO.Path]::GetFileName($receipt) -cne 'receipt.json') {
        throw 'Controlled-host runtime receipt must be named receipt.json.'
    }
    $runtime = Split-Path -Parent $receipt
    $runtimeParent = Split-Path -Parent $runtime
    $expectedParent = [IO.Path]::GetFullPath(
        (Join-Path (
            [Environment]::GetFolderPath('CommonApplicationData')
        ) 'RebornSecureNetworkRuntime')).TrimEnd('\')
    if (-not $runtimeParent.Equals(
            $expectedParent,
            [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($runtime) -cnotmatch
            '^\d{8}-\d{6}$') {
        throw 'Controlled-host runtime receipt is outside issued scope.'
    }
    Assert-RebornProtectedDirectoryPath `
        $runtime 'controlled-host runtime root' `
        -ProtectContents `
        -RequireProtectedAcl | Out-Null

    $checksum = Join-Path $runtime 'receipt.sha256'
    foreach ($path in @($receipt, $checksum)) {
        Assert-RebornSingleLinkRegularFilePath `
            $path 'controlled-host runtime receipt file' | Out-Null
        Assert-RebornProtectedRegularFilePath `
            $path 'controlled-host runtime receipt file' | Out-Null
    }
    $expected = (Get-Content -LiteralPath $checksum -Raw).Trim()
    if ($expected -cnotmatch '^[0-9A-F]{64}$' -or
        (Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash -cne
            $expected) {
        throw 'Controlled-host runtime receipt checksum failed.'
    }
    $record = Get-Content -LiteralPath $receipt -Raw |
        ConvertFrom-Json
    $currentSid =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $activationEnvironment = [UInt64]0
    $activationSequenceFloor = [UInt64]0
    if ($record.schemaVersion -ne 1 -or
        $record.mode -cne 'ControlledHostServerRuntime' -or
        -not ([IO.Path]::GetFullPath(
            [string]$record.runtimeRoot)).Equals(
                $runtime,
                [StringComparison]::OrdinalIgnoreCase) -or
        $record.readerSid -cne $currentSid -or
        $record.databaseName -cnotmatch
            '^godswar_secure_acceptance_\d{8}_\d{6}$' -or
        [string]$record.manifestSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        -not [UInt64]::TryParse(
            [string]$record.activationEnvironment,
            [ref]$activationEnvironment) -or
        $activationEnvironment -lt 1 -or
        $activationEnvironment -gt 3 -or
        -not [UInt64]::TryParse(
            [string]$record.activationSequenceFloor,
            [ref]$activationSequenceFloor) -or
        $activationSequenceFloor -eq 0) {
        throw 'Controlled-host runtime receipt is not applicable.'
    }
    $databaseRuntime =
        Get-RebornControlledHostRuntimeRoot $record.databaseName
    if (-not $databaseRuntime.Equals(
            $runtime,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'Controlled-host runtime receipt database timestamp does not ' +
            'match its directory.')
    }
    [pscustomobject]@{
        RuntimeRoot = $runtime
        ReceiptPath = $receipt
        ChecksumPath = $checksum
        Record = $record
    }
}

function Assert-RebornControlledHostTlsDirectoryEntries {
    param([Parameter(Mandatory)][string]$TlsDirectory)

    $tls = Assert-RebornDirectoryPath (
        [IO.Path]::GetFullPath($TlsDirectory).TrimEnd('\')
    ) 'controlled-host runtime TLS directory'
    $expectedNames = @(
        'certificate-password.dpapi.clixml',
        'current-user-trust-receipt.json',
        'postgres-connection.dpapi.clixml',
        'reborn-development-root.cer',
        'reborn-development-server.pfx'
    )
    $entries = @(Get-ChildItem -LiteralPath $tls -Force)
    if ($entries.Count -ne $expectedNames.Count -or
        @($entries | Where-Object {
            $_.PSIsContainer -or
            $expectedNames -cnotcontains $_.Name
        }).Count -ne 0) {
        throw (
            'The controlled-host TLS directory does not contain the exact ' +
            'issued file set.')
    }
    $paths = [ordered]@{}
    foreach ($name in $expectedNames) {
        $paths[$name] = Assert-RebornSingleLinkRegularFilePath (
            Join-Path $tls $name
        ) "controlled-host TLS file $name"
    }
    return [pscustomobject]$paths
}

function Assert-RebornControlledHostBundleDirectoryEntries {
    param([Parameter(Mandatory)][string]$BundleDirectory)

    $bundle = Assert-RebornDirectoryPath (
        [IO.Path]::GetFullPath($BundleDirectory).TrimEnd('\')
    ) 'controlled-host runtime bundle directory'
    $expectedNames = @(
        'development-manifest-key-receipt.json',
        'development-manifest-trust.json',
        'Godswar.NetShim.Checks.exe',
        'RebornNetwork.gwem'
    )
    $entries = @(Get-ChildItem -LiteralPath $bundle -Force)
    if ($entries.Count -ne $expectedNames.Count -or
        @($entries | Where-Object {
            $_.PSIsContainer -or
            $expectedNames -cnotcontains $_.Name
        }).Count -ne 0) {
        throw (
            'The controlled-host bundle directory does not contain the ' +
            'exact issued file set.')
    }
    $paths = [ordered]@{}
    foreach ($name in $expectedNames) {
        $paths[$name] = Assert-RebornSingleLinkRegularFilePath (
            Join-Path $bundle $name
        ) "controlled-host bundle file $name"
    }
    return [pscustomobject]$paths
}

function Assert-RebornControlledHostRuntime {
    param(
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedManagedReleaseSetSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedOptionsSha256,
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
        [string]$ExpectedManifestSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedManifestTrustSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedManifestKeyReceiptSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedNativeChecksSha256
    )

    $runtime = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\')
    $receipt = Read-RebornControlledHostRuntimeReceipt (
        Join-Path $runtime 'receipt.json')
    $entries = @(Get-ChildItem -LiteralPath $runtime -Force)
    $expectedEntries = @(
        'appsettings.json',
        'bundle',
        'managed',
        'receipt.json',
        'receipt.sha256',
        'tls'
    )
    if ($entries.Count -ne $expectedEntries.Count -or
        @($entries | Where-Object {
            $expectedEntries -cnotcontains $_.Name
        }).Count -ne 0) {
        throw 'Controlled-host runtime root contains an unexpected entry.'
    }

    $managed = Get-RebornControlledHostManagedReleaseSet (
        Join-Path $runtime 'managed')
    if ($managed.SetSha256 -cne
        $ExpectedManagedReleaseSetSha256.ToUpperInvariant()) {
        throw 'Protected managed release set hash mismatch.'
    }
    foreach ($file in $managed.Files) {
        Assert-RebornProtectedRegularFilePath `
            $file.Path 'protected managed release file' | Out-Null
    }
    $tls = Assert-RebornControlledHostTlsDirectoryEntries (
        Join-Path $runtime 'tls')
    $bundle = Assert-RebornControlledHostBundleDirectoryEntries (
        Join-Path $runtime 'bundle')

    $specifications = @(
        @(
            (Join-Path $runtime 'appsettings.json'),
            $ExpectedOptionsSha256,
            'protected server options'
        ),
        @(
            $tls.'reborn-development-server.pfx',
            $ExpectedCertificateSha256,
            'protected TLS PFX'
        ),
        @(
            $tls.'certificate-password.dpapi.clixml',
            $ExpectedCertificateSecretSha256,
            'protected certificate secret'
        ),
        @(
            $tls.'postgres-connection.dpapi.clixml',
            $ExpectedPostgresSecretSha256,
            'protected PostgreSQL secret'
        ),
        @(
            $tls.'reborn-development-root.cer',
            $ExpectedRootCertificateSha256,
            'protected issued root'
        ),
        @(
            $tls.'current-user-trust-receipt.json',
            $ExpectedTrustReceiptSha256,
            'protected trust receipt'
        ),
        @(
            $bundle.'RebornNetwork.gwem',
            $ExpectedManifestSha256,
            'protected endpoint manifest'
        ),
        @(
            $bundle.'development-manifest-trust.json',
            $ExpectedManifestTrustSha256,
            'protected endpoint-manifest trust'
        ),
        @(
            $bundle.'development-manifest-key-receipt.json',
            $ExpectedManifestKeyReceiptSha256,
            'protected manifest-key receipt'
        ),
        @(
            $bundle.'Godswar.NetShim.Checks.exe',
            $ExpectedNativeChecksSha256,
            'protected native checks'
        )
    )
    foreach ($specification in $specifications) {
        $path = Assert-RebornSingleLinkRegularFilePath `
            $specification[0] $specification[2]
        Assert-RebornProtectedRegularFilePath `
            $path $specification[2] | Out-Null
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne
            ([string]$specification[1]).ToUpperInvariant()) {
            throw "$($specification[2]) hash mismatch."
        }
    }

    $manifest =
        Read-RebornSecureEndpointManifestForRestore `
            $bundle.'RebornNetwork.gwem' `
            $bundle.'development-manifest-trust.json' `
            0
    if ($manifest.ManifestSha256 -cne
            $ExpectedManifestSha256.ToUpperInvariant() -or
        $manifest.TrustSha256 -cne
            $ExpectedManifestTrustSha256.ToUpperInvariant()) {
        throw 'Protected endpoint manifest authority mismatch.'
    }
    $record = $receipt.Record
    foreach ($binding in @(
        @('managedReleaseSetSha256', $ExpectedManagedReleaseSetSha256),
        @('optionsSha256', $ExpectedOptionsSha256),
        @('certificateSha256', $ExpectedCertificateSha256),
        @('certificateSecretSha256', $ExpectedCertificateSecretSha256),
        @('postgresSecretSha256', $ExpectedPostgresSecretSha256),
        @('rootCertificateSha256', $ExpectedRootCertificateSha256),
        @('trustReceiptSha256', $ExpectedTrustReceiptSha256),
        @('manifestSha256', $ExpectedManifestSha256),
        @('manifestTrustSha256', $ExpectedManifestTrustSha256),
        @('manifestKeyReceiptSha256', $ExpectedManifestKeyReceiptSha256),
        @('nativeChecksSha256', $ExpectedNativeChecksSha256)
    )) {
        if ([string]$record.($binding[0]) -cne
            ([string]$binding[1]).ToUpperInvariant()) {
            throw "Controlled-host runtime receipt mismatch: $($binding[0])"
        }
    }
    if ([string]$record.activationEnvironment -cne
            ([UInt64]$manifest.Environment).ToString(
                [Globalization.CultureInfo]::InvariantCulture) -or
        [string]$record.activationSequenceFloor -cne
            ([UInt64]$manifest.Sequence).ToString(
                [Globalization.CultureInfo]::InvariantCulture)) {
        throw (
            'Controlled-host runtime receipt activation authority does ' +
            'not match the signed endpoint manifest.')
    }
    return $receipt
}

Export-ModuleMember -Function @(
    'Get-RebornControlledHostRuntimeRoot',
    'New-RebornControlledHostRuntimeSecurity',
    'Read-RebornControlledHostRuntimeReceipt',
    'Assert-RebornControlledHostTlsDirectoryEntries',
    'Assert-RebornControlledHostBundleDirectoryEntries',
    'Assert-RebornControlledHostRuntime'
)
