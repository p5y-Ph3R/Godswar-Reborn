Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkHostsAcl.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkHostsFileIO.psm1'
)

function Get-RebornManagedHostsBytes {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$BeginMarker,
        [Parameter(Mandatory)][string]$Mapping,
        [Parameter(Mandatory)][string]$EndMarker
    )

    $resolved = Assert-RebornSingleLinkRegularFilePath `
        $Path 'hosts byte source'
    $existing = [IO.File]::ReadAllBytes($resolved)
    if ($existing.Length -gt 4MB) {
        throw 'Hosts file exceeds the controlled four-megabyte limit.'
    }
    $addition = $null
    $combined = $null
    try {
        $prefix = if (
            $existing.Length -gt 0 -and
            $existing[$existing.Length - 1] -notin @(10, 13)
        ) {
            "`r`n"
        } else {
            ''
        }
        $addition = [Text.Encoding]::ASCII.GetBytes(
            "$prefix$BeginMarker`r`n$Mapping`r`n$EndMarker`r`n")
        $combined = New-Object byte[] ($existing.Length + $addition.Length)
        [Array]::Copy($existing, 0, $combined, 0, $existing.Length)
        [Array]::Copy(
            $addition,
            0,
            $combined,
            $existing.Length,
            $addition.Length)
        return ,$combined
    }
    finally {
        [Array]::Clear($existing, 0, $existing.Length)
        if ($null -ne $addition) {
            [Array]::Clear($addition, 0, $addition.Length)
        }
    }
}

function Get-RebornDevelopmentHostsState {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$BeginMarker,
        [Parameter(Mandatory)][string]$Mapping,
        [Parameter(Mandatory)][string]$EndMarker,
        [Parameter(Mandatory)][string[]]$ManagedNames
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Hosts file not found: $Path"
    }
    $resolved = Assert-RebornSingleLinkRegularFilePath $Path 'hosts file'
    $lines = [IO.File]::ReadAllLines($resolved)
    $beginIndexes = @()
    $endIndexes = @()
    $managedOccurrences = @()

    for ($index = 0; $index -lt $lines.Length; $index++) {
        $trimmed = $lines[$index].Trim()
        if ($trimmed -ceq $BeginMarker) {
            $beginIndexes += $index
        }
        if ($trimmed -ceq $EndMarker) {
            $endIndexes += $index
        }
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) {
            continue
        }

        $active = ($trimmed -split '#', 2)[0].Trim()
        $parts = @($active -split '\s+' | Where-Object { $_ })
        if ($parts.Count -lt 2) {
            continue
        }
        foreach ($name in $ManagedNames) {
            if ($parts[1..($parts.Count - 1)] -contains $name) {
                $managedOccurrences += [pscustomobject]@{
                    Line = $index
                    Address = $parts[0]
                    Name = $name
                }
            }
        }
    }

    $exactBlock = (
        $beginIndexes.Count -eq 1 -and
        $endIndexes.Count -eq 1 -and
        $endIndexes[0] -eq ($beginIndexes[0] + 2) -and
        $lines[$beginIndexes[0] + 1].Trim() -ceq $Mapping
    )
    $managedBlockLines = if ($exactBlock) {
        @($beginIndexes[0], ($beginIndexes[0] + 1), $endIndexes[0])
    } else {
        @()
    }
    $outsideManagedBlock = @(
        $managedOccurrences |
            Where-Object { $managedBlockLines -notcontains $_.Line }
    )

    $state = if (
        $exactBlock -and
        $outsideManagedBlock.Count -eq 0 -and
        $managedOccurrences.Count -eq $ManagedNames.Count
    ) {
        'InstalledExact'
    } elseif (
        $beginIndexes.Count -eq 0 -and
        $endIndexes.Count -eq 0 -and
        $managedOccurrences.Count -eq 0
    ) {
        'Absent'
    } else {
        'Conflict'
    }

    [pscustomobject]@{
        State = $state
        Sha256 = Get-RebornHostsFileSha256 $resolved
        ManagedOccurrences = $managedOccurrences.Count
    }
}

function Test-RebornHostsSha256 {
    param([object]$Value)

    return $Value -is [string] -and
        $Value -cmatch '^[0-9A-F]{64}$'
}

function Test-RebornHostsInterruptedManagedPrefix {
    param(
        [Parameter(Mandatory)][string]$CurrentPath,
        [Parameter(Mandatory)][string]$BackupPath,
        [Parameter(Mandatory)][string]$BeginMarker,
        [Parameter(Mandatory)][string]$Mapping,
        [Parameter(Mandatory)][string]$EndMarker
    )

    $current = [IO.File]::ReadAllBytes($CurrentPath)
    $original = [IO.File]::ReadAllBytes($BackupPath)
    $intended = Get-RebornManagedHostsBytes `
        $BackupPath $BeginMarker $Mapping $EndMarker
    try {
        if (
            $current.Length -le $original.Length -or
            $current.Length -ge $intended.Length
        ) {
            return $false
        }
        for ($index = 0; $index -lt $current.Length; $index++) {
            if ($current[$index] -ne $intended[$index]) {
                return $false
            }
        }
        return $true
    }
    finally {
        [Array]::Clear($current, 0, $current.Length)
        [Array]::Clear($original, 0, $original.Length)
        [Array]::Clear($intended, 0, $intended.Length)
    }
}

function Read-RebornHostsReceipt {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedHostsPath,
        [Parameter(Mandatory)][string]$BeginMarker,
        [Parameter(Mandatory)][string]$Mapping,
        [Parameter(Mandatory)][string]$EndMarker
    )

    $resolved = Assert-RebornSingleLinkRegularFilePath `
        $Path 'active hosts receipt'
    $issuedReceipt = [IO.Path]::GetFullPath(
        (Join-Path $env:ProgramData (
            'RebornSecureNetworkBackups\' +
            'development-hosts\' +
            'development-hosts-receipt.json')))
    $production = $resolved.Equals(
        $issuedReceipt,
        [StringComparison]::OrdinalIgnoreCase)
    if ($production) {
        Assert-RebornDevelopmentHostsArtifactReadAcl `
            (Split-Path -Parent $resolved) | Out-Null
        Assert-RebornDevelopmentHostsArtifactReadAcl `
            $resolved -File | Out-Null
    }
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Length -lt 2 -or $item.Length -gt 16384) {
        throw 'Active hosts receipt size is outside its bounded range.'
    }
    try {
        $record = Get-Content -LiteralPath $resolved -Raw -Encoding utf8 |
            ConvertFrom-Json
    }
    catch {
        throw 'Active hosts receipt is not valid JSON.'
    }

    $states = @(
        'Prepared',
        'InstalledExact',
        'Restoring',
        'PendingDnsFlush',
        'RolledBack',
        'Restored')
    $readerSid =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if (
        $record.schemaVersion -ne 3 -or
        $record.state -isnot [string] -or
        $states -cnotcontains $record.state -or
        [string]$record.readerSid -cne $readerSid -or
        -not ([IO.Path]::GetFullPath(
                [string]$record.hostsPath).Equals(
                $ExpectedHostsPath,
                [StringComparison]::OrdinalIgnoreCase)) -or
        -not (Test-RebornHostsSha256 $record.originalSha256) -or
        -not (Test-RebornHostsSha256 $record.backupSha256) -or
        -not (Test-RebornHostsSha256 $record.intendedAppliedSha256) -or
        [string]$record.backupSha256 -cne
            [string]$record.originalSha256
    ) {
        throw 'The active hosts receipt is malformed or outside policy.'
    }

    $receiptDirectory = [IO.Path]::GetFullPath(
        (Split-Path -Parent $resolved)).TrimEnd('\')
    $backup = [IO.Path]::GetFullPath([string]$record.backupPath)
    if (
        -not ([IO.Path]::GetDirectoryName($backup)).Equals(
            $receiptDirectory,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $backup -PathType Leaf)
    ) {
        throw 'The hosts backup is absent or outside its protected root.'
    }
    $backup = Assert-RebornSingleLinkRegularFilePath `
        $backup 'hosts backup'
    if ($production) {
        Assert-RebornDevelopmentHostsArtifactReadAcl `
            $backup -File | Out-Null
    }
    if ((Get-RebornHostsFileSha256 $backup) -cne
            [string]$record.backupSha256) {
        throw 'The hosts backup changed after capture.'
    }

    $intended = Get-RebornManagedHostsBytes `
        $backup $BeginMarker $Mapping $EndMarker
    try {
        if ((Get-RebornHostsByteSha256 $intended) -cne
                [string]$record.intendedAppliedSha256) {
            throw 'The active hosts receipt intended hash is not authoritative.'
        }
    }
    finally {
        [Array]::Clear($intended, 0, $intended.Length)
    }

    $applied = $record.appliedSha256
    $pendingCompletion = if (
        $null -ne $record.PSObject.Properties['pendingCompletion']
    ) {
        [string]$record.pendingCompletion
    } else {
        $null
    }
    if (
        ($null -ne $applied -and
            (-not (Test-RebornHostsSha256 $applied) -or
                [string]$applied -cne
                    [string]$record.intendedAppliedSha256)) -or
        ($record.state -in @(
                'InstalledExact',
                'Restoring',
                'Restored') -and
            [string]$applied -cne
                [string]$record.intendedAppliedSha256) -or
        ($record.state -eq 'PendingDnsFlush' -and
            $pendingCompletion -notin @('RolledBack', 'Restored')) -or
        ($record.state -eq 'PendingDnsFlush' -and
            $pendingCompletion -eq 'Restored' -and
            [string]$applied -cne
                [string]$record.intendedAppliedSha256)
    ) {
        throw 'The active hosts receipt applied hash is inconsistent.'
    }

    [pscustomobject]@{
        Path = $resolved
        Directory = $receiptDirectory
        BackupPath = $backup
        Record = $record
    }
}

function Move-RebornHostsReceiptToHistory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$State
    )

    if ($State -notin @('RolledBack', 'Restored')) {
        throw 'Only a completed hosts receipt can enter history.'
    }
    $resolved = Assert-RebornSingleLinkRegularFilePath `
        $Path 'completed hosts receipt'
    $directory = Split-Path -Parent $resolved
    $base = [IO.Path]::GetFileNameWithoutExtension($resolved)
    $history = Join-Path $directory (
        "$base.history-" +
        [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmssfff') + '-' +
        [guid]::NewGuid().ToString('N') + "-$State.json")
    [IO.File]::Move($resolved, $history)
    return Assert-RebornSingleLinkRegularFilePath `
        $history 'hosts receipt history'
}

function Set-RebornHostsReceiptPendingDnsFlush {
    param(
        [Parameter(Mandatory)][object]$Loaded,
        [ValidateSet('RolledBack', 'Restored')]
        [string]$Completion
    )

    $record = $Loaded.Record
    $record.state = 'PendingDnsFlush'
    $record | Add-Member `
        -NotePropertyName pendingCompletion `
        -NotePropertyValue $Completion `
        -Force
    $record | Add-Member `
        -NotePropertyName dnsFlushPendingUtc `
        -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) `
        -Force
    if ($Completion -eq 'RolledBack') {
        $record.rolledBackUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } else {
        $record.restoredUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    Write-RebornHostsReceiptAtomic $record $Loaded.Path
    return $record
}

function Complete-RebornHostsReceipt {
    param(
        [Parameter(Mandatory)][object]$Loaded,
        [ValidateSet('RolledBack', 'Restored')]
        [string]$State,
        [switch]$DnsFlushed
    )

    $record = $Loaded.Record
    $record.state = $State
    if ($State -eq 'RolledBack') {
        $record.rolledBackUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } else {
        $record.restoredUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    if ($DnsFlushed) {
        $record | Add-Member `
            -NotePropertyName dnsFlushedUtc `
            -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) `
            -Force
    }
    Write-RebornHostsReceiptAtomic $record $Loaded.Path
    return Move-RebornHostsReceiptToHistory $Loaded.Path $State
}

Export-ModuleMember -Function @(
    'Get-RebornManagedHostsBytes',
    'Get-RebornDevelopmentHostsState',
    'Test-RebornHostsInterruptedManagedPrefix',
    'Read-RebornHostsReceipt',
    'Move-RebornHostsReceiptToHistory',
    'Set-RebornHostsReceiptPendingDnsFlush',
    'Complete-RebornHostsReceipt'
)
