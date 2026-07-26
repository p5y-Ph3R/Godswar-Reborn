[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentNetworkTrustReceipt.psm1'
) -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Message)
    $text = $null
    try {
        & $Action | Out-Null
    }
    catch {
        $text = $_.Exception.Message
    }
    Assert-True (
        $null -ne $text -and $text -match $Pattern
    ) "$Message; error was: $text"
}

$root = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-trust-receipt-test-$([guid]::NewGuid().ToString('N'))")

function New-TrustFixture {
    param([string]$Name)

    $directory = Join-Path $root $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $rootCer = Join-Path $directory 'reborn-development-root.cer'
    $pfx = Join-Path $directory 'reborn-development-server.pfx'
    $receipt = Join-Path $directory 'current-user-trust-receipt.json'
    $key = [Security.Cryptography.ECDsa]::Create(
        [Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $certificate = $null
    try {
        $request =
            [Security.Cryptography.X509Certificates.CertificateRequest]::new(
                'CN=Reborn Development Root CA',
                $key,
                [Security.Cryptography.HashAlgorithmName]::SHA256)
        $request.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
                $true, $false, 0, $true))
        $request.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                (
                    [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
                    [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign
                ),
                $true))
        $certificate = $request.CreateSelfSigned(
            [DateTimeOffset]::UtcNow.AddMinutes(-5),
            [DateTimeOffset]::UtcNow.AddDays(1))
        [IO.File]::WriteAllBytes(
            $rootCer,
            $certificate.Export(
                [Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        [IO.File]::WriteAllBytes(
            $pfx,
            [Text.Encoding]::UTF8.GetBytes(
                "hash-bound-dummy-pfx-$([Guid]::NewGuid().ToString('N'))"))
        $descriptor =
            Get-RebornTrustCertificateDescriptor $certificate
    }
    finally {
        if ($null -ne $certificate) {
            $certificate.Dispose()
        }
        $key.Dispose()
    }
    [pscustomobject]@{
        Directory = $directory
        RootCer = $rootCer
        Pfx = $pfx
        Receipt = $receipt
        Descriptor = $descriptor
        RootFileSha256 = Get-RebornTrustFileSha256 $rootCer
        PfxSha256 = Get-RebornTrustFileSha256 $pfx
    }
}

function New-InstallRecord {
    param([object]$Fixture)
    New-RebornTrustReceiptRecord `
        $Fixture.Descriptor `
        $Fixture.RootFileSha256 `
        $Fixture.PfxSha256 `
        $false `
        'NoChange'
}

[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $successful = New-TrustFixture 'successful-install-remove'
    $storeState = [pscustomobject]@{ Installed = $false }
    $add = { $storeState.Installed = $true }
    $find = {
        if ($storeState.Installed) {
            return $successful.Descriptor
        }
        return $null
    }
    $remove = { $storeState.Installed = $false }
    $installed = Invoke-RebornTrustInstallReceiptTransaction `
        (New-InstallRecord $successful) `
        $successful.Receipt `
        $add `
        $find
    Assert-True (
        $installed.Record.state -eq 'Installed' -and
        $installed.Record.installedByScript -is [bool] -and
        $installed.Record.installedByScript -and
        $storeState.Installed
    ) 'durable install transaction did not reach Installed'
    $removed = Invoke-RebornTrustRemovalTransaction `
        $installed $find $remove
    Assert-True (
        $removed.Result -eq 'Removed' -and
        $removed.Loaded.Record.state -eq 'Removed' -and
        -not $storeState.Installed
    ) 'successful removal did not durably reach Removed'

    $pendingOnly = New-TrustFixture 'pending-before-add'
    $pendingState = [pscustomobject]@{ Installed = $false }
    Assert-Throws {
        Invoke-RebornTrustInstallReceiptTransaction `
            (New-InstallRecord $pendingOnly) `
            $pendingOnly.Receipt `
            { $pendingState.Installed = $true } `
            {
                if ($pendingState.Installed) {
                    return $pendingOnly.Descriptor
                }
                return $null
            } `
            -FailurePoint AfterPendingReceipt
    } 'pending trust receipt' (
        'pending-install interruption was not injected')
    $pendingLoaded = Read-RebornTrustReceipt $pendingOnly.Receipt
    $pendingRemoved = Invoke-RebornTrustRemovalTransaction `
        $pendingLoaded `
        {
            if ($pendingState.Installed) {
                return $pendingOnly.Descriptor
            }
            return $null
        } `
        { $pendingState.Installed = $false }
    Assert-True (
        $pendingRemoved.Result -eq 'AlreadyAbsent' -and
        $pendingRemoved.Loaded.Record.state -eq 'Removed'
    ) 'PendingInstall-before-Add was not safely reconciled'

    $afterAdd = New-TrustFixture 'interrupted-after-add'
    $afterAddState = [pscustomobject]@{ Installed = $false }
    Assert-Throws {
        Invoke-RebornTrustInstallReceiptTransaction `
            (New-InstallRecord $afterAdd) `
            $afterAdd.Receipt `
            { $afterAddState.Installed = $true } `
            {
                if ($afterAddState.Installed) {
                    return $afterAdd.Descriptor
                }
                return $null
            } `
            -FailurePoint AfterStoreAdd
    } 'after trust Store.Add' (
        'post-Store.Add interruption was not injected')
    $afterAddLoaded = Read-RebornTrustReceipt $afterAdd.Receipt
    $afterAddRemoved = Invoke-RebornTrustRemovalTransaction `
        $afterAddLoaded `
        {
            if ($afterAddState.Installed) {
                return $afterAdd.Descriptor
            }
            return $null
        } `
        { $afterAddState.Installed = $false }
    Assert-True (
        $afterAddRemoved.Result -eq 'Removed' -and
        -not $afterAddState.Installed
    ) 'PendingInstall-after-Add did not retain cleanup authority'

    $removeRetry = New-TrustFixture 'remove-retry'
    $retryState = [pscustomobject]@{ Installed = $true }
    $retryRecord = New-RebornTrustReceiptRecord `
        $removeRetry.Descriptor `
        $removeRetry.RootFileSha256 `
        $removeRetry.PfxSha256 `
        $true `
        'Installed'
    Write-RebornTrustReceiptAtomic `
        $retryRecord $removeRetry.Receipt -NoOverwrite
    $retryLoaded = Read-RebornTrustReceipt $removeRetry.Receipt
    Assert-Throws {
        Invoke-RebornTrustRemovalTransaction `
            $retryLoaded `
            {
                if ($retryState.Installed) {
                    return $removeRetry.Descriptor
                }
                return $null
            } `
            { $retryState.Installed = $false } `
            -FailurePoint AfterStoreRemove
    } 'after trust Store.Remove' (
        'post-Store.Remove interruption was not injected')
    $retryLoaded = Read-RebornTrustReceipt $removeRetry.Receipt
    $retryResult = Invoke-RebornTrustRemovalTransaction `
        $retryLoaded `
        {
            if ($retryState.Installed) {
                return $removeRetry.Descriptor
            }
            return $null
        } `
        { $retryState.Installed = $false }
    Assert-True (
        $retryResult.Result -eq 'AlreadyAbsent' -and
        $retryResult.Loaded.Record.state -eq 'Removed'
    ) 'RemovalPending did not reconcile after interrupted Store.Remove'

    $typeFixture = New-TrustFixture 'strict-bool'
    $typeRecord = New-RebornTrustReceiptRecord `
        $typeFixture.Descriptor `
        $typeFixture.RootFileSha256 `
        $typeFixture.PfxSha256 `
        $true `
        'Installed'
    $typeRecord.installedByScript = 'false'
    Write-RebornTrustReceiptAtomic `
        $typeRecord $typeFixture.Receipt -NoOverwrite
    Assert-Throws {
        Read-RebornTrustReceipt $typeFixture.Receipt
    } 'malformed' 'string installedByScript=false was accepted'

    $legacyFalse = [pscustomobject]@{
        version = 1
        storeLocation = 'CurrentUser'
        storeName = 'Root'
        thumbprint = $typeFixture.Descriptor.Thumbprint
        rootSha256 = $typeFixture.Descriptor.RootSha256
        installedByScript = 'false'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    Assert-Throws {
        ConvertFrom-RebornLegacyTrustReceipt `
            $legacyFalse `
            $typeFixture.Descriptor `
            $typeFixture.RootFileSha256 `
            $typeFixture.PfxSha256
    } 'cannot issue' 'legacy string false issued cleanup authority'

    $legacyTrue = [pscustomobject]@{
        version = 1
        storeLocation = 'CurrentUser'
        storeName = 'Root'
        thumbprint = $typeFixture.Descriptor.Thumbprint
        rootSha256 = $typeFixture.Descriptor.RootSha256
        installedByScript = $true
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $migrated = ConvertFrom-RebornLegacyTrustReceipt `
        $legacyTrue `
        $typeFixture.Descriptor `
        $typeFixture.RootFileSha256 `
        $typeFixture.PfxSha256
    Assert-True (
        $migrated.schemaVersion -eq 2 -and
        $migrated.state -ceq 'Installed' -and
        $migrated.installedByScript -is [bool] -and
        $migrated.installedByScript -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$migrated.migrationUtc)
    ) 'valid legacy receipt did not convert to durable schema 2'

    $truncated = New-TrustFixture 'truncated-receipt'
    $truncatedRecord = New-RebornTrustReceiptRecord `
        $truncated.Descriptor `
        $truncated.RootFileSha256 `
        $truncated.PfxSha256 `
        $true `
        'Installed'
    Write-RebornTrustReceiptAtomic `
        $truncatedRecord $truncated.Receipt -NoOverwrite
    [IO.File]::WriteAllText($truncated.Receipt, '{')
    Assert-Throws {
        Read-RebornTrustReceipt $truncated.Receipt
    } 'size|JSON' 'truncated receipt retained cleanup authority'

    $tampered = New-TrustFixture 'tampered-artifact'
    $tamperedRecord = New-RebornTrustReceiptRecord `
        $tampered.Descriptor `
        $tampered.RootFileSha256 `
        $tampered.PfxSha256 `
        $true `
        'Installed'
    Write-RebornTrustReceiptAtomic `
        $tamperedRecord $tampered.Receipt -NoOverwrite
    [IO.File]::AppendAllText($tampered.Pfx, 'tamper')
    Assert-Throws {
        Read-RebornTrustReceipt $tampered.Receipt
    } 'hash binding' 'tampered PFX retained cleanup authority'

    $whatIf = New-TrustFixture 'whatif-read-only'
    $whatIfRecord = New-RebornTrustReceiptRecord `
        $whatIf.Descriptor `
        $whatIf.RootFileSha256 `
        $whatIf.PfxSha256 `
        $true `
        'Installed'
    Write-RebornTrustReceiptAtomic `
        $whatIfRecord $whatIf.Receipt -NoOverwrite
    $whatIfReceiptBefore = (
        Get-FileHash $whatIf.Receipt -Algorithm SHA256).Hash
    $rootStore =
        [Security.Cryptography.X509Certificates.X509Store]::new(
            [Security.Cryptography.X509Certificates.StoreName]::Root,
            [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $rootStore.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $storeCountBefore = $rootStore.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $whatIf.Descriptor.Thumbprint,
            $false).Count
    }
    finally {
        $rootStore.Dispose()
    }
    & (Join-Path $PSScriptRoot 'RemoveDevelopmentNetworkTrust.ps1') `
        -Mode Remove `
        -ReceiptPath $whatIf.Receipt `
        -AllowTestPath `
        -AllowTrustRemoval `
        -WhatIf `
        -Confirm:$false | Out-Null
    $rootStore =
        [Security.Cryptography.X509Certificates.X509Store]::new(
            [Security.Cryptography.X509Certificates.StoreName]::Root,
            [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $rootStore.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $storeCountAfter = $rootStore.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $whatIf.Descriptor.Thumbprint,
            $false).Count
    }
    finally {
        $rootStore.Dispose()
    }
    Assert-True (
        (Get-FileHash $whatIf.Receipt -Algorithm SHA256).Hash -ceq
            $whatIfReceiptBefore -and
        $storeCountAfter -eq $storeCountBefore
    ) 'Remove -WhatIf changed the receipt or CurrentUser root store'

    Assert-Throws {
        & (Join-Path $PSScriptRoot 'RemoveDevelopmentNetworkTrust.ps1') `
            -Mode Status `
            -ReceiptPath (Join-Path $root 'arbitrary\receipt.json')
    } 'exact issued filename|protected runtime TLS scope' (
        'production trust cleanup accepted an arbitrary receipt path')

    [pscustomobject]@{
        Result = 'Passed'
        DurablePendingBeforeAdd = $true
        InterruptedAddCleanup = $true
        SuccessfulRemoval = $true
        InterruptedRemovalRetry = $true
        StrictBoolean = $true
        LegacyMigration = $true
        ReceiptLossOrTruncationRefusal = $true
        ArtifactTamperRefusal = $true
        WhatIfReadOnly = $true
        ProductionPathBinding = $true
        LiveStoreMutated = $false
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temporary = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith(
            $temporary,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unexpected test cleanup path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
