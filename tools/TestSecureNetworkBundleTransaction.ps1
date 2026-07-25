[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleTransaction.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureEndpointManifestValidation.psm1'
) -Force

function Write-UInt16Be {
    param([byte[]]$Bytes, [int]$Offset, [UInt16]$Value)
    $Bytes[$Offset] = [byte](($Value -shr 8) -band 0xFF)
    $Bytes[$Offset + 1] = [byte]($Value -band 0xFF)
}

function Write-UInt32Be {
    param([byte[]]$Bytes, [int]$Offset, [UInt32]$Value)
    for ($index = 0; $index -lt 4; $index++) {
        $Bytes[$Offset + $index] =
            [byte](($Value -shr ((3 - $index) * 8)) -band 0xFF)
    }
}

function Write-UInt64Be {
    param([byte[]]$Bytes, [int]$Offset, [UInt64]$Value)
    for ($index = 0; $index -lt 8; $index++) {
        $Bytes[$Offset + $index] =
            [byte](($Value -shr ((7 - $index) * 8)) -band 0xFF)
    }
}

function New-SignedManifestFixture {
    param(
        [string]$ManifestPath,
        [string]$TrustPath,
        [UInt64]$Sequence = 7
    )

    $key = [Security.Cryptography.CngKey]::Create(
        [Security.Cryptography.CngAlgorithm]::ECDsaP256)
    $ecdsa = [Security.Cryptography.ECDsaCng]::new($key)
    $ecdsa.HashAlgorithm =
        [Security.Cryptography.CngAlgorithm]::Sha256
    try {
        $public = $key.Export(
            [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        if ($public.Length -ne 72) {
            throw 'Unexpected ECDSA P-256 public blob size.'
        }
        $x = New-Object byte[] 32
        $y = New-Object byte[] 32
        [Array]::Copy($public, 8, $x, 0, 32)
        [Array]::Copy($public, 40, $y, 0, 32)

        $logical = [Text.Encoding]::ASCII.GetBytes(
            'login-route.reborn.test')
        $tls = [Text.Encoding]::ASCII.GetBytes('login.reborn.test')
        $suffix = [Text.Encoding]::ASCII.GetBytes('reborn.test')
        $audience = [Text.Encoding]::ASCII.GetBytes('reborn-game')
        $signedLength =
            72 + $logical.Length + $tls.Length +
            1 + $suffix.Length + 1 + $audience.Length + 4
        $signed = New-Object byte[] $signedLength
        [Text.Encoding]::ASCII.GetBytes('GWEM').CopyTo($signed, 0)
        Write-UInt32Be $signed 4 ([UInt32]($signedLength + 64))
        Write-UInt16Be $signed 8 72
        Write-UInt16Be $signed 10 1
        Write-UInt16Be $signed 12 0
        $signed[14] = 1
        $signed[15] = 0
        Write-UInt16Be $signed 16 1
        Write-UInt16Be $signed 18 0xD001
        Write-UInt64Be $signed 24 $Sequence
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        Write-UInt64Be $signed 32 ([UInt64]($now - 60))
        Write-UInt64Be $signed 40 ([UInt64]($now + 3600))
        Write-UInt16Be $signed 48 1
        Write-UInt16Be $signed 50 0
        Write-UInt16Be $signed 52 5999
        Write-UInt16Be $signed 54 6599
        Write-UInt16Be $signed 56 ([UInt16]$logical.Length)
        Write-UInt16Be $signed 58 ([UInt16]$tls.Length)
        $signed[60] = 1
        $signed[61] = 1
        $signed[62] = 1
        Write-UInt32Be $signed 64 ([UInt32]$signedLength)

        $cursor = 72
        $logical.CopyTo($signed, $cursor)
        $cursor += $logical.Length
        $tls.CopyTo($signed, $cursor)
        $cursor += $tls.Length
        $signed[$cursor++] = [byte]$suffix.Length
        $suffix.CopyTo($signed, $cursor)
        $cursor += $suffix.Length
        $signed[$cursor++] = [byte]$audience.Length
        $audience.CopyTo($signed, $cursor)
        $cursor += $audience.Length
        Write-UInt32Be $signed $cursor 42

        $signature = $ecdsa.SignData($signed)
        if ($signature.Length -ne 64) {
            throw 'Unexpected ECDSA P-256 signature size.'
        }
        $manifest = New-Object byte[] ($signed.Length + 64)
        $signed.CopyTo($manifest, 0)
        $signature.CopyTo($manifest, $signed.Length)
        [IO.File]::WriteAllBytes($ManifestPath, $manifest)
        [IO.File]::WriteAllText(
            $TrustPath,
            ([ordered]@{
                schemaVersion = 1
                keyId = '53249'
                environment = '1'
                minimumSequence = '1'
                x = [Convert]::ToBase64String($x)
                y = [Convert]::ToBase64String($y)
            } | ConvertTo-Json),
            [Text.UTF8Encoding]::new($false))
    }
    finally {
        $ecdsa.Dispose()
        $key.Dispose()
    }
}

function Write-TestBytes {
    param([string]$Path, [int]$Length, [byte]$Seed)
    $bytes = New-Object byte[] $Length
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [byte](($Seed + $index) % 251)
    }
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

$authenticatedUsers =
    [Security.Principal.SecurityIdentifier]::new('S-1-5-11')
$inheritance =
    [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
    [Security.AccessControl.InheritanceFlags]::ObjectInherit
$inheritOnlyModify =
    [Security.AccessControl.FileSystemAccessRule]::new(
        $authenticatedUsers,
        [Security.AccessControl.FileSystemRights]::Modify,
        $inheritance,
        [Security.AccessControl.PropagationFlags]::InheritOnly,
        [Security.AccessControl.AccessControlType]::Allow)
$pathSafetyModule = Get-Module SecureNetworkPathSafety
$inheritOnlyHazard = & $pathSafetyModule {
    param($Rule)
    Test-RebornDirectoryRuleHazard $Rule $true $true $false
} $inheritOnlyModify
Assert-True $inheritOnlyHazard (
    'an unsafe inheritance-only client-content ACE was ignored')

$root = Join-Path (
    [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\artifacts'))
) ('slice8-bundle-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $reparseTarget = Join-Path $root 'reparse-target'
    $reparsePath = Join-Path $root 'reparse-link'
    [IO.Directory]::CreateDirectory($reparseTarget) | Out-Null
    $reparseRejected = $false
    try {
        New-Item `
            -ItemType Junction `
            -Path $reparsePath `
            -Target $reparseTarget | Out-Null
        try {
            Assert-RebornDirectoryPath `
                $reparsePath 'reparse fixture' | Out-Null
        }
        catch {
            $reparseRejected = $true
        }
    }
    finally {
        if (Test-Path -LiteralPath $reparsePath) {
            [IO.Directory]::Delete($reparsePath, $false)
        }
        [IO.Directory]::Delete($reparseTarget, $false)
    }
    Assert-True $reparseRejected 'reparse path was accepted'

    $client = Join-Path $root 'client'
    $inputs = Join-Path $root 'inputs'
    $backups = Join-Path $root 'backups'
    [IO.Directory]::CreateDirectory($client) | Out-Null
    [IO.Directory]::CreateDirectory($inputs) | Out-Null
    [IO.Directory]::CreateDirectory($backups) | Out-Null
    $origin = Join-Path $client 'Origin.exe'
    $stock = Join-Path $client 'Net.dll'
    $candidate = Join-Path $inputs 'Net.dll'
    $manifest = Join-Path $inputs 'RebornNetwork.gwem'
    $trust = Join-Path $inputs 'manifest-trust.json'
    $state = Join-Path $root 'activation-state.json'
    Write-TestBytes $origin 4096 3
    Write-TestBytes $stock 2048 7
    Write-TestBytes $candidate 3072 11
    New-SignedManifestFixture $manifest $trust

    $originHash = (Get-FileHash $origin -Algorithm SHA256).Hash
    $stockHash = (Get-FileHash $stock -Algorithm SHA256).Hash
    $candidateHash = (Get-FileHash $candidate -Algorithm SHA256).Hash
    $manifestHash = (Get-FileHash $manifest -Algorithm SHA256).Hash
    $trustHash = (Get-FileHash $trust -Algorithm SHA256).Hash
    $policy = New-RebornSecureBundlePolicy `
        $originHash $stockHash $candidateHash $manifestHash $trustHash

    $initial = Get-RebornSecureBundleStatus `
        $policy $client $candidate $manifest $trust `
        OfflineFile $state
    Assert-True ($initial.State -eq 'Stock') 'fixture was not Stock'

    $applied = Invoke-RebornSecureBundleApply `
        $policy $client $candidate $manifest $trust $backups `
        OfflineFile $state
    Assert-True (
        $applied.Result -eq 'InstalledExact'
    ) 'Apply did not finish'
    $installed = Get-RebornSecureBundleStatus `
        $policy $client $candidate $manifest $trust `
        OfflineFile $state
    Assert-True (
        $installed.State -eq 'InstalledExact' -and
        $installed.SequenceFloor -eq 7
    ) 'installed bundle or floor was incorrect'

    $wrongBackups = Join-Path $root 'wrong-backups'
    [IO.Directory]::CreateDirectory($wrongBackups) | Out-Null
    $escapedBackupRejected = $false
    try {
        Invoke-RebornSecureBundleRestore `
            $policy $client $candidate $manifest $trust `
            $applied.BackupPath $wrongBackups OfflineFile $state |
            Out-Null
    }
    catch {
        $escapedBackupRejected = $true
    }
    Assert-True (
        $escapedBackupRejected -and
        (Get-RebornSecureBundleStatus `
            $policy $client $candidate $manifest $trust `
            OfflineFile $state).State -eq 'InstalledExact'
    ) 'Restore accepted a backup outside the configured BackupRoot'

    $restored = Invoke-RebornSecureBundleRestore `
        $policy $client $candidate $manifest $trust `
        $applied.BackupPath $backups OfflineFile $state
    $afterRestore = Get-RebornSecureBundleStatus `
        $policy $client $candidate $manifest $trust `
        OfflineFile $state
    Assert-True (
        $restored.Result -eq 'StockFilesRestored' -and
        $afterRestore.State -eq 'Stock' -and
        $afterRestore.SequenceFloor -eq 7 -and
        -not (Test-Path (Join-Path $client 'NetLegacy.dll')) -and
        -not (Test-Path (Join-Path $client 'RebornNetwork.gwem'))
    ) 'Restore did not reproduce Stock while retaining the floor'

    $highManifest = Join-Path $inputs 'UnpinnedHighSequence.gwem'
    $highTrust = Join-Path $inputs 'unpinned-high-trust.json'
    New-SignedManifestFixture $highManifest $highTrust 1000
    $highVerified = Read-RebornSecureEndpointManifest `
        $highManifest $highTrust 0
    Assert-True (
        $highVerified.ManifestSha256 -ceq
            (Get-FileHash $highManifest -Algorithm SHA256).Hash -and
        $highVerified.TrustSha256 -ceq
            (Get-FileHash $highTrust -Algorithm SHA256).Hash
    ) 'verified manifest did not retain exact-byte hashes'
    $transactionModule =
        Get-Module -Name SecureNetworkBundleTransaction
    $unboundHighSequenceRejected = $false
    try {
        & $transactionModule {
            param(
                $Current,
                $VerifiedManifest,
                $Policy,
                $StatePath
            )
            Set-RebornSafeDisabledState `
                $Current `
                $VerifiedManifest `
                $Policy `
                'OfflineFile' `
                $StatePath
        } ([pscustomobject]@{ SequenceFloor = [UInt64]7 }) `
            $highVerified $policy $state
    }
    catch {
        $unboundHighSequenceRejected = $true
    }
    Assert-True (
        $unboundHighSequenceRejected -and
        (Get-RebornSecureBundleStatus `
            $policy $client $candidate $manifest $trust `
            OfflineFile $state).SequenceFloor -eq 7
    ) 'unpinned higher manifest sequence changed the rollback floor'

    $sentinel = Join-Path $root 'receipt-path-sentinel.bin'
    Write-TestBytes $sentinel 64 19
    $sentinelHash = (Get-FileHash $sentinel -Algorithm SHA256).Hash
    $receiptPath = Join-Path $applied.BackupPath 'receipt.json'
    $forgedReceipt =
        Get-Content -LiteralPath $receiptPath -Raw |
        ConvertFrom-Json
    $forgedReceipt.files[2].path = '..\receipt-path-sentinel.bin'
    [IO.File]::WriteAllText(
        $receiptPath,
        ($forgedReceipt | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $applied.BackupPath 'receipt.sha256'),
        (Get-FileHash $receiptPath -Algorithm SHA256).Hash,
        [Text.UTF8Encoding]::new($false))
    $forgedReceiptRejected = $false
    try {
        Invoke-RebornSecureBundleRestore `
            $policy $client $candidate $manifest $trust `
            $applied.BackupPath $backups OfflineFile $state | Out-Null
    }
    catch {
        $forgedReceiptRejected = $true
    }
    Assert-True (
        $forgedReceiptRejected -and
        (Get-FileHash $sentinel -Algorithm SHA256).Hash -eq $sentinelHash
    ) 'forged receipt path was not rejected before mutation'

    $forgedReceipt.files[2].path = 'RebornNetwork.gwem'
    $forgedReceipt.manifest.sequence = [Int64]::MaxValue.ToString()
    $forgedReceipt.stateBefore.sequenceFloor =
        [Int64]::MaxValue.ToString()
    [IO.File]::WriteAllText(
        $receiptPath,
        ($forgedReceipt | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $applied.BackupPath 'receipt.sha256'),
        (Get-FileHash $receiptPath -Algorithm SHA256).Hash,
        [Text.UTF8Encoding]::new($false))
    $forgedFloorRejected = $false
    try {
        Invoke-RebornSecureBundleRestore `
            $policy $client $candidate $manifest $trust `
            $applied.BackupPath $backups OfflineFile $state | Out-Null
    }
    catch {
        $forgedFloorRejected = $true
    }
    Assert-True (
        $forgedFloorRejected -and
        (Get-RebornSecureBundleStatus `
            $policy $client $candidate $manifest $trust `
            OfflineFile $state).SequenceFloor -eq 7
    ) 'forged receipt metadata changed the rollback floor'

    foreach ($point in @(
        'AfterState',
        'AfterManifest',
        'AfterLegacy',
        'AfterCandidate'
    )) {
        $threw = $false
        try {
            Invoke-RebornSecureBundleApply `
                $policy $client $candidate $manifest $trust $backups `
                OfflineFile $state -FailurePoint $point | Out-Null
        }
        catch {
            $threw = $true
        }
        $rolledBack = Get-RebornSecureBundleStatus `
            $policy $client $candidate $manifest $trust `
            OfflineFile $state
        Assert-True (
            $threw -and $rolledBack.State -eq 'Stock'
        ) "interruption $point did not roll back"
    }

    $tampered = [IO.File]::ReadAllBytes($manifest)
    $tampered[80] = $tampered[80] -bxor 1
    [IO.File]::WriteAllBytes($manifest, $tampered)
    $tamperRejected = $false
    try {
        Get-RebornSecureBundleStatus `
            $policy $client $candidate $manifest $trust `
            OfflineFile $state | Out-Null
    }
    catch {
        $tamperRejected = $true
    }
    Assert-True $tamperRejected 'tampered manifest was accepted'

    Write-Host 'Secure network bundle transaction checks passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $artifactRoot = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\artifacts')).TrimEnd('\')
    if ($resolved.StartsWith(
            $artifactRoot + '\',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved -PathType Container)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
