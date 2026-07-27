Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureEndpointManifestValidation.psm1'
) -Force

$script:ActiveCampaignRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV6'
$script:PreviewReadyV5CampaignRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV5'
$script:PreviewReadyV4CampaignRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV4'
$script:PreviewReadyV3CampaignRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV3'
$script:PreviewReadyV2CampaignRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV2'
$script:PreviewReadyV1CampaignRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV1'
$script:LegacyV1CampaignRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4Docker'
$script:ActiveFixture = (
    'C:\Reborn\artifacts\controlled-host-acceptance\' +
    '20260728-102640-preview-ready-v6')
$script:PreviewReadyV5Fixture = (
    'C:\Reborn\artifacts\controlled-host-acceptance\' +
    '20260728-031445-preview-ready-v5')
$script:PreviewReadyV4Fixture = (
    'C:\Reborn\artifacts\controlled-host-acceptance\' +
    '20260728-015732-preview-ready-v4')
$script:PreviewReadyV3Fixture = (
    'C:\Reborn\artifacts\controlled-host-acceptance\' +
    '20260728-004030-preview-ready-v3')
$script:PreviewReadyV2Fixture = (
    'C:\Reborn\artifacts\controlled-host-acceptance\' +
    '20260727-185522-preview-ready-v2')
$script:PreviewReadyV1Fixture = (
    'C:\Reborn\artifacts\controlled-host-acceptance\' +
    '20260727-004151-preview-ready-v1')
$script:LegacyV1Fixture = (
    'C:\Reborn\artifacts\controlled-host-acceptance\20260727-011921')
$script:RootSubject = 'CN=Reborn Development Root CA'

function Get-RebornPhase4PreviewReadyV3SecureDockerPinsCore {
    [pscustomobject]@{
        CampaignGeneration = 'PreviewReadyV3'
        CampaignSchemaVersion = 2
        CampaignMode =
            'Phase4SecureDockerClientCampaign.PreviewReadyV3'
        CompletionSchemaVersion = 2
        CompletionMode =
            'Phase4LoopbackAcceptanceCompletion.PreviewReadyV3'
        CampaignRoot = $script:PreviewReadyV3CampaignRoot
        EvidenceRoot = "$script:PreviewReadyV3Fixture\server-evidence"
        ClientRoot = 'C:\RebornNetworkAcceptanceClient'
        CandidatePath =
            "$script:PreviewReadyV3Fixture\candidate\Net.dll"
        NativeChecksPath = (
            "$script:PreviewReadyV3Fixture\candidate\" +
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
            '5FD6A0C37801A393689AF523854AD5BE258616BF52809D8FEA04437D34B7CA85'
        NativeChecksSha256 =
            'ABB81E184CA54DD9ECFFDC1F2DB690E122F81A4B394050AF4F7B6095FC34308B'
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

function Get-RebornPhase4PreviewReadyV4SecureDockerPinsCore {
    $pins = Get-RebornPhase4PreviewReadyV3SecureDockerPinsCore
    $pins.CampaignGeneration = 'PreviewReadyV4'
    $pins.CampaignSchemaVersion = 3
    $pins.CampaignMode =
        'Phase4SecureDockerClientCampaign.PreviewReadyV4'
    $pins.CompletionSchemaVersion = 3
    $pins.CompletionMode =
        'Phase4LoopbackAcceptanceCompletion.PreviewReadyV4'
    $pins.CampaignRoot = $script:PreviewReadyV4CampaignRoot
    $pins.EvidenceRoot =
        "$script:PreviewReadyV4Fixture\server-evidence"
    $pins.CandidatePath =
        "$script:PreviewReadyV4Fixture\candidate\Net.dll"
    $pins.NativeChecksPath = (
        "$script:PreviewReadyV4Fixture\candidate\" +
        'Godswar.NetShim.Checks.exe')
    $pins.CandidateSha256 =
        'D353E9215CE2F2E74A21C4C35FE356C15459FB7C1341FD01CA0618F575367D55'
    $pins.NativeChecksSha256 =
        'C5C8B7389F68F0C34E24EA2517A276DE912D92FAB9F0536544F15F9592934FB1'
    $pins | Add-Member -NotePropertyMembers ([ordered]@{
        CandidateOriginPath =
            "$script:PreviewReadyV4Fixture\candidate\Origin.exe"
        CandidateOriginSha256 =
            '1D1AA8768CC42655D4EF000237A301231B629D806FDCE99882C1D5888BBB3A5A'
    })
    return $pins
}

function Get-RebornPhase4PreviewReadyV5SecureDockerPinsCore {
    $pins = Get-RebornPhase4PreviewReadyV4SecureDockerPinsCore
    $pins.CampaignGeneration = 'PreviewReadyV5'
    $pins.CampaignSchemaVersion = 4
    $pins.CampaignMode =
        'Phase4SecureDockerClientCampaign.PreviewReadyV5'
    $pins.CompletionSchemaVersion = 4
    $pins.CompletionMode =
        'Phase4LoopbackAcceptanceCompletion.PreviewReadyV5'
    $pins.CampaignRoot = $script:PreviewReadyV5CampaignRoot
    $pins.EvidenceRoot =
        "$script:PreviewReadyV5Fixture\server-evidence"
    $pins.CandidatePath =
        "$script:PreviewReadyV5Fixture\candidate\Net.dll"
    $pins.NativeChecksPath = (
        "$script:PreviewReadyV5Fixture\candidate\" +
        'Godswar.NetShim.Checks.exe')
    $pins.CandidateSha256 =
        '0A34613ED9E4F6AC82608DA17570D905579F44A37CC6B08CAC8AA75B1A6DAA1A'
    $pins.NativeChecksSha256 =
        '49FEA163D18F37BFC1C3DD604C15028CDE57B3404C6C3F92A969CA30E0879E52'
    $pins.CandidateOriginPath =
        "$script:PreviewReadyV5Fixture\candidate\Origin.exe"
    $pins.CandidateOriginSha256 =
        'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C'
    $pins | Add-Member -NotePropertyName ClientTlsTrustMode `
        -NotePropertyValue 'EmbeddedDevelopmentRoot'
    return $pins
}

function Get-RebornPhase4SecureDockerPinsCore {
    $pins = Get-RebornPhase4PreviewReadyV5SecureDockerPinsCore
    $pins.CampaignGeneration = 'PreviewReadyV6'
    $pins.CampaignMode =
        'Phase4SecureDockerClientCampaign.PreviewReadyV6'
    $pins.CompletionMode =
        'Phase4LoopbackAcceptanceCompletion.PreviewReadyV6'
    $pins.CampaignRoot = $script:ActiveCampaignRoot
    $pins.EvidenceRoot = "$script:ActiveFixture\server-evidence"
    $pins.CandidatePath = "$script:ActiveFixture\candidate\Net.dll"
    $pins.NativeChecksPath = (
        "$script:ActiveFixture\candidate\" +
        'Godswar.NetShim.Checks.exe')
    $pins.CandidateSha256 =
        '2169589316DE3157F999563F80A3DFE9B73A120F73AFE1723D92338B816CAE97'
    $pins.NativeChecksSha256 =
        'FD34DD6F8FBD518D55C3833FB7E33C5DC819FD546D6799B201CE43E2A7424F75'
    $pins.CandidateOriginPath =
        "$script:ActiveFixture\candidate\Origin.exe"
    return $pins
}

function Get-RebornPhase4PreviewReadyV2SecureDockerPinsCore {
    $pins = Get-RebornPhase4PreviewReadyV3SecureDockerPinsCore
    $pins.CampaignGeneration = 'PreviewReadyV2'
    $pins.CampaignMode =
        'Phase4SecureDockerClientCampaign.PreviewReadyV2'
    $pins.CompletionMode =
        'Phase4LoopbackAcceptanceCompletion.PreviewReadyV2'
    $pins.CampaignRoot = $script:PreviewReadyV2CampaignRoot
    $pins.EvidenceRoot =
        "$script:PreviewReadyV2Fixture\server-evidence"
    $pins.CandidatePath =
        "$script:PreviewReadyV2Fixture\candidate\Net.dll"
    $pins.NativeChecksPath = (
        "$script:PreviewReadyV2Fixture\candidate\" +
        'Godswar.NetShim.Checks.exe')
    $pins.CandidateSha256 =
        'EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE'
    $pins.NativeChecksSha256 =
        '237EA0A3B90A4642DADA1170B1A740B966984C8004B99698F752491EC6732187'
    return $pins
}

function Get-RebornPhase4PreviewReadyV1SecureDockerPinsCore {
    $pins = Get-RebornPhase4PreviewReadyV2SecureDockerPinsCore
    $pins.CampaignGeneration = 'PreviewReadyV1'
    $pins.CampaignMode =
        'Phase4SecureDockerClientCampaign.PreviewReadyV1'
    $pins.CompletionMode =
        'Phase4LoopbackAcceptanceCompletion.PreviewReadyV1'
    $pins.CampaignRoot = $script:PreviewReadyV1CampaignRoot
    $pins.EvidenceRoot =
        "$script:PreviewReadyV1Fixture\server-evidence"
    $pins.CandidatePath =
        "$script:PreviewReadyV1Fixture\candidate\Net.dll"
    $pins.NativeChecksPath = (
        "$script:PreviewReadyV1Fixture\candidate\" +
        'Godswar.NetShim.Checks.exe')
    $pins.CandidateSha256 =
        'A3D042C6BC73AF4E9CAAA3B1BC1B5EE9EC9BD47E002B1A5BAE781A6AD43CFC75'
    $pins.NativeChecksSha256 =
        '294BE833851FB89468ECB011D01AE1A9B476DA25EB18A68D6B0544FC5374242F'
    return $pins
}

function Get-RebornPhase4HistoricalSecureDockerPinsCore {
    $pins = Get-RebornPhase4PreviewReadyV1SecureDockerPinsCore
    $pins.CampaignGeneration = 'LegacyV1'
    $pins.CampaignSchemaVersion = 1
    $pins.CampaignMode = 'Phase4SecureDockerClientCampaign'
    $pins.CompletionSchemaVersion = 1
    $pins.CompletionMode = 'Phase4LoopbackAcceptanceCompletion'
    $pins.CampaignRoot = $script:LegacyV1CampaignRoot
    $pins.EvidenceRoot = "$script:LegacyV1Fixture\server-evidence"
    $pins.CandidatePath = (
        "$script:LegacyV1Fixture\" +
        'candidate-posthandshake-alpn-fix\Net.dll')
    $pins.NativeChecksPath = (
        "$script:LegacyV1Fixture\candidate-posthandshake-alpn-fix\" +
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

    $bindings = @(
        @($Pins.CandidatePath, $Pins.CandidateSha256),
        @($Pins.NativeChecksPath, $Pins.NativeChecksSha256),
        @($Pins.ManifestPath, $Pins.ManifestSha256),
        @($Pins.ManifestTrustPath, $Pins.ManifestTrustSha256),
        @($Pins.NextManifestTrustPath, $Pins.NextManifestTrustSha256),
        @($Pins.RootCertificatePath, $Pins.RootCertificateSha256),
        @($Pins.ServerPfxPath, $Pins.ServerPfxSha256),
        @($Pins.SourceTrustReceiptPath, $Pins.SourceTrustReceiptSha256),
        @($Pins.InventoryReceiptPath, $Pins.InventoryReceiptSha256)
    )
    $candidateOriginPath =
        $Pins.PSObject.Properties['CandidateOriginPath']
    $candidateOriginSha256 =
        $Pins.PSObject.Properties['CandidateOriginSha256']
    if ($null -ne $candidateOriginPath -xor
        $null -ne $candidateOriginSha256) {
        throw 'Paired candidate Origin pins are incomplete.'
    }
    if ($null -ne $candidateOriginPath) {
        $bindings += ,@(
            [string]$candidateOriginPath.Value,
            [string]$candidateOriginSha256.Value)
    }
    $trustMode = $Pins.PSObject.Properties['ClientTlsTrustMode']
    if ([int]$Pins.CampaignSchemaVersion -ge 4) {
        if ($null -eq $trustMode -or
            [string]$trustMode.Value -cne
                'EmbeddedDevelopmentRoot') {
            throw 'Client TLS trust mode is invalid.'
        }
    } elseif ($null -ne $trustMode) {
        throw 'Pins contain a TLS trust mode before schema version 4.'
    }

    foreach ($binding in $bindings) {
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
    'Get-RebornPhase4PreviewReadyV5SecureDockerPinsCore',
    'Get-RebornPhase4PreviewReadyV4SecureDockerPinsCore',
    'Get-RebornPhase4PreviewReadyV3SecureDockerPinsCore',
    'Get-RebornPhase4PreviewReadyV2SecureDockerPinsCore',
    'Get-RebornPhase4PreviewReadyV1SecureDockerPinsCore',
    'Get-RebornPhase4HistoricalSecureDockerPinsCore',
    'Assert-RebornPhase4PinnedInputsCore'
)
