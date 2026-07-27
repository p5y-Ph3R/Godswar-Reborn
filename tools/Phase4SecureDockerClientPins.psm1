Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureEndpointManifestValidation.psm1'
) -Force

$script:ActiveCampaignRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV1'
$script:HistoricalCampaignRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4Docker'
$script:ActiveFixture = (
    'C:\Reborn\artifacts\controlled-host-acceptance\' +
    '20260727-004151-preview-ready-v1')
$script:HistoricalFixture = (
    'C:\Reborn\artifacts\controlled-host-acceptance\20260727-011921')
$script:RootSubject = 'CN=Reborn Development Root CA'

function Get-RebornPhase4SecureDockerPinsCore {
    [pscustomobject]@{
        CampaignGeneration = 'PreviewReadyV1'
        CampaignSchemaVersion = 2
        CampaignMode =
            'Phase4SecureDockerClientCampaign.PreviewReadyV1'
        CompletionSchemaVersion = 2
        CompletionMode =
            'Phase4LoopbackAcceptanceCompletion.PreviewReadyV1'
        CampaignRoot = $script:ActiveCampaignRoot
        EvidenceRoot = "$script:ActiveFixture\server-evidence"
        ClientRoot = 'C:\RebornNetworkAcceptanceClient'
        CandidatePath = "$script:ActiveFixture\candidate\Net.dll"
        NativeChecksPath = (
            "$script:ActiveFixture\candidate\" +
            'Godswar.NetShim.Checks.exe')
        ManifestPath =
            'C:\Reborn\artifacts\secure-network\RebornNetwork.gwem'
        ManifestTrustPath = (
            'C:\Reborn\artifacts\secure-network\' +
            'development-manifest-trust.json')
        NextManifestTrustPath = (
            'C:\Reborn\artifacts\secure-network\' +
            'development-manifest-next-trust.json')
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
            'A3D042C6BC73AF4E9CAAA3B1BC1B5EE9EC9BD47E002B1A5BAE781A6AD43CFC75'
        NativeChecksSha256 =
            '294BE833851FB89468ECB011D01AE1A9B476DA25EB18A68D6B0544FC5374242F'
        ManifestSha256 =
            '3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C'
        ManifestTrustSha256 =
            'A32B40917A01D510504528F5D6996F918A6A218991B64C50234ED84C75C75C07'
        NextManifestTrustSha256 =
            '582C252D31DE3361157C7625FB21DD104F907EA762FB77044E1CCEF2EA51E571'
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

function Get-RebornPhase4HistoricalSecureDockerPinsCore {
    $pins = Get-RebornPhase4SecureDockerPinsCore
    $pins.CampaignGeneration = 'LegacyV1'
    $pins.CampaignSchemaVersion = 1
    $pins.CampaignMode = 'Phase4SecureDockerClientCampaign'
    $pins.CompletionSchemaVersion = 1
    $pins.CompletionMode = 'Phase4LoopbackAcceptanceCompletion'
    $pins.CampaignRoot = $script:HistoricalCampaignRoot
    $pins.EvidenceRoot = "$script:HistoricalFixture\server-evidence"
    $pins.CandidatePath = (
        "$script:HistoricalFixture\" +
        'candidate-posthandshake-alpn-fix\Net.dll')
    $pins.NativeChecksPath = (
        "$script:HistoricalFixture\candidate-posthandshake-alpn-fix\" +
        'Godswar.NetShim.Checks.New.exe')
    $pins.CandidateSha256 =
        '0328D7EA84B68DD8D5A1DF7B0A291B9DC17EF3337C0114A7A396283FC4EF852B'
    $pins.NativeChecksSha256 =
        'D583309B921C7AA795F7A044F096762703AA2DB376A1D07B9EEB4F44312208D0'
    return $pins
}

function Get-RebornPhase4FileSha256Core {
    param([Parameter(Mandatory)][string]$LiteralPath)

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Pinned input is absent: $LiteralPath"
    }
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

function Assert-RebornPhase4PinnedInputsCore {
    param(
        [object]$Pins = (Get-RebornPhase4SecureDockerPinsCore)
    )

    foreach ($binding in @(
        @($Pins.CandidatePath, $Pins.CandidateSha256),
        @($Pins.NativeChecksPath, $Pins.NativeChecksSha256),
        @($Pins.ManifestPath, $Pins.ManifestSha256),
        @($Pins.ManifestTrustPath, $Pins.ManifestTrustSha256),
        @($Pins.NextManifestTrustPath, $Pins.NextManifestTrustSha256),
        @($Pins.RootCertificatePath, $Pins.RootCertificateSha256),
        @($Pins.ServerPfxPath, $Pins.ServerPfxSha256),
        @($Pins.SourceTrustReceiptPath, $Pins.SourceTrustReceiptSha256),
        @($Pins.InventoryReceiptPath, $Pins.InventoryReceiptSha256)
    )) {
        if ((Get-RebornPhase4FileSha256Core $binding[0]) -cne
            $binding[1]) {
            throw "Pinned SHA-256 mismatch: $($binding[0])"
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
        throw 'Pinned endpoint manifest contract changed.'
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
        throw 'Pinned source trust receipt contract changed.'
    }

    $root = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $Pins.RootCertificatePath)
    try {
        if ($root.HasPrivateKey -or
            $root.Subject -cne $script:RootSubject -or
            $root.Issuer -cne $script:RootSubject -or
            $root.Thumbprint -cne $Pins.RootThumbprint) {
            throw 'Pinned public root certificate is not exact.'
        }
    }
    finally {
        $root.Dispose()
    }
    return $manifest
}

Export-ModuleMember -Function @(
    'Get-RebornPhase4SecureDockerPinsCore',
    'Get-RebornPhase4HistoricalSecureDockerPinsCore',
    'Assert-RebornPhase4PinnedInputsCore'
)
