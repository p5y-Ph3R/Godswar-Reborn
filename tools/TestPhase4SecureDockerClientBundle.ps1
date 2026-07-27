$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'Phase4SecureDockerClientBundle.psm1'
) -Force

$passed = 0
$temporary = Join-Path (
    [IO.Path]::GetTempPath()
) ('reborn-phase4-bundle-manager-test-' +
    [Guid]::NewGuid().ToString('N'))

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
        [Parameter(Mandatory)][string]$Message,
        [Parameter(Mandatory)][string]$Name
    )

    try {
        & $Body
    }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") {
            throw "$Name returned an unexpected error: $($_.Exception.Message)"
        }
        $script:passed++
        Write-Host "PASS $Name"
        return
    }
    throw "Expected failure did not occur: $Name"
}

function Write-TestFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    [IO.File]::WriteAllText(
        $Path,
        $Content,
        [Text.UTF8Encoding]::new($false))
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-TestReceipt {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][object]$Receipt
    )

    $receiptPath = Join-Path $Directory 'receipt.json'
    [IO.File]::WriteAllText(
        $receiptPath,
        ($Receipt | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    $hash = (Get-FileHash -LiteralPath $receiptPath `
        -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        (Join-Path $Directory 'receipt.sha256'),
        "$hash`r`n",
        [Text.Encoding]::ASCII)
}

try {
    [IO.Directory]::CreateDirectory($temporary) | Out-Null
    $backupRoot = Join-Path $temporary 'paired-backups'
    [IO.Directory]::CreateDirectory($backupRoot) | Out-Null
    $baselineName =
        'client-secure-bundle-v2-Apply-20260728-000000000-' +
        ('a' * 32)
    $backupName =
        'client-secure-bundle-v2-Apply-20260728-000000001-' +
        ('b' * 32)
    [IO.Directory]::CreateDirectory(
        (Join-Path $backupRoot $baselineName)) | Out-Null
    $backup = Join-Path $backupRoot $backupName
    [IO.Directory]::CreateDirectory($backup) | Out-Null
    [IO.Directory]::CreateDirectory(
        (Join-Path $backupRoot 'not-an-apply-backup')) | Out-Null

    $stockOriginHash = Write-TestFile `
        (Join-Path $backup 'Origin.exe') 'stock-origin'
    $stockNetHash = Write-TestFile `
        (Join-Path $backup 'Net.dll') 'stock-net'
    $candidateOriginHash = Write-TestFile `
        (Join-Path $backup 'candidate-Origin.exe') 'candidate-origin'
    $candidateNetHash = Write-TestFile `
        (Join-Path $backup 'candidate-Net.dll') 'candidate-net'
    $manifestHash = Write-TestFile `
        (Join-Path $backup 'endpoint-manifest.gwem') 'manifest'
    $trustHash = Write-TestFile `
        (Join-Path $backup 'manifest-trust.json') 'trust'

    $pins = [pscustomobject]@{
        CampaignRoot =
            'C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV4'
        ClientRoot = 'C:\fixture-client'
        CandidatePath = 'C:\missing-v4-fixture\Net.dll'
        CandidateOriginPath = 'C:\missing-v4-fixture\Origin.exe'
        ManifestPath = 'C:\missing-v4-fixture\manifest.gwem'
        ManifestTrustPath = 'C:\missing-v4-fixture\trust.json'
        CandidateSha256 = $candidateNetHash
        CandidateOriginSha256 = $candidateOriginHash
        OriginSha256 = $stockOriginHash
        StockNetSha256 = $stockNetHash
        NativeChecksSha256 = ('A' * 64)
        ManifestSha256 = $manifestHash
        ManifestTrustSha256 = $trustHash
        InventoryReceiptPath = 'C:\missing-v4-fixture\inventory.json'
        InventoryReceiptSha256 = ('B' * 64)
    }

    $receipt = [ordered]@{
        schemaVersion = 4
        mode = 'Apply'
        policy = [ordered]@{
            OriginSha256 = $stockOriginHash
            CandidateOriginSha256 = $candidateOriginHash
            LegacyNetSha256 = $stockNetHash
            CandidateNetSha256 = $candidateNetHash
            ManifestSha256 = $manifestHash
            ManifestTrustSha256 = $trustHash
        }
        files = @(
            [ordered]@{
                path = 'Origin.exe'
                existed = $true
                backup = 'Origin.exe'
                sha256 = $stockOriginHash
            },
            [ordered]@{
                path = 'Net.dll'
                existed = $true
                backup = 'Net.dll'
                sha256 = $stockNetHash
            },
            [ordered]@{
                path = 'NetLegacy.dll'
                existed = $false
                backup = $null
                sha256 = $null
            },
            [ordered]@{
                path = 'RebornNetwork.gwem'
                existed = $false
                backup = $null
                sha256 = $null
            })
        recoveryInputs = @(
            [ordered]@{
                role = 'Candidate'
                path = 'candidate-Net.dll'
                sha256 = $candidateNetHash
            },
            [ordered]@{
                role = 'Manifest'
                path = 'endpoint-manifest.gwem'
                sha256 = $manifestHash
            },
            [ordered]@{
                role = 'Trust'
                path = 'manifest-trust.json'
                sha256 = $trustHash
            },
            [ordered]@{
                role = 'OriginCandidate'
                path = 'candidate-Origin.exe'
                sha256 = $candidateOriginHash
            })
    }
    Write-TestReceipt $backup $receipt

    Invoke-Check {
        $root = Resolve-RebornPhase4ManagerCampaignRoot '' $pins
        if ($root -cne $pins.CampaignRoot) {
            throw 'Manager did not select the active pinned V4 root.'
        }
        $custom = Resolve-RebornPhase4ManagerCampaignRoot `
            'C:\explicit-campaign' $pins
        if ($custom -cne 'C:\explicit-campaign') {
            throw 'Explicit campaign root was not preserved.'
        }
    } 'default campaign root follows active V5 pins'

    Invoke-Check {
        $arguments =
            Get-RebornPhase4BundleArguments $pins $backupRoot
        if ($arguments.ExpectedOriginSha256 -cne $stockOriginHash -or
            $arguments.CandidateOriginPath -cne
                $pins.CandidateOriginPath -or
            $arguments.ExpectedCandidateOriginSha256 -cne
                $candidateOriginHash -or
            $arguments.ExpectedCandidateSha256 -cne
                $candidateNetHash -or
            $arguments.BackupRoot -cne $backupRoot) {
            throw 'Paired bundle arguments are not bound to active pins.'
        }
    } 'V5 bundle arguments carry stock and candidate Origin pins'

    Invoke-Check {
        $installer = Get-Command (
            Join-Path $PSScriptRoot 'InstallSecureNetworkBundle.ps1')
        foreach ($name in @(
            'ExpectedOriginSha256',
            'CandidateOriginPath',
            'ExpectedCandidateOriginSha256'
        )) {
            if (-not $installer.Parameters.ContainsKey($name)) {
                throw "Secure bundle installer is missing $name."
            }
        }
    } 'manager paired-Origin arguments are accepted by installer'

    $liveClient = Join-Path $temporary 'live-client'
    [IO.Directory]::CreateDirectory($liveClient) | Out-Null
    $livePins = $pins.PSObject.Copy()
    $livePins.ClientRoot = $liveClient
    Write-TestFile (Join-Path $liveClient 'Origin.exe') 'stock-origin' |
        Out-Null
    Write-TestFile (Join-Path $liveClient 'Net.dll') 'stock-net' |
        Out-Null
    Invoke-Check {
        if ((Get-RebornPhase4LiveBundleRecoveryState $livePins) -cne
            'Stock') {
            throw 'Live recovery classifier did not recognize Stock.'
        }
        Write-TestFile (
            Join-Path $liveClient 'Origin.exe') 'candidate-origin' |
            Out-Null
        Write-TestFile (
            Join-Path $liveClient 'Net.dll') 'candidate-net' |
            Out-Null
        Write-TestFile (
            Join-Path $liveClient 'NetLegacy.dll') 'stock-net' |
            Out-Null
        Write-TestFile (
            Join-Path $liveClient 'RebornNetwork.gwem') 'manifest' |
            Out-Null
        if ((Get-RebornPhase4LiveBundleRecoveryState $livePins) -cne
            'InstalledExact') {
            throw (
                'Live recovery classifier did not recognize ' +
                'InstalledExact.')
        }
        [IO.File]::Delete(
            (Join-Path $liveClient 'RebornNetwork.gwem'))
        if ((Get-RebornPhase4LiveBundleRecoveryState $livePins) -cne
            'RecoverablePartial') {
            throw (
                'Live recovery classifier did not recognize an allowed ' +
                'partial state.')
        }
    } 'live recovery classification needs no external fixture'

    Write-TestFile (Join-Path $liveClient 'Net.dll') 'unexpected-net' |
        Out-Null
    Assert-Throws {
        Get-RebornPhase4LiveBundleRecoveryState $livePins | Out-Null
    } 'outside its pinned recovery state space' `
        'live recovery classification rejects unpinned bytes'

    Invoke-Check {
        $names = @(
            Get-RebornPhase4BundleBackupBaselineNames $backupRoot)
        if ($names.Count -ne 2 -or
            $names[0] -cne $baselineName -or
            $names[1] -cne $backupName) {
            throw 'Apply-backup baseline discovery changed.'
        }
    } 'backup baseline names remain sorted and prefix-scoped'

    $record = [pscustomobject]@{
        bundleBackupPath = ''
        bundleBackupBaselineNames = @($baselineName)
    }
    Invoke-Check {
        $resolved = Resolve-RebornPhase4BundleBackupPath `
            $record $backupRoot $pins
        if ($resolved -cne $backup) {
            throw 'Schema-4 backup discovery selected the wrong directory.'
        }
    } 'schema-4 paired backup discovery is self-contained'

    Invoke-Check {
        $direct = [pscustomobject]@{
            bundleBackupPath = $backup
            bundleBackupBaselineNames = @()
        }
        $resolved = Resolve-RebornPhase4BundleBackupPath `
            $direct $backupRoot $pins
        if ($resolved -cne $backup) {
            throw 'Recorded schema-4 backup path changed.'
        }
    } 'recorded schema-4 backup is revalidated'

    $candidateOriginBackup = Join-Path $backup 'candidate-Origin.exe'
    $heldCandidateOrigin = "$candidateOriginBackup.missing"
    [IO.File]::Move($candidateOriginBackup, $heldCandidateOrigin)
    Assert-Throws {
        Resolve-RebornPhase4BundleBackupPath `
            $record $backupRoot $pins | Out-Null
    } 'candidate-Origin.exe' `
        'schema-4 discovery rejects a missing Origin recovery input'
    [IO.File]::Move($heldCandidateOrigin, $candidateOriginBackup)

    $receipt.schemaVersion = 3
    Write-TestReceipt $backup $receipt
    Assert-Throws {
        Resolve-RebornPhase4BundleBackupPath `
            $record $backupRoot $pins | Out-Null
    } 'not schema 4 Apply' `
        'paired discovery rejects an unpaired receipt schema'
    $receipt.schemaVersion = 4
    Write-TestReceipt $backup $receipt

    $secondName =
        'client-secure-bundle-v2-Apply-20260728-000000002-' +
        ('c' * 32)
    $second = Join-Path $backupRoot $secondName
    [IO.Directory]::CreateDirectory($second) | Out-Null
    Assert-Throws {
        Resolve-RebornPhase4BundleBackupPath `
            $record $backupRoot $pins | Out-Null
    } 'identify one new Apply backup' `
        'ambiguous backup discovery remains fail-closed'
    [IO.Directory]::Delete($second, $false)

    $outside = Join-Path $temporary (
        'client-secure-bundle-v2-Apply-20260728-000000003-' +
        ('d' * 32))
    [IO.Directory]::CreateDirectory($outside) | Out-Null
    Assert-Throws {
        $direct = [pscustomobject]@{
            bundleBackupPath = $outside
            bundleBackupBaselineNames = @()
        }
        Resolve-RebornPhase4BundleBackupPath `
            $direct $backupRoot $pins | Out-Null
    } 'outside its issued backup root' `
        'recorded backup cannot escape the campaign backup root'

    $v3Root = Join-Path $temporary 'v3-backups'
    [IO.Directory]::CreateDirectory($v3Root) | Out-Null
    $v3Name =
        'client-secure-bundle-v2-Apply-20260728-000000004-' +
        ('e' * 32)
    $v3Backup = Join-Path $v3Root $v3Name
    [IO.Directory]::CreateDirectory($v3Backup) | Out-Null
    Write-TestFile (Join-Path $v3Backup 'Net.dll') 'v3-stock-net' |
        Out-Null
    Write-TestReceipt $v3Backup ([ordered]@{
        schemaVersion = 3
        mode = 'Apply'
    })
    $v3Pins = [pscustomobject]@{
        CampaignRoot =
            'C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV3'
        ClientRoot = 'C:\fixture-client'
        CandidatePath = 'C:\v3-fixture\Net.dll'
        ManifestPath = 'C:\v3-fixture\manifest.gwem'
        ManifestTrustPath = 'C:\v3-fixture\trust.json'
        CandidateSha256 = ('C' * 64)
        OriginSha256 = ('D' * 64)
        StockNetSha256 = ('E' * 64)
        NativeChecksSha256 = ('F' * 64)
        ManifestSha256 = ('1' * 64)
        ManifestTrustSha256 = ('2' * 64)
        InventoryReceiptPath = 'C:\v3-fixture\inventory.json'
        InventoryReceiptSha256 = ('3' * 64)
    }
    Invoke-Check {
        $arguments =
            Get-RebornPhase4BundleArguments $v3Pins $v3Root
        if ($arguments.ExpectedOriginSha256 -cne
                $v3Pins.OriginSha256 -or
            $arguments.ContainsKey('CandidateOriginPath') -or
            $arguments.ContainsKey(
                'ExpectedCandidateOriginSha256')) {
            throw 'Legacy Net-only bundle arguments changed.'
        }
        $v3Record = [pscustomobject]@{
            bundleBackupPath = ''
            bundleBackupBaselineNames = @()
        }
        $resolved = Resolve-RebornPhase4BundleBackupPath `
            $v3Record $v3Root $v3Pins
        if ($resolved -cne $v3Backup) {
            throw 'Legacy Net-only backup discovery changed.'
        }
    } 'PreviewReadyV3 Net-only behavior remains compatible'

    $brokenPins = $pins.PSObject.Copy()
    $brokenPins.CandidateOriginPath = ''
    Assert-Throws {
        Get-RebornPhase4BundleArguments `
            $brokenPins $backupRoot | Out-Null
    } 'must be present together' `
        'partial paired-Origin pins are rejected'

    if ($passed -ne 14) {
        throw "Expected 14 checks, got $passed."
    }
    Write-Host "Phase 4 manager bundle checks passed: $passed"
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Container) {
        $resolved = [IO.Path]::GetFullPath($temporary).TrimEnd('\')
        $base =
            [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') +
            '\'
        if (-not $resolved.StartsWith(
                $base,
                [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolved) -notmatch
                '^reborn-phase4-bundle-manager-test-[0-9a-f]{32}$') {
            throw 'Test cleanup target escaped its issued temporary scope.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
