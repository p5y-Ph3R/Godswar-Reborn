Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'ControlledHostClientRootLease.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'ControlledHostServerRuntime.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'ControlledHostRuntimeCleanupInventory.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkBundleFiles.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

$script:MaximumEntries = 10000
$script:MaximumFileBytes = 128MB
$script:MaximumTotalBytes = 4GB
$script:MaximumDepth = 16

function Get-RebornControlledHostRuntimeCleanupReceiptPath {
    param(
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [string]$ReceiptRoot = (
            Join-Path $env:ProgramData (
                'RebornSecureNetworkCleanupReceipts')),
        [switch]$AllowTestPath
    )

    $runtime = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\')
    $stamp = [IO.Path]::GetFileName($runtime)
    if ($stamp -cnotmatch '^\d{8}-\d{6}$') {
        throw 'Runtime cleanup requires an issued timestamped root.'
    }
    $root = [IO.Path]::GetFullPath($ReceiptRoot).TrimEnd('\')
    if ($AllowTestPath) {
        $temporary = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $root.StartsWith(
                $temporary,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Test cleanup-receipt root must remain under temp.'
        }
    } else {
        $expected = [IO.Path]::GetFullPath(
            (Join-Path $env:ProgramData (
                'RebornSecureNetworkCleanupReceipts'))).TrimEnd('\')
        if (-not $root.Equals(
                $expected,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                'Production cleanup receipts require the issued ' +
                'protected root.')
        }
    }
    return Join-Path $root "runtime-cleanup-$stamp.json"
}

function Resolve-RuntimeCleanupReceiptRoot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowTestPath
    )

    $root = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($AllowTestPath) {
        $temporary = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $root.StartsWith(
                $temporary,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Test cleanup-receipt root must remain under temp.'
        }
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            [IO.Directory]::CreateDirectory($root) | Out-Null
        }
        return Assert-RebornDirectoryPath `
            $root 'test runtime cleanup receipt root'
    }

    $expected = [IO.Path]::GetFullPath(
        (Join-Path $env:ProgramData (
            'RebornSecureNetworkCleanupReceipts'))).TrimEnd('\')
    if (-not $root.Equals(
            $expected,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Production cleanup receipts require the issued protected root.'
    }
    if (-not (Test-Path -LiteralPath $root)) {
        [IO.Directory]::CreateDirectory(
            $root,
            (New-RebornControlledHostRuntimeSecurity)) | Out-Null
    }
    return Assert-RebornProtectedDirectoryPath `
        $root 'runtime cleanup receipt root' `
        -ProtectContents -RequireProtectedAcl
}

function New-RebornControlledHostRuntimeCleanupReceipt {
    param(
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{8}-[0-9A-F]{16}$')]
        [string]$RuntimeIdentity,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$RuntimeReceiptSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$RuntimeReceiptChecksumSha256,
        [Parameter(Mandatory)][string]$ClientInventoryReceiptPath,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ClientInventoryReceiptSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$FinalTrustReceiptSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$FinalManifestKeyReceiptSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{40}$')]
        [string]$TrustRootThumbprint,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$TrustRootSha256,
        [Parameter(Mandatory)][string]$ManifestCurrentKeyName,
        [Parameter(Mandatory)][string]$ManifestNextKeyName,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ManifestCurrentTrustSha256,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ManifestNextTrustSha256,
        [Parameter(Mandatory)][ValidateRange(1, 3)]
        [UInt64]$ActivationEnvironment,
        [Parameter(Mandatory)]
        [ValidateRange(1, [Int64]::MaxValue)]
        [UInt64]$ActivationSequenceFloor,
        [string]$ReceiptRoot = (
            Join-Path $env:ProgramData (
                'RebornSecureNetworkCleanupReceipts')),
        [switch]$AllowTestPath
    )

    $runtime = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\')
    Resolve-RuntimeCleanupReceiptRoot `
        $ReceiptRoot -AllowTestPath:$AllowTestPath | Out-Null
    $receiptPath =
        Get-RebornControlledHostRuntimeCleanupReceiptPath `
            $runtime $ReceiptRoot -AllowTestPath:$AllowTestPath
    if (Test-Path -LiteralPath $receiptPath) {
        throw 'A runtime cleanup receipt already exists.'
    }
    $inventory = @(Get-RuntimeCleanupInventory $runtime)
    $stamp = [IO.Path]::GetFileName($runtime)
    $nonceBytes = [byte[]]::new(8)
    $generator =
        [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($nonceBytes)
        $nonce = ([BitConverter]::ToString(
            $nonceBytes)).Replace('-', '')
    }
    finally {
        [Array]::Clear($nonceBytes, 0, $nonceBytes.Length)
        $generator.Dispose()
    }
    $tombstone = Join-Path (Split-Path -Parent $runtime) (
        ".removing-$stamp-$nonce")
    $now = [DateTimeOffset]::UtcNow.ToString('O')
    $record = [ordered]@{
        schemaVersion = 1
        mode = 'ControlledHostRuntimeCleanup'
        state = 'Prepared'
        createdUtc = $now
        tombstonedUtc = $null
        removedUtc = $null
        runtimeRoot = $runtime
        tombstoneRoot = $tombstone
        runtimeIdentity = $RuntimeIdentity
        runtimeReceiptSha256 =
            $RuntimeReceiptSha256.ToUpperInvariant()
        runtimeReceiptChecksumSha256 =
            $RuntimeReceiptChecksumSha256.ToUpperInvariant()
        clientInventoryReceiptPath =
            [IO.Path]::GetFullPath($ClientInventoryReceiptPath)
        clientInventoryReceiptSha256 =
            $ClientInventoryReceiptSha256.ToUpperInvariant()
        finalTrustReceiptSha256 =
            $FinalTrustReceiptSha256.ToUpperInvariant()
        finalManifestKeyReceiptSha256 =
            $FinalManifestKeyReceiptSha256.ToUpperInvariant()
        trustRootThumbprint = $TrustRootThumbprint.ToUpperInvariant()
        trustRootSha256 = $TrustRootSha256.ToUpperInvariant()
        manifestCurrentKeyName = $ManifestCurrentKeyName
        manifestNextKeyName = $ManifestNextKeyName
        manifestCurrentTrustSha256 =
            $ManifestCurrentTrustSha256.ToUpperInvariant()
        manifestNextTrustSha256 =
            $ManifestNextTrustSha256.ToUpperInvariant()
        activationEnvironment =
            $ActivationEnvironment.ToString(
                [Globalization.CultureInfo]::InvariantCulture)
        activationSequenceFloor =
            $ActivationSequenceFloor.ToString(
                [Globalization.CultureInfo]::InvariantCulture)
        inventorySetSha256 =
            Get-RuntimeCleanupInventorySha256 $inventory
        entries = $inventory
    }
    Write-RebornJsonAtomic $record $receiptPath
    if (-not $AllowTestPath) {
        Assert-RebornProtectedRegularFilePath `
            $receiptPath 'runtime cleanup receipt' | Out-Null
    }
    return Read-RebornControlledHostRuntimeCleanupReceipt `
        $receiptPath -AllowTestPath:$AllowTestPath
}

function Read-RebornControlledHostRuntimeCleanupReceipt {
    param(
        [Parameter(Mandatory)][string]$ReceiptPath,
        [switch]$AllowTestPath
    )

    $path = Assert-RebornSingleLinkRegularFilePath (
        [IO.Path]::GetFullPath($ReceiptPath)
    ) 'runtime cleanup receipt'
    $expectedPath =
        Get-RebornControlledHostRuntimeCleanupReceiptPath `
            ((Get-Content -LiteralPath $path -Raw |
                ConvertFrom-Json).runtimeRoot) `
            (Split-Path -Parent $path) `
            -AllowTestPath:$AllowTestPath
    if (-not $path.Equals(
            $expectedPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime cleanup receipt path is not exact.'
    }
    if (-not $AllowTestPath) {
        Assert-RebornProtectedRegularFilePath `
            $path 'runtime cleanup receipt' | Out-Null
    }
    $record = Get-Content -LiteralPath $path -Raw |
        ConvertFrom-Json
    $expectedProperties = @(
        'schemaVersion', 'mode', 'state', 'createdUtc',
        'tombstonedUtc', 'removedUtc', 'runtimeRoot',
        'tombstoneRoot', 'runtimeIdentity', 'runtimeReceiptSha256',
        'runtimeReceiptChecksumSha256',
        'clientInventoryReceiptPath',
        'clientInventoryReceiptSha256', 'activationEnvironment',
        'activationSequenceFloor', 'finalTrustReceiptSha256',
        'finalManifestKeyReceiptSha256', 'trustRootThumbprint',
        'trustRootSha256', 'manifestCurrentKeyName',
        'manifestNextKeyName', 'manifestCurrentTrustSha256',
        'manifestNextTrustSha256', 'inventorySetSha256', 'entries'
    )
    Assert-ExactPropertySet $record $expectedProperties
    if ($record.schemaVersion -ne 1 -or
        [string]$record.mode -cne
            'ControlledHostRuntimeCleanup' -or
        [string]$record.state -cnotin @(
            'Prepared', 'Tombstoned', 'Removed') -or
        [string]$record.runtimeIdentity -cnotmatch
            '^[0-9A-F]{8}-[0-9A-F]{16}$' -or
        [string]$record.runtimeReceiptSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$record.runtimeReceiptChecksumSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$record.clientInventoryReceiptSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$record.finalTrustReceiptSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$record.finalManifestKeyReceiptSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$record.trustRootThumbprint -cnotmatch
            '^[0-9A-F]{40}$' -or
        [string]$record.trustRootSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$record.manifestCurrentKeyName -cne
            'Reborn-Network-Manifest-Development-Current-v1' -or
        [string]$record.manifestNextKeyName -cne
            'Reborn-Network-Manifest-Development-Next-v1' -or
        [string]$record.manifestCurrentTrustSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$record.manifestNextTrustSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$record.inventorySetSha256 -cnotmatch
            '^[0-9A-F]{64}$') {
        throw 'Runtime cleanup receipt authority is invalid.'
    }
    Assert-CleanupLifecycle $record
    $runtime = [IO.Path]::GetFullPath(
        [string]$record.runtimeRoot).TrimEnd('\')
    $stamp = [IO.Path]::GetFileName($runtime)
    $tombstone = [IO.Path]::GetFullPath(
        [string]$record.tombstoneRoot).TrimEnd('\')
    if ([IO.Path]::GetFileName($tombstone) -cnotmatch
            "^\.removing-$([regex]::Escape($stamp))-[0-9A-F]{16}$" -or
        -not (Split-Path -Parent $tombstone).Equals(
            (Split-Path -Parent $runtime),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime cleanup tombstone scope is invalid.'
    }
    $entries = @($record.entries)
    Assert-CleanupEntries $entries
    if ((Get-RuntimeCleanupInventorySha256 $entries) -cne
        [string]$record.inventorySetSha256) {
        throw 'Runtime cleanup inventory binding is invalid.'
    }
    $finalTrust = @($entries | Where-Object {
        [string]$_.relativePath -ceq
            'tls\current-user-trust-receipt.json'
    })
    $finalKeys = @($entries | Where-Object {
        [string]$_.relativePath -ceq
            'bundle\development-manifest-key-receipt.json'
    })
    if ($finalTrust.Count -ne 1 -or
        $finalKeys.Count -ne 1 -or
        [string]$finalTrust[0].sha256 -cne
            [string]$record.finalTrustReceiptSha256 -or
        [string]$finalKeys[0].sha256 -cne
            [string]$record.finalManifestKeyReceiptSha256) {
        throw 'Runtime cleanup final dependency proof is not inventory-bound.'
    }
    [pscustomobject]@{
        Path = $path
        Record = $record
        RuntimeRoot = $runtime
        TombstoneRoot = $tombstone
        Entries = $entries
    }
}

function Assert-ExactPropertySet {
    param([object]$Value, [string[]]$Expected)

    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count -or
        @($actual | Where-Object { $_ -cnotin $Expected }).Count -ne 0) {
        throw 'Runtime cleanup receipt property set is not exact.'
    }
}

function Assert-CleanupTimestamp {
    param([object]$Value, [string]$Name)

    $parsed = [DateTimeOffset]::MinValue
    if ($Value -isnot [string] -or
        -not [DateTimeOffset]::TryParseExact(
            $Value,
            'O',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero) {
        throw "Runtime cleanup $Name is not an exact UTC timestamp."
    }
    return $parsed
}

function Assert-CleanupLifecycle {
    param([Parameter(Mandatory)][object]$Record)

    $created = Assert-CleanupTimestamp $Record.createdUtc 'createdUtc'
    $tombstoned = $null
    $removed = $null
    if ($null -ne $Record.tombstonedUtc) {
        $tombstoned =
            Assert-CleanupTimestamp $Record.tombstonedUtc 'tombstonedUtc'
    }
    if ($null -ne $Record.removedUtc) {
        $removed =
            Assert-CleanupTimestamp $Record.removedUtc 'removedUtc'
    }
    if (
        ($Record.state -ceq 'Prepared' -and
            ($null -ne $tombstoned -or $null -ne $removed)) -or
        ($Record.state -ceq 'Tombstoned' -and
            ($null -eq $tombstoned -or $null -ne $removed)) -or
        ($Record.state -ceq 'Removed' -and
            ($null -eq $tombstoned -or $null -eq $removed)) -or
        ($null -ne $tombstoned -and $tombstoned -lt $created) -or
        ($null -ne $removed -and $removed -lt $tombstoned)
    ) {
        throw 'Runtime cleanup receipt lifecycle is invalid.'
    }
}

function Assert-CleanupEntries {
    param([Parameter(Mandatory)][object[]]$Entries)

    if ($Entries.Count -gt $script:MaximumEntries) {
        throw 'Runtime cleanup receipt has too many entries.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    [Int64]$total = 0
    foreach ($entry in $Entries) {
        Assert-ExactPropertySet `
            $entry @('relativePath', 'kind', 'length', 'sha256')
        $relative = [string]$entry.relativePath
        if ([string]::IsNullOrWhiteSpace($relative) -or
            [IO.Path]::IsPathRooted($relative) -or
            $relative.Contains(':') -or
            $relative.Split('\').Contains('..') -or
            -not $seen.Add($relative)) {
            throw 'Runtime cleanup receipt has an unsafe relative path.'
        }
        $length = [Int64]$entry.length
        if ([string]$entry.kind -ceq 'Directory') {
            if ($length -ne 0 -or $null -ne $entry.sha256) {
                throw 'Runtime cleanup directory entry is invalid.'
            }
        } elseif ([string]$entry.kind -ceq 'File') {
            if ($length -lt 0 -or
                $length -gt $script:MaximumFileBytes -or
                [string]$entry.sha256 -cnotmatch '^[0-9A-F]{64}$') {
                throw 'Runtime cleanup file entry is invalid.'
            }
            $total += $length
            if ($total -gt $script:MaximumTotalBytes) {
                throw 'Runtime cleanup receipt exceeds its byte budget.'
            }
        } else {
            throw 'Runtime cleanup receipt has an unknown entry kind.'
        }
    }
}

function Set-RuntimeCleanupState {
    param(
        [Parameter(Mandatory)][object]$Receipt,
        [Parameter(Mandatory)]
        [ValidateSet('Tombstoned', 'Removed')]
        [string]$State,
        [switch]$AllowTestPath
    )

    $record = [ordered]@{}
    foreach ($property in $Receipt.Record.PSObject.Properties) {
        $record[$property.Name] = $property.Value
    }
    $now = [DateTimeOffset]::UtcNow.ToString('O')
    $record.state = $State
    if ($State -ceq 'Tombstoned') {
        $record.tombstonedUtc = $now
    } else {
        $record.removedUtc = $now
    }
    Write-RebornJsonAtomic $record $Receipt.Path
    return Read-RebornControlledHostRuntimeCleanupReceipt `
        $Receipt.Path -AllowTestPath:$AllowTestPath
}

function Assert-RuntimeCleanupRemainingTree {
    param(
        [Parameter(Mandatory)][object]$Receipt,
        [Parameter(Mandatory)][string]$Root
    )

    $expected = @{}
    foreach ($entry in $Receipt.Entries) {
        $expected[[string]$entry.relativePath] = $entry
    }
    $remaining = @(Get-RuntimeCleanupInventory $Root)
    foreach ($entry in $remaining) {
        $relative = [string]$entry.relativePath
        if (-not $expected.ContainsKey($relative)) {
            throw 'Runtime cleanup found an unexpected remaining entry.'
        }
        $issued = $expected[$relative]
        if ([string]$entry.kind -cne [string]$issued.kind -or
            [Int64]$entry.length -ne [Int64]$issued.length -or
            [string]$entry.sha256 -cne [string]$issued.sha256) {
            throw 'Runtime cleanup remaining entry does not match authority.'
        }
    }
    return $true
}

Export-ModuleMember -Function @(
    'Get-RebornControlledHostRuntimeCleanupReceiptPath',
    'New-RebornControlledHostRuntimeCleanupReceipt',
    'Read-RebornControlledHostRuntimeCleanupReceipt',
    'Set-RuntimeCleanupState',
    'Assert-RuntimeCleanupRemainingTree'
)
