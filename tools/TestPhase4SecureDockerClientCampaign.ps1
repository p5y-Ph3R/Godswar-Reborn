$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$moduleRoot = $PSScriptRoot
Import-Module (
    Join-Path $moduleRoot 'Phase4SecureDockerClientCampaign.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'Phase4SecureDockerClientRuntime.psm1'
)

$passed = 0
$expectedChecks = 30

function Invoke-Check {
    param(
        [Parameter(Mandatory)][scriptblock]$Body,
        [Parameter(Mandatory)][string]$Name
    )

    & $Body
    $script:passed++
    Write-Host "PASS $Name"
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Body,
        [Parameter(Mandatory)][string]$Name
    )

    try {
        & $Body
    }
    catch {
        $script:passed++
        Write-Host "PASS $Name"
        return
    }
    throw "Expected failure did not occur: $Name"
}

$pins = Get-RebornPhase4SecureDockerPins
$temporary = Join-Path (
    [IO.Path]::GetTempPath()
) ('reborn-phase4-campaign-test-' + [Guid]::NewGuid().ToString('N'))

try {
    Invoke-Check {
        $manifest = Assert-RebornPhase4PinnedInputs $pins
        if ([UInt64]$manifest.Sequence -ne 3 -or
            $manifest.TlsLoginHost -cne 'login.reborn.test' -or
            $pins.CampaignRoot -cne (
                'C:\ProgramData\' +
                'RebornSecureNetworkPhase4DockerPreviewReadyV6') -or
            $pins.CandidatePath -cne (
                'C:\Reborn\artifacts\controlled-host-acceptance\' +
                '20260728-102640-preview-ready-v6\candidate\Net.dll') -or
            $pins.NativeChecksPath -cne (
                'C:\Reborn\artifacts\controlled-host-acceptance\' +
                '20260728-102640-preview-ready-v6\candidate\' +
                'Godswar.NetShim.Checks.exe') -or
            $pins.CandidateOriginPath -cne (
                'C:\Reborn\artifacts\controlled-host-acceptance\' +
                '20260728-102640-preview-ready-v6\candidate\Origin.exe') -or
            $pins.OriginSha256 -cne
                '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79' -or
            $pins.CandidateOriginSha256 -cne
                'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C' -or
            $pins.CandidateSha256 -cne
                '2169589316DE3157F999563F80A3DFE9B73A120F73AFE1723D92338B816CAE97' -or
            $pins.NativeChecksSha256 -cne
                'FD34DD6F8FBD518D55C3833FB7E33C5DC819FD546D6799B201CE43E2A7424F75' -or
            $pins.ClientTlsTrustMode -cne
                'EmbeddedDevelopmentRoot' -or
            (Split-Path -Leaf $pins.NextManifestTrustPath) -cne
                'development-manifest-next-trust.json') {
            throw 'Pinned manifest result changed.'
        }
    } 'exact source pins and signed manifest'

    Assert-Throws {
        $tamperedPins = $pins | Select-Object *
        $tamperedPins.CandidateOriginSha256 = ('0' * 64)
        Assert-RebornPhase4PinnedInputs $tamperedPins | Out-Null
    } 'pinned inputs reject a changed candidate Origin hash'

    Invoke-Check {
        $record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() `
            -Pins $pins
        $currentSid =
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if (@($record.bundleBackupBaselineNames).Count -ne 0 -or
            $record.issuedUserSid -cne $currentSid -or
            $record.schemaVersion -ne 4 -or
            $record.mode -cne $pins.CampaignMode -or
            $record.generation -cne 'PreviewReadyV6' -or
            $record.stockOriginSha256 -cne $pins.OriginSha256 -or
            $record.candidateOriginSha256 -cne
                $pins.CandidateOriginSha256 -or
            $record.nextManifestTrustSha256 -cne
                $pins.NextManifestTrustSha256 -or
            $record.tlsTrustMode -cne
                $pins.ClientTlsTrustMode) {
            throw 'An empty first-campaign backup baseline was not preserved.'
        }
    } 'PreviewReadyV6 schema, paired Origin, and embedded trust pins'

    Invoke-Check {
        $record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() `
            -Pins $pins
        $first = Write-RebornPhase4CampaignReceipt `
            $record $temporary -AllowTestPath -Pins $pins
        if ([UInt64]$first.Record.revision -ne 1 -or
            $first.Record.state -cne 'Preparing' -or
            @($first.Record.bundleBackupBaselineNames).Count -ne 0) {
            throw 'First campaign revision is invalid.'
        }

        $secondRecord =
            Copy-RebornPhase4CampaignRecord $first.Record
        $secondRecord.state = 'TrustInstalled'
        $secondRecord.trustState = 'EmbeddedRootPinned'
        $second = Write-RebornPhase4CampaignReceipt `
            $secondRecord $temporary -AllowTestPath -Pins $pins
        if ([UInt64]$second.Record.revision -ne 2 -or
            $second.Record.state -cne 'TrustInstalled') {
            throw 'Latest campaign revision is invalid.'
        }

        [IO.File]::WriteAllText(
            (Join-Path $temporary 'handoff-000003.json'),
            '{}',
            [Text.UTF8Encoding]::new($false))
        $latest = Read-RebornPhase4CampaignReceipt `
            $temporary -AllowTestPath -Pins $pins
        if ([UInt64]$latest.Record.revision -ne 2) {
            throw 'Trailing incomplete revision was not ignored.'
        }
    } 'immutable checksummed handoff and interrupted-tail recovery'

    Assert-Throws {
        $latest = Read-RebornPhase4CampaignReceipt `
            $temporary -AllowTestPath -Pins $pins
        $tampered = Copy-RebornPhase4CampaignRecord $latest.Record
        $tampered.candidateSha256 = ('0' * 64)
        Write-RebornPhase4CampaignReceipt `
            $tampered $temporary -AllowTestPath -Pins $pins | Out-Null
    } 'handoff rejects a changed candidate pin'

    Assert-Throws {
        $record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $pins
        $record.stockOriginSha256 = ('0' * 64)
        Write-RebornPhase4CampaignReceipt `
            $record $temporary -AllowTestPath -Pins $pins | Out-Null
    } 'handoff rejects a changed stock Origin pin'

    Assert-Throws {
        $record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $pins
        $record.candidateOriginSha256 = ('0' * 64)
        Write-RebornPhase4CampaignReceipt `
            $record $temporary -AllowTestPath -Pins $pins | Out-Null
    } 'handoff rejects a changed candidate Origin pin'

    $previewReadyV5Pins =
        Get-RebornPhase4PreviewReadyV5SecureDockerPins
    $previewReadyV4Pins =
        Get-RebornPhase4PreviewReadyV4SecureDockerPins
    $previewReadyV3Pins =
        Get-RebornPhase4PreviewReadyV3SecureDockerPins
    $previewReadyV2Pins =
        Get-RebornPhase4PreviewReadyV2SecureDockerPins
    $previewReadyV1Pins =
        Get-RebornPhase4PreviewReadyV1SecureDockerPins
    $legacyPins = Get-RebornPhase4HistoricalSecureDockerPins
    $legacyRoot = Join-Path $temporary 'legacy-v1'
    $previewReadyV5Root = Join-Path $temporary 'preview-ready-v5'
    $previewReadyV4Root = Join-Path $temporary 'preview-ready-v4'
    $previewReadyV3Root = Join-Path $temporary 'preview-ready-v3'
    $previewReadyV2Root = Join-Path $temporary 'preview-ready-v2'
    $previewReadyV1Root = Join-Path $temporary 'preview-ready-v1'
    $activeRoot = Join-Path $temporary 'preview-ready-v6'
    Invoke-Check {
        $legacyRecord = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $legacyPins
        $legacy = Write-RebornPhase4CampaignReceipt `
            $legacyRecord $legacyRoot -AllowTestPath -Pins $legacyPins
        if ($legacy.Record.schemaVersion -ne 1 -or
            $legacy.Record.mode -cne
                'Phase4SecureDockerClientCampaign' -or
            $null -ne $legacy.Record.PSObject.Properties['generation'] -or
            [UInt64]$legacy.Record.revision -ne 1) {
            throw 'Historical V1 receipt compatibility changed.'
        }
    } 'historical V1 receipts remain readable with historical pins'

    Invoke-Check {
        $v1Record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $previewReadyV1Pins
        $v1 = Write-RebornPhase4CampaignReceipt `
            $v1Record $previewReadyV1Root -AllowTestPath `
            -Pins $previewReadyV1Pins
        if ($v1.Record.schemaVersion -ne 2 -or
            $v1.Record.generation -cne 'PreviewReadyV1' -or
            $v1.Record.mode -cne
                'Phase4SecureDockerClientCampaign.PreviewReadyV1' -or
            $previewReadyV1Pins.CandidateSha256 -cne
                'A3D042C6BC73AF4E9CAAA3B1BC1B5EE9EC9BD47E002B1A5BAE781A6AD43CFC75') {
            throw 'PreviewReadyV1 receipt compatibility changed.'
        }
    } 'PreviewReadyV1 receipts remain readable with pinned historical accessor'

    Invoke-Check {
        $v2Record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $previewReadyV2Pins
        $v2 = Write-RebornPhase4CampaignReceipt `
            $v2Record $previewReadyV2Root -AllowTestPath `
            -Pins $previewReadyV2Pins
        if ($v2.Record.schemaVersion -ne 2 -or
            $v2.Record.generation -cne 'PreviewReadyV2' -or
            $v2.Record.mode -cne
                'Phase4SecureDockerClientCampaign.PreviewReadyV2' -or
            $previewReadyV2Pins.CandidateSha256 -cne
                'EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE') {
            throw 'PreviewReadyV2 receipt compatibility changed.'
        }
    } 'PreviewReadyV2 receipts remain readable with pinned historical accessor'

    Invoke-Check {
        $v3Record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $previewReadyV3Pins
        $v3 = Write-RebornPhase4CampaignReceipt `
            $v3Record $previewReadyV3Root -AllowTestPath `
            -Pins $previewReadyV3Pins
        if ($v3.Record.schemaVersion -ne 2 -or
            $v3.Record.generation -cne 'PreviewReadyV3' -or
            $v3.Record.mode -cne
                'Phase4SecureDockerClientCampaign.PreviewReadyV3' -or
            $previewReadyV3Pins.CandidateSha256 -cne
                '5FD6A0C37801A393689AF523854AD5BE258616BF52809D8FEA04437D34B7CA85' -or
            $null -ne $previewReadyV3Pins.PSObject.Properties[
                'CandidateOriginSha256'] -or
            $null -ne $v3.Record.PSObject.Properties[
                'candidateOriginSha256']) {
            throw 'PreviewReadyV3 receipt compatibility changed.'
        }
    } 'PreviewReadyV3 remains exact and readable with frozen pins'

    Invoke-Check {
        $v4Record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $previewReadyV4Pins
        $v4 = Write-RebornPhase4CampaignReceipt `
            $v4Record $previewReadyV4Root -AllowTestPath `
            -Pins $previewReadyV4Pins
        if ($v4.Record.schemaVersion -ne 3 -or
            $v4.Record.generation -cne 'PreviewReadyV4' -or
            $v4.Record.candidateOriginSha256 -cne
                '1D1AA8768CC42655D4EF000237A301231B629D806FDCE99882C1D5888BBB3A5A' -or
            $null -ne $v4.Record.PSObject.Properties['tlsTrustMode']) {
            throw 'PreviewReadyV4 receipt compatibility changed.'
        }
    } 'PreviewReadyV4 remains exact and readable with frozen pins'

    Invoke-Check {
        $v5Record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $previewReadyV5Pins
        $v5 = Write-RebornPhase4CampaignReceipt `
            $v5Record $previewReadyV5Root -AllowTestPath `
            -Pins $previewReadyV5Pins
        if ($v5.Record.schemaVersion -ne 4 -or
            $v5.Record.generation -cne 'PreviewReadyV5' -or
            $v5.Record.candidateSha256 -cne
                '0A34613ED9E4F6AC82608DA17570D905579F44A37CC6B08CAC8AA75B1A6DAA1A' -or
            $v5.Record.candidateOriginSha256 -cne
                'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C' -or
            $v5.Record.tlsTrustMode -cne
                'EmbeddedDevelopmentRoot') {
            throw 'PreviewReadyV5 receipt compatibility changed.'
        }
    } 'PreviewReadyV5 remains exact and readable with frozen pins'

    $activeRecord = New-RebornPhase4CampaignRecord `
        -BackupBaselineNames @() -Pins $pins
    Write-RebornPhase4CampaignReceipt `
        $activeRecord $activeRoot -AllowTestPath -Pins $pins | Out-Null
    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $activeRoot -AllowTestPath -Pins $legacyPins | Out-Null
    } 'LegacyV1 pins reject PreviewReadyV6 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $activeRoot -AllowTestPath -Pins $previewReadyV1Pins |
                Out-Null
    } 'PreviewReadyV1 pins reject PreviewReadyV6 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $activeRoot -AllowTestPath -Pins $previewReadyV2Pins |
                Out-Null
    } 'PreviewReadyV2 pins reject PreviewReadyV6 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $activeRoot -AllowTestPath -Pins $previewReadyV3Pins |
                Out-Null
    } 'PreviewReadyV3 pins reject PreviewReadyV6 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $activeRoot -AllowTestPath -Pins $previewReadyV5Pins |
                Out-Null
    } 'PreviewReadyV5 pins reject PreviewReadyV6 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $previewReadyV5Root -AllowTestPath -Pins $pins |
                Out-Null
    } 'PreviewReadyV6 pins reject PreviewReadyV5 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $previewReadyV3Root -AllowTestPath -Pins $pins |
                Out-Null
    } 'PreviewReadyV6 pins reject PreviewReadyV3 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $previewReadyV2Root -AllowTestPath -Pins $pins |
                Out-Null
    } 'PreviewReadyV6 pins reject PreviewReadyV2 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $previewReadyV1Root -AllowTestPath -Pins $pins |
                Out-Null
    } 'PreviewReadyV6 pins reject PreviewReadyV1 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $legacyRoot -AllowTestPath -Pins $pins | Out-Null
    } 'PreviewReadyV6 pins reject LegacyV1 receipts'

    Assert-Throws {
        $record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $previewReadyV5Pins
        Write-RebornPhase4CampaignReceipt `
            $record $previewReadyV5Root -Pins $previewReadyV5Pins |
                Out-Null
    } 'PreviewReadyV5 production campaign writes are read-only'

    Assert-Throws {
        $record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $previewReadyV3Pins
        Write-RebornPhase4CampaignReceipt `
            $record $previewReadyV3Root -Pins $previewReadyV3Pins |
                Out-Null
    } 'PreviewReadyV3 production campaign writes are read-only'

    Assert-Throws {
        $record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $previewReadyV2Pins
        Write-RebornPhase4CampaignReceipt `
            $record $previewReadyV2Root -Pins $previewReadyV2Pins |
                Out-Null
    } 'PreviewReadyV2 production campaign writes are read-only'

    Assert-Throws {
        $record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() -Pins $previewReadyV1Pins
        Write-RebornPhase4CampaignReceipt `
            $record $previewReadyV1Root -Pins $previewReadyV1Pins |
                Out-Null
    } 'PreviewReadyV1 production campaign writes are read-only'

    $containers = @(
        @'
[
  {
    "Name": "/godswar-server",
    "State": { "Running": true, "Health": { "Status": "healthy" } },
    "RestartCount": 0,
    "Config": {
      "Labels": { "com.reborn.network.profile": "secure-hybrid" },
      "Env": [
        "GODSWAR_RUNTIME_PROFILE=LocalDevelopment",
        "GODSWAR_SECURE_ENABLED=true",
        "GODSWAR_AUTH_ALLOW_LEGACY_RAW_AUTHENTICATION=false",
        "GODSWAR_SECURE_UDP_ENABLED=true",
        "GODSWAR_SECURE_UDP_GAMEPLAY_MOVEMENT_ENABLED=true",
        "GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED=false",
        "GODSWAR_AUTH_ALLOW_REGISTRATION=false",
        "GODSWAR_POSTGRES_CONNECTION_STRING=Host=postgres;Database=godswar_secure_dev;Username=test"
      ]
    },
    "NetworkSettings": {
      "Networks": { "reborn_secure_runtime": {} },
      "Ports": {
        "6599/tcp": [{ "HostIp": "127.0.0.1", "HostPort": "6599" }],
        "7443/tcp": [{ "HostIp": "127.0.0.1", "HostPort": "7443" }],
        "7444/udp": [{ "HostIp": "127.0.0.1", "HostPort": "7444" }]
      }
    }
  },
  {
    "Name": "/godswar-postgres",
    "State": { "Running": true, "Health": { "Status": "healthy" } },
    "RestartCount": 0,
    "Config": { "Labels": {}, "Env": [] },
    "NetworkSettings": { "Networks": {}, "Ports": {} }
  }
]
'@ | ConvertFrom-Json)
    $tcp = @(
        [pscustomobject]@{ LocalAddress = '127.0.0.1'; LocalPort = 6599 },
        [pscustomobject]@{ LocalAddress = '127.0.0.1'; LocalPort = 7443 })
    $udp = @(
        [pscustomobject]@{ LocalAddress = '127.0.0.1'; LocalPort = 7444 })

    Invoke-Check {
        $result = Assert-RebornPhase4DockerInspection `
            $containers $tcp $udp $pins
        if ($result.State -cne 'HealthyExact') {
            throw 'Valid Docker policy was not accepted.'
        }
    } 'exact secure Docker inspection policy'

    Assert-Throws {
        $raw = @(
            $tcp +
            [pscustomobject]@{
                LocalAddress = '127.1.1.110'
                LocalPort = 7000
            })
        Assert-RebornPhase4DockerInspection `
            $containers $raw $udp $pins | Out-Null
    } 'Docker inspection rejects a raw game listener'

    Assert-Throws {
        $wrongUdp = @(
            [pscustomobject]@{
                LocalAddress = '0.0.0.0'
                LocalPort = 7444
            })
        Assert-RebornPhase4DockerInspection `
            $containers $tcp $wrongUdp $pins | Out-Null
    } 'Docker inspection rejects a non-loopback UDP listener'

    if ($passed -ne $expectedChecks) {
        throw "Expected $expectedChecks checks, got $passed."
    }
    Write-Host "Phase 4 secure-Docker client campaign checks passed: $passed"
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        $resolvedTemporary =
            [IO.Path]::GetFullPath($temporary).TrimEnd('\')
        $temporaryBase =
            [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') +
            '\'
        if (-not $resolvedTemporary.StartsWith(
                $temporaryBase,
                [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTemporary) -notmatch
                '^reborn-phase4-campaign-test-[0-9a-f]{32}$') {
            throw 'Test cleanup target escaped its issued temporary scope.'
        }
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
