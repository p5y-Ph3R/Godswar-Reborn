Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'ControlledHostServerRuntime.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'ControlledHostClientInventoryReceipt.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkActivationState.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'ControlledHostManagedRelease.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureEndpointManifestValidation.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkTrustReceipt.psm1'
)

$script:ExpectedClientRoot = 'C:\RebornNetworkAcceptanceClient'

function Assert-RebornControlledHostCleanupClientRoot {
    param([Parameter(Mandatory)][string]$ClientRoot)

    $client = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
    if (-not $client.Equals(
            $script:ExpectedClientRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'Cleanup requires the exact disposable controlled client at ' +
            "$script:ExpectedClientRoot.")
    }
    return $client
}

function Get-RebornControlledHostCleanupRuntimeAuthority {
    param([Parameter(Mandatory)][string]$RuntimeRoot)

    $runtime = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\')
    $receipt = Read-RebornControlledHostRuntimeReceipt (
        Join-Path $runtime 'receipt.json')
    $record = $receipt.Record
    $expectedEntries = @(
        'appsettings.json',
        'bundle',
        'managed',
        'receipt.json',
        'receipt.sha256',
        'tls'
    )
    $entries = @(Get-ChildItem -LiteralPath $runtime -Force)
    if ($entries.Count -ne $expectedEntries.Count -or
        @($entries | Where-Object {
            $expectedEntries -cnotcontains $_.Name
        }).Count -ne 0) {
        throw 'Cleanup runtime root contains an unexpected entry.'
    }
    $tls = Assert-RebornControlledHostTlsDirectoryEntries (
        Join-Path $runtime 'tls')
    $bundle = Assert-RebornControlledHostBundleDirectoryEntries (
        Join-Path $runtime 'bundle')
    $trustHash = (
        Get-FileHash `
            $tls.'current-user-trust-receipt.json' `
            -Algorithm SHA256).Hash
    $keyHash = (
        Get-FileHash `
            $bundle.'development-manifest-key-receipt.json' `
            -Algorithm SHA256).Hash
    if (
        $trustHash -ceq [string]$record.trustReceiptSha256 -and
        $keyHash -ceq [string]$record.manifestKeyReceiptSha256
    ) {
        return Assert-RebornControlledHostRuntime `
            $runtime `
            ([string]$record.managedReleaseSetSha256) `
            ([string]$record.optionsSha256) `
            ([string]$record.certificateSha256) `
            ([string]$record.certificateSecretSha256) `
            ([string]$record.postgresSecretSha256) `
            ([string]$record.rootCertificateSha256) `
            ([string]$record.trustReceiptSha256) `
            ([string]$record.manifestSha256) `
            ([string]$record.manifestTrustSha256) `
            ([string]$record.manifestKeyReceiptSha256) `
            ([string]$record.nativeChecksSha256)
    }

    $managed = Get-RebornControlledHostManagedReleaseSet (
        Join-Path $runtime 'managed')
    if ($managed.SetSha256 -cne
        [string]$record.managedReleaseSetSha256) {
        throw 'Cleanup runtime managed release hash changed.'
    }
    foreach ($file in $managed.Files) {
        Assert-RebornProtectedRegularFilePath `
            $file.Path 'cleanup runtime managed file' | Out-Null
    }
    foreach ($binding in @(
        @(
            (Join-Path $runtime 'appsettings.json'),
            $record.optionsSha256
        ),
        @(
            $tls.'reborn-development-server.pfx',
            $record.certificateSha256
        ),
        @(
            $tls.'certificate-password.dpapi.clixml',
            $record.certificateSecretSha256
        ),
        @(
            $tls.'postgres-connection.dpapi.clixml',
            $record.postgresSecretSha256
        ),
        @(
            $tls.'reborn-development-root.cer',
            $record.rootCertificateSha256
        ),
        @(
            $bundle.'RebornNetwork.gwem',
            $record.manifestSha256
        ),
        @(
            $bundle.'development-manifest-trust.json',
            $record.manifestTrustSha256
        ),
        @(
            $bundle.'Godswar.NetShim.Checks.exe',
            $record.nativeChecksSha256
        )
    )) {
        $expected = [string]$binding[1]
        if ($expected -cnotmatch '^[0-9A-F]{64}$') {
            throw 'Cleanup runtime receipt contains an invalid hash.'
        }
        Assert-RebornProtectedRegularFilePath `
            $binding[0] 'cleanup runtime immutable file' | Out-Null
        if ((Get-FileHash $binding[0] -Algorithm SHA256).Hash -cne
            $expected) {
            throw 'Cleanup runtime immutable file hash changed.'
        }
    }

    $manifest = Read-RebornSecureEndpointManifestForRestore `
        $bundle.'RebornNetwork.gwem' `
        $bundle.'development-manifest-trust.json' `
        0
    if (
        $manifest.ManifestSha256 -cne
            [string]$record.manifestSha256 -or
        $manifest.TrustSha256 -cne
            [string]$record.manifestTrustSha256 -or
        ([UInt64]$manifest.Environment).ToString() -cne
            [string]$record.activationEnvironment -or
        ([UInt64]$manifest.Sequence).ToString() -cne
            [string]$record.activationSequenceFloor
    ) {
        throw 'Cleanup runtime signed-manifest authority changed.'
    }

    if ($trustHash -cne [string]$record.trustReceiptSha256) {
        $trustTransition = Read-RebornTrustReceipt (
            $tls.'current-user-trust-receipt.json')
        if ($trustTransition.Record.state -notin @(
                'RemovalPending', 'Removed')) {
            throw 'Trust receipt changed outside its cleanup transaction.'
        }
    }
    if ($keyHash -cne [string]$record.manifestKeyReceiptSha256) {
        $keyItem = Get-Item -LiteralPath (
            $bundle.'development-manifest-key-receipt.json') -Force
        if ($keyItem.Length -lt 128 -or $keyItem.Length -gt 16384) {
            throw 'Manifest key cleanup receipt exceeds its bounded size.'
        }
        try {
            $keyTransition = Get-Content $keyItem.FullName -Raw |
                ConvertFrom-Json
        }
        catch {
            throw 'Manifest key cleanup receipt is not valid JSON.'
        }
        $manifestTrust = Get-Content `
            $bundle.'development-manifest-trust.json' -Raw |
            ConvertFrom-Json
        $coordinateLengths = @()
        try {
            foreach ($coordinate in @(
                $keyTransition.current.x,
                $keyTransition.current.y,
                $keyTransition.next.x,
                $keyTransition.next.y
            )) {
                $bytes = [Convert]::FromBase64String(
                    [string]$coordinate)
                $coordinateLengths += $bytes.Length
                [Array]::Clear($bytes, 0, $bytes.Length)
            }
        }
        catch {
            throw 'Manifest key cleanup receipt has invalid coordinates.'
        }
        if (
            $keyTransition.schemaVersion -ne 1 -or
            $keyTransition.state -notin @('RemovalPending', 'Removed') -or
            [string]$keyTransition.headerSha256 -cnotmatch
                '^[0-9A-F]{64}$' -or
            [string]$keyTransition.currentTrustSha256 -cnotmatch
                '^[0-9A-F]{64}$' -or
            [string]$keyTransition.nextTrustSha256 -cnotmatch
                '^[0-9A-F]{64}$' -or
            [string]$keyTransition.currentTrustSha256 -cne
                [string]$record.manifestTrustSha256 -or
            $keyTransition.current.keyName -cne
                'Reborn-Network-Manifest-Development-Current-v1' -or
            $keyTransition.next.keyName -cne
                'Reborn-Network-Manifest-Development-Next-v1' -or
            $keyTransition.current.algorithm -cne 'ECDSA_P256' -or
            $keyTransition.next.algorithm -cne 'ECDSA_P256' -or
            $keyTransition.current.keyUsage -cne 'Signing' -or
            $keyTransition.next.keyUsage -cne 'Signing' -or
            $keyTransition.current.exportPolicy -cne 'None' -or
            $keyTransition.next.exportPolicy -cne 'None' -or
            @($coordinateLengths | Where-Object { $_ -ne 32 }).Count -ne 0 -or
            $keyTransition.current.x -cne $manifestTrust.x -or
            $keyTransition.current.y -cne $manifestTrust.y -or
            $keyTransition.current.keyName -cne
                $manifestTrust.cngKeyName -or
            $keyTransition.current.removed -isnot [bool] -or
            $keyTransition.next.removed -isnot [bool] -or
            ($keyTransition.next.removed -and
                -not $keyTransition.current.removed) -or
            ($keyTransition.state -eq 'Removed' -and (
                    -not $keyTransition.current.removed -or
                    -not $keyTransition.next.removed))
        ) {
            throw (
                'Manifest key receipt changed outside its cleanup ' +
                'transaction.')
        }
    }
    return $receipt
}

function Assert-RebornControlledHostCleanupAuthorization {
    param(
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$InventoryReceiptPath,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedInventoryReceiptSha256
    )

    $client =
        Assert-RebornControlledHostCleanupClientRoot $ClientRoot
    if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
        throw 'Origin.exe must be closed before controlled-host cleanup.'
    }

    $runtimeAuthority =
        Get-RebornControlledHostCleanupRuntimeAuthority $RuntimeRoot
    $inventory =
        Read-RebornControlledHostClientInventoryReceipt `
            $InventoryReceiptPath `
            $ExpectedInventoryReceiptSha256
    $recordedRoot = [IO.Path]::GetFullPath(
        [string]$inventory.Record.clientRoot).TrimEnd('\')
    if (-not $recordedRoot.Equals(
            $script:ExpectedClientRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'Protected inventory receipt does not name the exact ' +
            'disposable controlled client.')
    }
    $stock = Assert-RebornControlledHostClientInventoryReceipt `
        $inventory $client Stock

    $expectedEnvironment = [UInt64](
        $runtimeAuthority.Record.activationEnvironment)
    $expectedFloor = [UInt64](
        $runtimeAuthority.Record.activationSequenceFloor)
    $activation = Assert-RebornProtectedHklmActivationState
    if (
        -not $activation.Exists -or
        -not $activation.Complete -or
        [UInt64]$activation.Mode -ne 0 -or
        [UInt64]$activation.Environment -ne $expectedEnvironment -or
        [UInt64]$activation.SequenceFloor -ne $expectedFloor
    ) {
        throw (
            'Protected HKLM activation does not exactly match the ' +
            'signed-manifest, receipt-bound disabled state.')
    }

    [pscustomobject]@{
        RuntimeRoot = $runtimeAuthority.RuntimeRoot
        ClientRoot = $client
        InventoryReceiptPath = $stock.ReceiptPath
        InventoryReceiptSha256 = $stock.ReceiptSha256
        ActivationEnvironment = $expectedEnvironment
        ActivationSequenceFloor = $expectedFloor
    }
}

function Assert-RebornControlledHostFinalCleanupReceiptState {
    param(
        [Parameter(Mandatory)][object]$TrustRecord,
        [Parameter(Mandatory)][object]$ManifestKeyRecord,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedManifestTrustSha256
    )

    if (
        $TrustRecord.state -cne 'Removed' -or
        $TrustRecord.installedByScript -isnot [bool] -or
        -not $TrustRecord.installedByScript
    ) {
        throw (
            'Final runtime cleanup requires the exact script-installed ' +
            'development root receipt in Removed state.')
    }
    if (
        $ManifestKeyRecord.schemaVersion -ne 1 -or
        $ManifestKeyRecord.state -cne 'Removed' -or
        $ManifestKeyRecord.current.removed -isnot [bool] -or
        $ManifestKeyRecord.next.removed -isnot [bool] -or
        -not $ManifestKeyRecord.current.removed -or
        -not $ManifestKeyRecord.next.removed -or
        $ManifestKeyRecord.current.keyName -cne
            'Reborn-Network-Manifest-Development-Current-v1' -or
        $ManifestKeyRecord.next.keyName -cne
            'Reborn-Network-Manifest-Development-Next-v1' -or
        $ManifestKeyRecord.currentTrustSha256 -cne
            $ExpectedManifestTrustSha256
    ) {
        throw (
            'Final runtime cleanup requires the exact development ' +
            'manifest-key receipt with both keys Removed.')
    }
}

function Assert-RebornControlledHostFinalCleanupResourceAbsence {
    param(
        [Parameter(Mandatory)][bool]$RootCertificatePresent,
        [Parameter(Mandatory)][bool]$CurrentManifestKeyPresent,
        [Parameter(Mandatory)][bool]$NextManifestKeyPresent
    )

    if ($RootCertificatePresent) {
        throw (
            'Final runtime cleanup requires the exact issued development ' +
            'root to be absent from CurrentUser Root.')
    }
    if ($CurrentManifestKeyPresent -or $NextManifestKeyPresent) {
        throw (
            'Final runtime cleanup requires both exact issued development ' +
            'manifest keys to be absent.')
    }
}

function Assert-RebornControlledHostFinalCleanupDependencies {
    param(
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$InventoryReceiptPath,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedInventoryReceiptSha256
    )

    $authorization = Assert-RebornControlledHostCleanupAuthorization `
        $RuntimeRoot `
        $ClientRoot `
        $InventoryReceiptPath `
        $ExpectedInventoryReceiptSha256
    $runtime = $authorization.RuntimeRoot
    $runtimeReceipt = Read-RebornControlledHostRuntimeReceipt (
        Join-Path $runtime 'receipt.json')
    $tls = Assert-RebornControlledHostTlsDirectoryEntries (
        Join-Path $runtime 'tls')
    $bundle = Assert-RebornControlledHostBundleDirectoryEntries (
        Join-Path $runtime 'bundle')

    $trust = Read-RebornTrustReceipt (
        $tls.'current-user-trust-receipt.json')

    $keyReceiptPath =
        $bundle.'development-manifest-key-receipt.json'
    $keyItem = Get-Item -LiteralPath $keyReceiptPath -Force
    if ($keyItem.Length -lt 128 -or $keyItem.Length -gt 16384) {
        throw 'Final manifest key cleanup receipt exceeds its bounded size.'
    }
    try {
        $keyRecord = Get-Content -LiteralPath $keyItem.FullName -Raw |
            ConvertFrom-Json
    }
    catch {
        throw 'Final manifest key cleanup receipt is not valid JSON.'
    }
    Assert-RebornControlledHostFinalCleanupReceiptState `
        $trust.Record `
        $keyRecord `
        ([string]$runtimeReceipt.Record.manifestTrustSha256)

    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $rootPresent = $false
    try {
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::
                FindByThumbprint,
            [string]$trust.Record.thumbprint,
            $false)
        $rootPresent = $matches.Count -ne 0
    }
    finally {
        $store.Close()
        $store.Dispose()
    }
    $provider =
        [Security.Cryptography.CngProvider]::
            MicrosoftSoftwareKeyStorageProvider
    $openOptions =
        [Security.Cryptography.CngKeyOpenOptions]::None
    $currentKeyPresent = [Security.Cryptography.CngKey]::Exists(
        [string]$keyRecord.current.keyName,
        $provider,
        $openOptions)
    $nextKeyPresent = [Security.Cryptography.CngKey]::Exists(
        [string]$keyRecord.next.keyName,
        $provider,
        $openOptions)
    Assert-RebornControlledHostFinalCleanupResourceAbsence `
        $rootPresent $currentKeyPresent $nextKeyPresent

    [pscustomobject]@{
        RuntimeRoot = $runtime
        ClientRoot = $authorization.ClientRoot
        InventoryReceiptPath =
            $authorization.InventoryReceiptPath
        InventoryReceiptSha256 =
            $authorization.InventoryReceiptSha256
        ActivationEnvironment =
            $authorization.ActivationEnvironment
        ActivationSequenceFloor =
            $authorization.ActivationSequenceFloor
        TrustReceiptPath = $trust.Path
        TrustReceiptSha256 = (
            Get-FileHash -LiteralPath $trust.Path -Algorithm SHA256
        ).Hash
        TrustRootThumbprint =
            [string]$trust.Record.thumbprint
        TrustRootSha256 =
            [string]$trust.Record.rootSha256
        ManifestKeyReceiptPath = $keyItem.FullName
        ManifestKeyReceiptSha256 = (
            Get-FileHash -LiteralPath $keyItem.FullName -Algorithm SHA256
        ).Hash
        ManifestCurrentKeyName =
            [string]$keyRecord.current.keyName
        ManifestNextKeyName =
            [string]$keyRecord.next.keyName
        ManifestCurrentTrustSha256 =
            [string]$keyRecord.currentTrustSha256
        ManifestNextTrustSha256 =
            [string]$keyRecord.nextTrustSha256
    }
}

Export-ModuleMember -Function @(
    'Assert-RebornControlledHostCleanupClientRoot',
    'Get-RebornControlledHostCleanupRuntimeAuthority',
    'Assert-RebornControlledHostCleanupAuthorization',
    'Assert-RebornControlledHostFinalCleanupDependencies'
)
