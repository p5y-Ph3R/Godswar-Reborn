Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'Phase4SecureDockerClientPins.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force

$script:IssuedCampaignRoot = (
    Get-RebornPhase4SecureDockerPinsCore).CampaignRoot
$script:MaximumReceiptBytes = 64KB
$script:MaximumReceiptRevisions = 256

function Get-RebornPhase4SecureDockerPins {
    return Get-RebornPhase4SecureDockerPinsCore
}

function Get-RebornPhase4HistoricalSecureDockerPins {
    return Get-RebornPhase4HistoricalSecureDockerPinsCore
}

function Get-RebornPhase4PreviewReadyV5SecureDockerPins {
    return Get-RebornPhase4PreviewReadyV5SecureDockerPinsCore
}

function Get-RebornPhase4PreviewReadyV4SecureDockerPins {
    return Get-RebornPhase4PreviewReadyV4SecureDockerPinsCore
}

function Get-RebornPhase4PreviewReadyV3SecureDockerPins {
    return Get-RebornPhase4PreviewReadyV3SecureDockerPinsCore
}

function Get-RebornPhase4PreviewReadyV2SecureDockerPins {
    return Get-RebornPhase4PreviewReadyV2SecureDockerPinsCore
}

function Get-RebornPhase4PreviewReadyV1SecureDockerPins {
    return Get-RebornPhase4PreviewReadyV1SecureDockerPinsCore
}

function Assert-RebornPhase4PinnedInputs {
    param([object]$Pins = (Get-RebornPhase4SecureDockerPins))

    return Assert-RebornPhase4PinnedInputsCore $Pins
}

function Test-RebornPhase4PairedOriginPins {
    param([Parameter(Mandatory)][object]$Pins)

    return (
        $null -ne
            $Pins.PSObject.Properties['CandidateOriginSha256'])
}

function Test-RebornPhase4ClientTlsTrustPins {
    param([Parameter(Mandatory)][object]$Pins)

    return (
        $null -ne
            $Pins.PSObject.Properties['ClientTlsTrustMode'])
}

function Test-RebornPhase4CurrentCampaignPins {
    param([Parameter(Mandatory)][object]$Pins)

    $current = Get-RebornPhase4SecureDockerPins
    return (
        [string]$Pins.CampaignGeneration -ceq
            [string]$current.CampaignGeneration -and
        [string]$Pins.CampaignMode -ceq
            [string]$current.CampaignMode)
}

function Grant-RebornPhase4CampaignReadAccess {
    param([Parameter(Mandatory)][string]$CampaignRoot)

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User -or
        $identity.User.Value -ceq 'S-1-5-18') {
        throw 'Campaign read access requires an issued user.'
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
        [switch]$Create,
        [object]$Pins = (Get-RebornPhase4SecureDockerPins)
    )

    $resolved = [IO.Path]::GetFullPath($CampaignRoot).TrimEnd('\')
    if (-not $AllowTestPath -and
        -not $resolved.Equals(
            [string]$Pins.CampaignRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Campaign root is not the issued path.'
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
    $generationProperty = $Record.PSObject.Properties['generation']
    $nextTrustProperty =
        $Record.PSObject.Properties['nextManifestTrustSha256']
    $stockOriginProperty =
        $Record.PSObject.Properties['stockOriginSha256']
    $candidateOriginProperty =
        $Record.PSObject.Properties['candidateOriginSha256']
    $tlsTrustProperty =
        $Record.PSObject.Properties['tlsTrustMode']
    $generationValid = if (
        [string]$Pins.CampaignGeneration -ceq 'LegacyV1') {
        $null -eq $generationProperty -and
            $null -eq $nextTrustProperty
    } else {
        $null -ne $generationProperty -and
            [string]$Record.generation -ceq
                [string]$Pins.CampaignGeneration -and
            $null -ne $nextTrustProperty -and
            [string]$Record.nextManifestTrustSha256 -ceq
                [string]$Pins.NextManifestTrustSha256
    }
    $originPinsValid = if (
        Test-RebornPhase4PairedOriginPins $Pins) {
        $null -ne $stockOriginProperty -and
            [string]$stockOriginProperty.Value -ceq
                [string]$Pins.OriginSha256 -and
            $null -ne $candidateOriginProperty -and
            [string]$candidateOriginProperty.Value -ceq
                [string]$Pins.CandidateOriginSha256
    } else {
        $null -eq $stockOriginProperty -and
            $null -eq $candidateOriginProperty
    }
    $tlsTrustValid = if (
        Test-RebornPhase4ClientTlsTrustPins $Pins) {
        $null -ne $tlsTrustProperty -and
            [string]$tlsTrustProperty.Value -ceq
                [string]$Pins.ClientTlsTrustMode
    }
    else {
        $null -eq $tlsTrustProperty
    }
    if (-not $generationValid -or
        -not $originPinsValid -or
        -not $tlsTrustValid -or
        $Record.schemaVersion -ne $Pins.CampaignSchemaVersion -or
        $Record.mode -cne $Pins.CampaignMode -or
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
        throw 'Campaign receipt is outside its pinned policy.'
    }
    $campaignId = [Guid]::Empty
    if (-not [Guid]::TryParse([string]$Record.campaignId, [ref]$campaignId) -or
        $campaignId -eq [Guid]::Empty -or
        [UInt64]$Record.revision -eq 0) {
        throw 'Campaign identity is invalid.'
    }
}

function Read-RebornPhase4CampaignReceipt {
    param(
        [string]$CampaignRoot = $script:IssuedCampaignRoot,
        [switch]$AllowTestPath,
        [object]$Pins = (Get-RebornPhase4SecureDockerPins)
    )

    $root = Resolve-RebornPhase4CampaignRoot `
        $CampaignRoot -AllowTestPath:$AllowTestPath -Pins $Pins
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return $null
    }

    $latest = $null
    $jsonFiles = @(
        Get-ChildItem -LiteralPath $root -File -Filter 'handoff-*.json' |
            Sort-Object Name)
    if ($jsonFiles.Count -gt $script:MaximumReceiptRevisions) {
        throw 'Campaign receipt revision bound was exceeded.'
    }
    foreach ($file in $jsonFiles) {
        if ($file.Name -notmatch '^handoff-(\d{6})\.json$') {
            throw "Unexpected receipt name: $($file.Name)"
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
            throw 'Campaign receipt size is invalid.'
        }
        $expected = (
            Get-Content -LiteralPath $checksumPath -Raw).Trim()
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        try {
            if ($expected -notmatch '^[0-9A-F]{64}$' -or
                (Get-RebornPhase4ReceiptSha256 $bytes) -cne $expected) {
                throw 'Campaign receipt checksum failed.'
            }
            $record = [Text.Encoding]::UTF8.GetString($bytes) |
                ConvertFrom-Json
        }
        finally {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
        Assert-RebornPhase4CampaignRecord $record $Pins
        if ([UInt64]$record.revision -ne $revision) {
            throw 'Campaign receipt revision mismatch.'
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

    if (-not $AllowTestPath -and
        -not (Test-RebornPhase4CurrentCampaignPins $Pins)) {
        throw 'Historical campaign receipts are read-only.'
    }
    $root = Resolve-RebornPhase4CampaignRoot `
        $CampaignRoot -AllowTestPath:$AllowTestPath -Create -Pins $Pins
    $all = @(
        Get-ChildItem -LiteralPath $root -File -Filter 'handoff-*.*')
    if ($all.Count -ge ($script:MaximumReceiptRevisions * 2)) {
        throw 'Campaign receipt file bound was exceeded.'
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
            throw 'Campaign receipt exceeds its size bound.'
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
    $record = [ordered]@{
        schemaVersion = $Pins.CampaignSchemaVersion
        mode = $Pins.CampaignMode
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
    if ([string]$Pins.CampaignGeneration -cne 'LegacyV1') {
        $record.Insert(2, 'generation', $Pins.CampaignGeneration)
        $record.Add(
            'nextManifestTrustSha256',
            $Pins.NextManifestTrustSha256)
    }
    if (Test-RebornPhase4PairedOriginPins $Pins) {
        $record.Add('stockOriginSha256', $Pins.OriginSha256)
        $record.Add(
            'candidateOriginSha256',
            $Pins.CandidateOriginSha256)
    }
    if (Test-RebornPhase4ClientTlsTrustPins $Pins) {
        $record.Add('tlsTrustMode', $Pins.ClientTlsTrustMode)
    }
    return [pscustomobject]$record
}

Export-ModuleMember -Function @(
    'Get-RebornPhase4SecureDockerPins',
    'Get-RebornPhase4PreviewReadyV5SecureDockerPins',
    'Get-RebornPhase4PreviewReadyV4SecureDockerPins',
    'Get-RebornPhase4PreviewReadyV3SecureDockerPins',
    'Get-RebornPhase4PreviewReadyV2SecureDockerPins',
    'Get-RebornPhase4PreviewReadyV1SecureDockerPins',
    'Get-RebornPhase4HistoricalSecureDockerPins',
    'Assert-RebornPhase4PinnedInputs',
    'Resolve-RebornPhase4CampaignRoot',
    'Assert-RebornPhase4CampaignRecord',
    'Read-RebornPhase4CampaignReceipt',
    'Write-RebornPhase4CampaignReceipt',
    'Copy-RebornPhase4CampaignRecord',
    'New-RebornPhase4CampaignRecord'
)
