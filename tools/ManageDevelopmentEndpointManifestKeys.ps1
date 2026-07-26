[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status','Create','IssueReceipt','ValidateReceipt','Remove')]
    [string]$Mode = 'Status',

    [string]$CurrentKeyName =
        'Reborn-Network-Manifest-Development-Current-v1',

    [string]$NextKeyName =
        'Reborn-Network-Manifest-Development-Next-v1',

    [string]$HeaderPath = (
        Join-Path $PSScriptRoot `
            '..\client\network-shim\src\SecureClientManifestDevelopmentKeys.generated.h'),

    [string]$TrustPath = (
        Join-Path $PSScriptRoot `
            '..\artifacts\secure-network\development-manifest-trust.json'),

    [string]$NextTrustPath = (
        Join-Path $PSScriptRoot `
            '..\artifacts\secure-network\development-manifest-next-trust.json'),

    [string]$ReceiptPath = (
        Join-Path $PSScriptRoot `
            '..\artifacts\secure-network\development-manifest-key-receipt.json'),

    [string]$ClientRoot = 'C:\RebornNetworkAcceptanceClient',

    [string]$ClientInventoryReceiptPath,

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedClientInventoryReceiptSha256,

    [string]$RuntimeRoot,

    [switch]$AllowReceiptIssue,

    [switch]$AllowKeyRemoval,

    [switch]$AllowTestPath,

    [switch]$AllowTestKeyNames,

    [ValidateSet('None', 'AfterFirstKeyDelete')]
    [string]$TestFailurePoint = 'None'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$fixedCurrentKeyName =
    'Reborn-Network-Manifest-Development-Current-v1'
$fixedNextKeyName =
    'Reborn-Network-Manifest-Development-Next-v1'
$issuedHeader = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot (
        '..\client\network-shim\src\' +
        'SecureClientManifestDevelopmentKeys.generated.h')))
$issuedTrust = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot (
        '..\artifacts\secure-network\' +
        'development-manifest-trust.json')))
$issuedNextTrust = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot (
        '..\artifacts\secure-network\' +
        'development-manifest-next-trust.json')))
$issuedReceipt = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot (
        '..\artifacts\secure-network\' +
        'development-manifest-key-receipt.json')))

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentEndpointManifestKeyReceipt.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentEndpointManifestKeyGeneration.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force

$provider =
    [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
$openOptions = [Security.Cryptography.CngKeyOpenOptions]::None

function Test-KeyExists {
    param([string]$Name)
    return [Security.Cryptography.CngKey]::Exists(
        $Name,
        $provider,
        $openOptions)
}

function Open-Key {
    param([string]$Name)
    return [Security.Cryptography.CngKey]::Open(
        $Name,
        $provider,
        $openOptions)
}

function Get-KeyStatus {
    param([string]$Name)

    if (-not (Test-KeyExists $Name)) {
        return [pscustomobject]@{
            Exists = $false
            Valid = $false
            Exportable = $false
        }
    }
    $key = Open-Key $Name
    try {
        $exportable =
            $key.ExportPolicy -ne
                [Security.Cryptography.CngExportPolicies]::None
        return [pscustomobject]@{
            Exists = $true
            Valid = (
                $key.Algorithm.Algorithm -ceq 'ECDSA_P256' -and
                ($key.KeyUsage -band
                    [Security.Cryptography.CngKeyUsages]::Signing) -ne 0 -and
                -not $exportable)
            Exportable = $exportable
        }
    }
    finally {
        $key.Dispose()
    }
}

function Get-KeyDescriptor {
    param([string]$Name)

    $key = Open-Key $Name
    try {
        $coordinates = Get-PublicCoordinates $key
        try {
            [pscustomobject]@{
                Name = $Name
                Algorithm = $key.Algorithm.Algorithm
                KeyUsage = $key.KeyUsage.ToString()
                ExportPolicy = $key.ExportPolicy.ToString()
                X = [Convert]::ToBase64String($coordinates.X)
                Y = [Convert]::ToBase64String($coordinates.Y)
            }
        }
        finally {
            [Array]::Clear(
                $coordinates.X, 0, $coordinates.X.Length)
            [Array]::Clear(
                $coordinates.Y, 0, $coordinates.Y.Length)
        }
    }
    finally {
        $key.Dispose()
    }
}

function Assert-KeyManagementPaths {
    $resolvedHeader = [IO.Path]::GetFullPath($HeaderPath)
    $resolvedTrust = [IO.Path]::GetFullPath($TrustPath)
    $resolvedNextTrust = [IO.Path]::GetFullPath($NextTrustPath)
    $resolvedReceipt = [IO.Path]::GetFullPath($ReceiptPath)
    if ($AllowTestKeyNames -and -not $AllowTestPath) {
        throw 'Test key names require explicit -AllowTestPath.'
    }
    if ($TestFailurePoint -ne 'None' -and -not $AllowTestPath) {
        throw 'Key-removal fault injection requires -AllowTestPath.'
    }
    if (-not $AllowTestKeyNames -and (
            $CurrentKeyName -cne $fixedCurrentKeyName -or
            $NextKeyName -cne $fixedNextKeyName)) {
        throw 'Only the two exact development manifest key names are allowed.'
    }
    if ($AllowTestPath) {
        $temporary = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        foreach ($path in @(
            $resolvedHeader,
            $resolvedTrust,
            $resolvedNextTrust,
            $resolvedReceipt
        )) {
            if (-not $path.StartsWith(
                    $temporary,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Test key artifacts must remain under the temp directory.'
            }
        }
        return
    }
    if (
        $resolvedHeader -cne $issuedHeader -or
        $resolvedTrust -cne $issuedTrust -or
        $resolvedNextTrust -cne $issuedNextTrust
    ) {
        throw 'Production manifest key artifacts must use issued paths.'
    }
    if ($Mode -in @('Create', 'IssueReceipt') -and
        $resolvedReceipt -cne $issuedReceipt) {
        throw 'Receipt issuance must use the exact original issued path.'
    }
}

function New-SigningKey {
    param([string]$Name)

    $parameters =
        [Security.Cryptography.CngKeyCreationParameters]::new()
    $parameters.Provider = $provider
    $parameters.ExportPolicy =
        [Security.Cryptography.CngExportPolicies]::None
    $parameters.KeyUsage =
        [Security.Cryptography.CngKeyUsages]::Signing
    $parameters.KeyCreationOptions =
        [Security.Cryptography.CngKeyCreationOptions]::None
    return [Security.Cryptography.CngKey]::Create(
        [Security.Cryptography.CngAlgorithm]::ECDsaP256,
        $Name,
        $parameters)
}

function Get-PublicCoordinates {
    param([Security.Cryptography.CngKey]$Key)

    $blob = $Key.Export(
        [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    try {
        if ($blob.Length -ne 72 -or
            [Text.Encoding]::ASCII.GetString($blob, 0, 4) -cne 'ECS1') {
            throw 'The manifest key is not an ECDSA P-256 public key.'
        }
        $x = New-Object byte[] 32
        $y = New-Object byte[] 32
        [Array]::Copy($blob, 8, $x, 0, 32)
        [Array]::Copy($blob, 40, $y, 0, 32)
        return [pscustomobject]@{ X = $x; Y = $y }
    }
    finally {
        [Array]::Clear($blob, 0, $blob.Length)
    }
}

function Resolve-RemovalRuntimeRoot {
    if ($AllowTestPath) {
        return $null
    }
    $resolvedReceipt = [IO.Path]::GetFullPath($ReceiptPath)
    $programDataRuntime = [IO.Path]::GetFullPath(
        (Join-Path $env:ProgramData 'RebornSecureNetworkRuntime')
    ).TrimEnd('\')
    if ($resolvedReceipt.Equals(
            $issuedReceipt,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'Controlled-host Remove requires the exact protected staged ' +
            'receipt; the original receipt is issuance-only.')
    }
    $candidateRoot = if (-not [string]::IsNullOrWhiteSpace($RuntimeRoot)) {
        [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\')
    } else {
        $bundle = Split-Path -Parent $resolvedReceipt
        if ([IO.Path]::GetFileName($bundle) -cne 'bundle' -or
            [IO.Path]::GetFileName($resolvedReceipt) -cne
                'development-manifest-key-receipt.json') {
            throw 'Remove receipt is not an exact protected staged copy.'
        }
        Split-Path -Parent $bundle
    }
    if (-not $candidateRoot.StartsWith(
            $programDataRuntime + '\',
            [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($candidateRoot) -cnotmatch
            '^\d{8}-\d{6}$') {
        throw 'RuntimeRoot is outside the issued controlled-host scope.'
    }
    $expectedStaged = Join-Path $candidateRoot (
        'bundle\development-manifest-key-receipt.json')
    if (
        -not $resolvedReceipt.Equals(
            $expectedStaged,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Remove receipt is outside the exact runtime bundle scope.'
    }
    Assert-RebornProtectedDirectoryPath `
        $candidateRoot 'controlled-host runtime root' `
        -ProtectContents -RequireProtectedAcl | Out-Null
    Assert-RebornRegularFilePath `
        $resolvedReceipt 'manifest key receipt' | Out-Null
    return $candidateRoot
}

function Assert-BundleRestoredBeforeKeyRemoval {
    if ($AllowTestPath) {
        return
    }
    if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
        throw 'Origin.exe must be closed before manifest key removal.'
    }
    $client = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
    if (-not $client.Equals(
            'C:\RebornNetworkAcceptanceClient',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Key removal requires the exact disposable controlled client.'
    }
    if (
        (Test-Path -LiteralPath (Join-Path $client 'NetLegacy.dll')) -or
        (Test-Path -LiteralPath (
            Join-Path $client 'RebornNetwork.gwem'))
    ) {
        throw (
            'Restore the secure client bundle before deleting manifest ' +
            'verification keys.')
    }
}

function Read-ValidatedKeyRemovalAuthority {
    $artifacts = Get-RebornManifestKeyArtifactBinding `
        $HeaderPath $TrustPath $NextTrustPath `
        $CurrentKeyName $NextKeyName
    $loaded = Read-RebornManifestKeyReceipt `
        $ReceiptPath $artifacts $CurrentKeyName $NextKeyName
    foreach ($slot in @(
        @('current', $CurrentKeyName,
            $artifacts.CurrentX, $artifacts.CurrentY),
        @('next', $NextKeyName,
            $artifacts.NextX, $artifacts.NextY)
    )) {
        $exists = Test-KeyExists $slot[1]
        if ($exists) {
            Assert-RebornManifestKeyDescriptor `
                (Get-KeyDescriptor $slot[1]) `
                $slot[1] $slot[2] $slot[3]
        } elseif ($loaded.Record.state -eq 'Issued') {
            throw (
                "Key $($slot[1]) is absent without a durable " +
                'partial-removal receipt.')
        }
    }
    return $loaded
}

Assert-KeyManagementPaths
$currentExists = Test-KeyExists $CurrentKeyName
$nextExists = Test-KeyExists $NextKeyName
if ($Mode -eq 'Status') {
    $currentStatus = Get-KeyStatus $CurrentKeyName
    $nextStatus = Get-KeyStatus $NextKeyName
    [pscustomobject]@{
        CurrentKeyName = $CurrentKeyName
        CurrentExists = $currentStatus.Exists
        CurrentValid = $currentStatus.Valid
        NextKeyName = $NextKeyName
        NextExists = $nextStatus.Exists
        NextValid = $nextStatus.Valid
        HeaderPath = [IO.Path]::GetFullPath($HeaderPath)
        TrustPath = [IO.Path]::GetFullPath($TrustPath)
        NextTrustPath = [IO.Path]::GetFullPath($NextTrustPath)
        ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
        ReceiptExists =
            Test-Path -LiteralPath $ReceiptPath -PathType Leaf
        PrivateKeysExportable = (
            $currentStatus.Exportable -or
            $nextStatus.Exportable)
    }
    return
}

if ($Mode -eq 'ValidateReceipt') {
    if (-not $AllowTestPath -and
        -not ([IO.Path]::GetFullPath($ReceiptPath)).Equals(
            $issuedReceipt,[StringComparison]::OrdinalIgnoreCase)) {
        Resolve-RemovalRuntimeRoot | Out-Null
    }
    $loaded = Read-ValidatedKeyRemovalAuthority
    [pscustomobject]@{
        Result = 'Validated'
        ReceiptPath = $loaded.Path
        ReceiptState = [string]$loaded.Record.state
        PublicCoordinatesBound = $true
        PrivateKeysExportable = $false
    }
    return
}

if ($Mode -eq 'IssueReceipt') {
    if (-not $AllowReceiptIssue) {
        throw 'IssueReceipt requires explicit -AllowReceiptIssue.'
    }
    if (-not $currentExists -or -not $nextExists) {
        throw 'IssueReceipt requires both existing development keys.'
    }
    $artifacts = Get-RebornManifestKeyArtifactBinding `
        $HeaderPath $TrustPath $NextTrustPath `
        $CurrentKeyName $NextKeyName
    $currentDescriptor = Get-KeyDescriptor $CurrentKeyName
    $nextDescriptor = Get-KeyDescriptor $NextKeyName
    $record = New-RebornManifestKeyReceiptRecord `
        $artifacts $currentDescriptor $nextDescriptor
    if (-not $PSCmdlet.ShouldProcess(
            [IO.Path]::GetFullPath($ReceiptPath),
            'Issue removal authority for the two existing keys')) {
        return
    }
    Write-RebornManifestKeyReceiptAtomic `
        $record $ReceiptPath -NoOverwrite
    Read-RebornManifestKeyReceipt `
        $ReceiptPath $artifacts $CurrentKeyName $NextKeyName |
        Out-Null
    [pscustomobject]@{
        Result = 'ReceiptIssued'
        ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
        KeysMutated = $false
    }
    return
}

if ($Mode -eq 'Remove') {
    if (-not $AllowKeyRemoval) {
        throw 'Remove requires explicit -AllowKeyRemoval.'
    }
    Assert-BundleRestoredBeforeKeyRemoval
    $runtime = Resolve-RemovalRuntimeRoot
    if (-not $AllowTestPath) {
        if ([string]::IsNullOrWhiteSpace(
                $ClientInventoryReceiptPath) -or
            [string]::IsNullOrWhiteSpace(
                $ExpectedClientInventoryReceiptSha256)) {
            throw (
                'Key removal requires the protected stock client ' +
                'inventory receipt and its expected SHA-256.')
        }
        Import-Module (
            Join-Path $PSScriptRoot (
                'ControlledHostCleanupAuthorization.psm1')
        ) -Force
        Assert-RebornControlledHostCleanupAuthorization `
            $runtime $ClientRoot `
            $ClientInventoryReceiptPath `
            $ExpectedClientInventoryReceiptSha256 | Out-Null
    }
    $loaded = Read-ValidatedKeyRemovalAuthority
    if (-not $PSCmdlet.ShouldProcess(
            'CurrentUser CNG key store',
            "Delete development keys $CurrentKeyName and $NextKeyName")) {
        return
    }
    $runtimeLock = $null
    $bundleLock = $null
    try {
        if (-not $AllowTestPath) {
            Import-Module (
                Join-Path $PSScriptRoot 'SecureNetworkOperationLock.psm1'
            ) -Force
            Import-Module (
                Join-Path $PSScriptRoot 'ControlledHostRuntimeLock.psm1'
            ) -Force
            $bundleLock = Enter-RebornSecureNetworkOperationLock `
                -Name 'secure-bundle'
            $runtimeLock = Enter-RebornControlledHostRuntimeLock `
                -RuntimeRoot $runtime `
                -Purpose 'manifest key cleanup'
            Assert-RebornControlledHostCleanupAuthorization `
                $runtime $ClientRoot `
                $ClientInventoryReceiptPath `
                $ExpectedClientInventoryReceiptSha256 | Out-Null
        }
        $loaded = Read-ValidatedKeyRemovalAuthority
        if ($loaded.Record.state -eq 'Issued') {
            $loaded.Record.state = 'RemovalPending'
            $loaded.Record.removalStartedUtc =
                [DateTimeOffset]::UtcNow.ToString('O')
            Write-RebornManifestKeyReceiptAtomic `
                $loaded.Record $loaded.Path
        }
        foreach ($slot in @(
            @('current', $CurrentKeyName),
            @('next', $NextKeyName)
        )) {
            $deleted = $false
            if (Test-KeyExists $slot[1]) {
                $key = Open-Key $slot[1]
                try {
                    $key.Delete()
                    $deleted = $true
                }
                finally {
                    $key.Dispose()
                }
            }
            if (Test-KeyExists $slot[1]) {
                throw "Key deletion did not remove $($slot[1])."
            }
            if ($deleted -and
                $slot[0] -ceq 'current' -and
                $TestFailurePoint -ceq 'AfterFirstKeyDelete') {
                throw 'Simulated interruption after first key deletion.'
            }
            $loaded.Record.($slot[0]).removed = $true
            Write-RebornManifestKeyReceiptAtomic `
                $loaded.Record $loaded.Path
        }
        $loaded.Record.state = 'Removed'
        $loaded.Record.removedUtc =
            [DateTimeOffset]::UtcNow.ToString('O')
        Write-RebornManifestKeyReceiptAtomic `
            $loaded.Record $loaded.Path
        [pscustomobject]@{
            Result = 'Removed'
            ReceiptPath = $loaded.Path
        }
    }
    finally {
        if ($null -ne $runtimeLock) {
            Exit-RebornControlledHostRuntimeLock $runtimeLock
        }
        if ($null -ne $bundleLock) {
            Exit-RebornSecureNetworkOperationLock $bundleLock
        }
    }
    return
}

if ($currentExists -or $nextExists) {
    throw 'Create refuses to overwrite either existing development key.'
}
if (-not $PSCmdlet.ShouldProcess(
        'CurrentUser CNG key store',
        'Create two non-exportable ECDSA P-256 development signing keys')) {
    return
}

$currentKey = $null
$nextKey = $null
$artifactSnapshots = @(
    Get-RebornManifestKeyArtifactSnapshot $HeaderPath
    Get-RebornManifestKeyArtifactSnapshot $TrustPath
    Get-RebornManifestKeyArtifactSnapshot $NextTrustPath
    Get-RebornManifestKeyArtifactSnapshot $ReceiptPath
)
try {
    $currentKey = New-SigningKey $CurrentKeyName
    $nextKey = New-SigningKey $NextKeyName
    Write-RebornManifestKeyPublicArtifacts `
        (Get-PublicCoordinates $currentKey) `
        (Get-PublicCoordinates $nextKey) `
        $CurrentKeyName $NextKeyName `
        $HeaderPath $TrustPath $NextTrustPath
    $artifacts = Get-RebornManifestKeyArtifactBinding `
        $HeaderPath $TrustPath $NextTrustPath `
        $CurrentKeyName $NextKeyName
    $record = New-RebornManifestKeyReceiptRecord `
        $artifacts `
        (Get-KeyDescriptor $CurrentKeyName) `
        (Get-KeyDescriptor $NextKeyName)
    Write-RebornManifestKeyReceiptAtomic `
        $record $ReceiptPath -NoOverwrite
}
catch {
    foreach ($key in @($nextKey, $currentKey)) {
        if ($null -ne $key) {
            try { $key.Delete() } catch {}
        }
    }
    foreach ($snapshot in $artifactSnapshots) {
        try {
            Restore-RebornManifestKeyArtifactSnapshot $snapshot
        } catch {}
    }
    throw
}
finally {
    if ($null -ne $nextKey) { $nextKey.Dispose() }
    if ($null -ne $currentKey) { $currentKey.Dispose() }
}

[pscustomobject]@{
    Result = 'Created'
    CurrentKeyName = $CurrentKeyName
    NextKeyName = $NextKeyName
    HeaderPath = [IO.Path]::GetFullPath($HeaderPath)
    TrustPath = [IO.Path]::GetFullPath($TrustPath)
    NextTrustPath = [IO.Path]::GetFullPath($NextTrustPath)
    ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
    PrivateKeysExportable = $false
}
