Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureEndpointManifestValidation.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force

$script:IssuedCampaignRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4Docker'
$script:RootSubject = 'CN=Reborn Development Root CA'
$script:MaximumDockerOutputBytes = 1MB
$script:MaximumReceiptBytes = 64KB
$script:MaximumReceiptRevisions = 256

function Get-RebornPhase4SecureDockerPins {
    [pscustomobject]@{
        ClientRoot = 'C:\RebornNetworkAcceptanceClient'
        CandidatePath =
            'C:\Reborn\client\network-shim\bin\Release\Win32\Net.dll'
        NativeChecksPath = (
            'C:\Reborn\client\network-shim\bin\Release\Win32\' +
            'Godswar.NetShim.Checks.exe')
        ManifestPath =
            'C:\Reborn\artifacts\secure-network\RebornNetwork.gwem'
        ManifestTrustPath = (
            'C:\Reborn\artifacts\secure-network\' +
            'development-manifest-trust.json')
        RootCertificatePath = (
            'C:\Reborn\artifacts\controlled-host-acceptance\' +
            '20260727-011921\tls\reborn-development-root.cer')
        ServerPfxPath = (
            'C:\Reborn\artifacts\controlled-host-acceptance\' +
            '20260727-011921\tls\reborn-development-server.pfx')
        SourceTrustReceiptPath = (
            'C:\Reborn\artifacts\controlled-host-acceptance\' +
            '20260727-011921\tls\current-user-trust-receipt.json')
        InventoryReceiptPath = (
            'C:\ProgramData\RebornSecureNetworkClientInventory\' +
            'client-stock-inventory-' +
            '6C076E54CE10B28D81F1EBBE22EA068B889DE71B06D3B2A04B03B367A9920FEB-' +
            '4eae4f12100e42d4ad131dea0b47ca27.json')
        OriginSha256 =
            '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
        StockNetSha256 =
            '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
        CandidateSha256 =
            '0328D7EA84B68DD8D5A1DF7B0A291B9DC17EF3337C0114A7A396283FC4EF852B'
        NativeChecksSha256 =
            'D583309B921C7AA795F7A044F096762703AA2DB376A1D07B9EEB4F44312208D0'
        ManifestSha256 =
            '3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C'
        ManifestTrustSha256 =
            'A32B40917A01D510504528F5D6996F918A6A218991B64C50234ED84C75C75C07'
        RootCertificateSha256 =
            '911E3CF444B631AAB9EDCC5980DF65243CAAC42B9000C5E2410C7DADFEB54DED'
        ServerPfxSha256 =
            'C498666CC8D6ECF09DF92C217169A6F2CDA788DEDA60E5DD17B1EA9CA6C6BC0F'
        SourceTrustReceiptSha256 =
            '57FF8F9D9A5701E6AB3E79C243F69D412DE30BA085F9DAD0EED473208748BCF4'
        InventoryReceiptSha256 =
            '978A7AA78F3898290F63994E2958004AF0026ADBD7EE3E66C0E6B4491FF71FE1'
        InventorySetSha256 =
            '6C076E54CE10B28D81F1EBBE22EA068B889DE71B06D3B2A04B03B367A9920FEB'
        OriginalHostsSha256 =
            '96B8714EAEB906C50EA8282A44C5A0A239BCAC1F723A89B5C4476957B496ADA3'
        RootThumbprint = 'C8FBF5F5B3DB9A50707ED70094C9C04F25039737'
        ManifestSequence = [UInt64]3
        ActivationEnvironment = [UInt64]1
        ServerContainer = 'godswar-server'
        PostgresContainer = 'godswar-postgres'
        DockerProfile = 'secure-hybrid'
        DockerNetwork = 'reborn_secure_runtime'
        DockerDatabase = 'godswar_secure_dev'
    }
}

function Get-RebornPhase4FileSha256 {
    param([Parameter(Mandatory)][string]$LiteralPath)

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Pinned Phase 4 input is absent: $LiteralPath"
    }
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

function Assert-RebornPhase4PinnedInputs {
    param([object]$Pins = (Get-RebornPhase4SecureDockerPins))

    foreach ($binding in @(
        @($Pins.CandidatePath, $Pins.CandidateSha256),
        @($Pins.NativeChecksPath, $Pins.NativeChecksSha256),
        @($Pins.ManifestPath, $Pins.ManifestSha256),
        @($Pins.ManifestTrustPath, $Pins.ManifestTrustSha256),
        @($Pins.RootCertificatePath, $Pins.RootCertificateSha256),
        @($Pins.ServerPfxPath, $Pins.ServerPfxSha256),
        @($Pins.SourceTrustReceiptPath, $Pins.SourceTrustReceiptSha256),
        @($Pins.InventoryReceiptPath, $Pins.InventoryReceiptSha256)
    )) {
        if ((Get-RebornPhase4FileSha256 $binding[0]) -cne $binding[1]) {
            throw "Pinned Phase 4 SHA-256 mismatch: $($binding[0])"
        }
    }

    $manifest = Read-RebornSecureEndpointManifest `
        -ManifestPath $Pins.ManifestPath `
        -TrustPath $Pins.ManifestTrustPath `
        -InstalledSequenceFloor $Pins.ManifestSequence
    if ([UInt64]$manifest.Sequence -ne $Pins.ManifestSequence -or
        [UInt64]$manifest.Environment -ne $Pins.ActivationEnvironment -or
        $manifest.TlsLoginHost -cne 'login.reborn.test' -or
        [UInt16]$manifest.TlsLoginPort -ne 6599) {
        throw 'Pinned Phase 4 endpoint manifest contract changed.'
    }

    $sourceReceipt =
        Get-Content -LiteralPath $Pins.SourceTrustReceiptPath -Raw |
            ConvertFrom-Json
    if ($sourceReceipt.schemaVersion -ne 2 -or
        $sourceReceipt.state -cne 'Installed' -or
        $sourceReceipt.installedByScript -isnot [bool] -or
        -not $sourceReceipt.installedByScript -or
        $sourceReceipt.subject -cne $script:RootSubject -or
        $sourceReceipt.thumbprint -cne $Pins.RootThumbprint -or
        $sourceReceipt.rootCertificateSha256 -cne
            $Pins.RootCertificateSha256 -or
        $sourceReceipt.serverPfxSha256 -cne $Pins.ServerPfxSha256) {
        throw 'Pinned Phase 4 source trust receipt contract changed.'
    }

    $root = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $Pins.RootCertificatePath)
    try {
        if ($root.HasPrivateKey -or
            $root.Subject -cne $script:RootSubject -or
            $root.Issuer -cne $script:RootSubject -or
            $root.Thumbprint -cne $Pins.RootThumbprint) {
            throw 'Pinned Phase 4 public root certificate is not exact.'
        }
    }
    finally {
        $root.Dispose()
    }
    return $manifest
}

function Grant-RebornPhase4CampaignReadAccess {
    param([Parameter(Mandatory)][string]$CampaignRoot)

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User -or
        $identity.User.Value -ceq 'S-1-5-18') {
        throw 'Phase 4 campaign read access requires an issued user.'
    }

    $security = Get-Acl -LiteralPath $CampaignRoot
    $inheritance =
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $identity.User,
        [Security.AccessControl.FileSystemRights]::ReadAndExecute,
        $inheritance,
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    $security.SetAccessRule($rule)
    Set-Acl -LiteralPath $CampaignRoot -AclObject $security

    Assert-RebornProtectedDirectoryPath `
        $CampaignRoot 'Phase 4 secure-Docker campaign root' `
        -ProtectContents -RequireProtectedAcl | Out-Null
}

function Resolve-RebornPhase4CampaignRoot {
    param(
        [string]$CampaignRoot = $script:IssuedCampaignRoot,
        [switch]$AllowTestPath,
        [switch]$Create
    )

    $resolved = [IO.Path]::GetFullPath($CampaignRoot).TrimEnd('\')
    if (-not $AllowTestPath -and
        -not $resolved.Equals(
            $script:IssuedCampaignRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Production Phase 4 campaign root is not the issued path.'
    }
    if ($Create) {
        if ($AllowTestPath) {
            [IO.Directory]::CreateDirectory($resolved) | Out-Null
        } else {
            Initialize-RebornProtectedDirectoryPath `
                $resolved 'Phase 4 secure-Docker campaign root' | Out-Null
            Grant-RebornPhase4CampaignReadAccess $resolved
        }
    } elseif (Test-Path -LiteralPath $resolved -PathType Container) {
        if (-not $AllowTestPath) {
            Assert-RebornProtectedDirectoryPath `
                $resolved 'Phase 4 secure-Docker campaign root' `
                -ProtectContents -RequireProtectedAcl | Out-Null
        }
    }
    return $resolved
}

function Get-RebornPhase4ReceiptSha256 {
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

function Write-RebornPhase4DurableFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][byte[]]$Bytes
    )

    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-RebornPhase4CampaignRecord {
    param(
        [Parameter(Mandatory)][object]$Record,
        [object]$Pins = (Get-RebornPhase4SecureDockerPins)
    )

    $states = @(
        'Preparing', 'TrustInstalled', 'HostsPending', 'HostsApplied',
        'BundlePending', 'BundleApplied', 'InstalledExact',
        'RestorePending', 'BundleRestored', 'HostsRestored',
        'TrustRemoved', 'Restored')
    $issuedUserSid =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($Record.schemaVersion -ne 1 -or
        $Record.mode -cne 'Phase4SecureDockerClientCampaign' -or
        $Record.state -notin $states -or
        [string]$Record.issuedUserSid -cne $issuedUserSid -or
        [string]$Record.clientRoot -cne $Pins.ClientRoot -or
        [string]$Record.candidateSha256 -cne $Pins.CandidateSha256 -or
        [string]$Record.manifestSha256 -cne $Pins.ManifestSha256 -or
        [string]$Record.manifestTrustSha256 -cne
            $Pins.ManifestTrustSha256 -or
        [string]$Record.rootCertificateSha256 -cne
            $Pins.RootCertificateSha256 -or
        [string]$Record.sourceTrustReceiptSha256 -cne
            $Pins.SourceTrustReceiptSha256 -or
        [string]$Record.inventoryReceiptSha256 -cne
            $Pins.InventoryReceiptSha256 -or
        [UInt64]$Record.manifestSequence -ne $Pins.ManifestSequence -or
        [UInt64]$Record.activationFloorBefore -ne
            $Pins.ManifestSequence -or
        [string]$Record.dockerProfile -cne $Pins.DockerProfile) {
        throw 'Phase 4 campaign receipt is outside its exact pinned policy.'
    }
    $campaignId = [Guid]::Empty
    if (-not [Guid]::TryParse([string]$Record.campaignId, [ref]$campaignId) -or
        $campaignId -eq [Guid]::Empty -or
        [UInt64]$Record.revision -eq 0) {
        throw 'Phase 4 campaign receipt identity is invalid.'
    }
}

function Read-RebornPhase4CampaignReceipt {
    param(
        [string]$CampaignRoot = $script:IssuedCampaignRoot,
        [switch]$AllowTestPath,
        [object]$Pins = (Get-RebornPhase4SecureDockerPins)
    )

    $root = Resolve-RebornPhase4CampaignRoot `
        $CampaignRoot -AllowTestPath:$AllowTestPath
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return $null
    }

    $latest = $null
    $jsonFiles = @(
        Get-ChildItem -LiteralPath $root -File -Filter 'handoff-*.json' |
            Sort-Object Name)
    if ($jsonFiles.Count -gt $script:MaximumReceiptRevisions) {
        throw 'Phase 4 campaign receipt revision bound was exceeded.'
    }
    foreach ($file in $jsonFiles) {
        if ($file.Name -notmatch '^handoff-(\d{6})\.json$') {
            throw "Unexpected Phase 4 receipt name: $($file.Name)"
        }
        $revision = [UInt64]$matches[1]
        $checksumPath = [IO.Path]::ChangeExtension(
            $file.FullName,
            '.sha256')
        if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
            continue
        }
        if ($file.Length -le 0 -or
            $file.Length -gt $script:MaximumReceiptBytes) {
            throw 'Phase 4 campaign receipt size is invalid.'
        }
        $expected = (
            Get-Content -LiteralPath $checksumPath -Raw).Trim()
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        try {
            if ($expected -notmatch '^[0-9A-F]{64}$' -or
                (Get-RebornPhase4ReceiptSha256 $bytes) -cne $expected) {
                throw 'Phase 4 campaign receipt checksum failed.'
            }
            $record = [Text.Encoding]::UTF8.GetString($bytes) |
                ConvertFrom-Json
        }
        finally {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
        Assert-RebornPhase4CampaignRecord $record $Pins
        if ([UInt64]$record.revision -ne $revision) {
            throw 'Phase 4 campaign receipt revision mismatch.'
        }
        $latest = [pscustomobject]@{
            Path = $file.FullName
            ChecksumPath = $checksumPath
            Record = $record
            Sha256 = $expected
        }
    }
    return $latest
}

function Write-RebornPhase4CampaignReceipt {
    param(
        [Parameter(Mandatory)][object]$Record,
        [string]$CampaignRoot = $script:IssuedCampaignRoot,
        [switch]$AllowTestPath,
        [object]$Pins = (Get-RebornPhase4SecureDockerPins)
    )

    $root = Resolve-RebornPhase4CampaignRoot `
        $CampaignRoot -AllowTestPath:$AllowTestPath -Create
    $all = @(
        Get-ChildItem -LiteralPath $root -File -Filter 'handoff-*.*')
    if ($all.Count -ge ($script:MaximumReceiptRevisions * 2)) {
        throw 'Phase 4 campaign receipt file bound was exceeded.'
    }
    $maximum = [UInt64]0
    foreach ($file in $all) {
        if ($file.Name -match '^handoff-(\d{6})\.(json|sha256)$') {
            $value = [UInt64]$matches[1]
            if ($value -gt $maximum) {
                $maximum = $value
            }
        }
    }
    $next = $maximum + 1
    $Record.revision = $next
    $Record.updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Assert-RebornPhase4CampaignRecord $Record $Pins
    $json = $Record | ConvertTo-Json -Depth 8
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    try {
        if ($bytes.Length -gt $script:MaximumReceiptBytes) {
            throw 'Phase 4 campaign receipt exceeds its size bound.'
        }
        $sha = Get-RebornPhase4ReceiptSha256 $bytes
        $base = Join-Path $root ('handoff-{0:D6}' -f $next)
        Write-RebornPhase4DurableFile "$base.json" $bytes
        $checksum = [Text.Encoding]::ASCII.GetBytes($sha)
        try {
            Write-RebornPhase4DurableFile "$base.sha256" $checksum
        }
        finally {
            [Array]::Clear($checksum, 0, $checksum.Length)
        }
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
    return Read-RebornPhase4CampaignReceipt `
        $root -AllowTestPath:$AllowTestPath -Pins $Pins
}

function Copy-RebornPhase4CampaignRecord {
    param([Parameter(Mandatory)][object]$Record)

    return ($Record | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
}

function New-RebornPhase4CampaignRecord {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$BackupBaselineNames,
        [object]$Pins = (Get-RebornPhase4SecureDockerPins)
    )

    $now = [DateTimeOffset]::UtcNow.ToString('O')
    $issuedUserSid =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    [pscustomobject][ordered]@{
        schemaVersion = 1
        mode = 'Phase4SecureDockerClientCampaign'
        campaignId = [Guid]::NewGuid().ToString('D')
        issuedUserSid = $issuedUserSid
        revision = [UInt64]1
        state = 'Preparing'
        createdUtc = $now
        updatedUtc = $now
        clientRoot = $Pins.ClientRoot
        candidateSha256 = $Pins.CandidateSha256
        manifestSha256 = $Pins.ManifestSha256
        manifestTrustSha256 = $Pins.ManifestTrustSha256
        rootCertificateSha256 = $Pins.RootCertificateSha256
        rootThumbprint = $Pins.RootThumbprint
        sourceTrustReceiptSha256 = $Pins.SourceTrustReceiptSha256
        inventoryReceiptSha256 = $Pins.InventoryReceiptSha256
        manifestSequence = $Pins.ManifestSequence
        activationFloorBefore = $Pins.ManifestSequence
        dockerProfile = $Pins.DockerProfile
        trustState = 'PendingInstall'
        hostsState = 'NotStarted'
        bundleState = 'NotStarted'
        bundleBackupBaselineNames = @($BackupBaselineNames)
        bundleBackupPath = ''
        bundleReceiptSha256 = ''
        bundleChecksumSha256 = ''
        hostsReceiptPath = ''
        hostsReceiptSha256 = ''
        hostsBackupPath = ''
        hostsBackupSha256 = ''
    }
}

Export-ModuleMember -Function @(
    'Get-RebornPhase4SecureDockerPins',
    'Assert-RebornPhase4PinnedInputs',
    'Resolve-RebornPhase4CampaignRoot',
    'Assert-RebornPhase4CampaignRecord',
    'Read-RebornPhase4CampaignReceipt',
    'Write-RebornPhase4CampaignReceipt',
    'Copy-RebornPhase4CampaignRecord',
    'New-RebornPhase4CampaignRecord'
)
