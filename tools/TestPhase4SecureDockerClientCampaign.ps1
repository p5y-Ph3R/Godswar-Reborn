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
$expectedChecks = 14

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
                'RebornSecureNetworkPhase4DockerPreviewReadyV2') -or
            $pins.CandidatePath -cne (
                'C:\Reborn\artifacts\controlled-host-acceptance\' +
                '20260727-185522-preview-ready-v2\candidate\Net.dll') -or
            $pins.NativeChecksPath -cne (
                'C:\Reborn\artifacts\controlled-host-acceptance\' +
                '20260727-185522-preview-ready-v2\candidate\' +
                'Godswar.NetShim.Checks.exe') -or
            $pins.CandidateSha256 -cne
                'EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE' -or
            $pins.NativeChecksSha256 -cne
                '237EA0A3B90A4642DADA1170B1A740B966984C8004B99698F752491EC6732187' -or
            (Split-Path -Leaf $pins.NextManifestTrustPath) -cne
                'development-manifest-next-trust.json') {
            throw 'Pinned manifest result changed.'
        }
    } 'exact source pins and signed manifest'

    Invoke-Check {
        $record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames @() `
            -Pins $pins
        $currentSid =
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if (@($record.bundleBackupBaselineNames).Count -ne 0 -or
            $record.issuedUserSid -cne $currentSid -or
            $record.schemaVersion -ne 2 -or
            $record.mode -cne $pins.CampaignMode -or
            $record.generation -cne 'PreviewReadyV2' -or
            $record.nextManifestTrustSha256 -cne
                $pins.NextManifestTrustSha256) {
            throw 'An empty first-campaign backup baseline was not preserved.'
        }
    } 'PreviewReadyV2 schema and empty revision-one baseline'

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
        $secondRecord.trustState = 'Installed'
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

    $previewReadyV1Pins =
        Get-RebornPhase4PreviewReadyV1SecureDockerPins
    $legacyPins = Get-RebornPhase4HistoricalSecureDockerPins
    $legacyRoot = Join-Path $temporary 'legacy-v1'
    $previewReadyV1Root = Join-Path $temporary 'preview-ready-v1'
    $activeRoot = Join-Path $temporary 'preview-ready-v2'
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

    $activeRecord = New-RebornPhase4CampaignRecord `
        -BackupBaselineNames @() -Pins $pins
    Write-RebornPhase4CampaignReceipt `
        $activeRecord $activeRoot -AllowTestPath -Pins $pins | Out-Null
    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $activeRoot -AllowTestPath -Pins $legacyPins | Out-Null
    } 'LegacyV1 pins reject PreviewReadyV2 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $activeRoot -AllowTestPath -Pins $previewReadyV1Pins |
                Out-Null
    } 'PreviewReadyV1 pins reject PreviewReadyV2 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $previewReadyV1Root -AllowTestPath -Pins $pins |
                Out-Null
    } 'PreviewReadyV2 pins reject PreviewReadyV1 receipts'

    Assert-Throws {
        Read-RebornPhase4CampaignReceipt `
            $legacyRoot -AllowTestPath -Pins $pins | Out-Null
    } 'PreviewReadyV2 pins reject LegacyV1 receipts'

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
        "GODSWAR_SECURE_ENABLED=true",
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
