Set-StrictMode -Version Latest

function Get-ProgressiveTalentBytesSha256([byte[]]$Data) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($Data)).Replace('-', '')
    }
    finally { $algorithm.Dispose() }
}

function Get-ProgressiveTalentFileSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-ProgressiveTalentPathWithin(
    [string]$Path,
    [string]$Root
) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootWithoutSeparator = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $fullRoot = $rootWithoutSeparator +
        [IO.Path]::DirectorySeparatorChar
    return [string]::Equals(
        $fullPath, $rootWithoutSeparator,
        [StringComparison]::OrdinalIgnoreCase) -or $fullPath.StartsWith(
        $fullRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ProgressiveTalentClientRootPolicy([string]$ClientRoot) {
    $fullRoot = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\', '/')
    $protected = [IO.Path]::GetFullPath(
        'C:\Godswar Origin B20H').TrimEnd('\', '/')
    $insideProtected = Test-ProgressiveTalentPathWithin $fullRoot $protected
    if ([string]::Equals(
            $fullRoot, $protected,
            [StringComparison]::OrdinalIgnoreCase) -or
        $insideProtected) {
        throw 'The protected B20H client is never a talent-tooltip patch target.'
    }
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        throw "Client root is missing: $fullRoot"
    }
    $driveRoot = [IO.Path]::GetPathRoot($fullRoot).TrimEnd('\', '/')
    $cursor = $fullRoot
    while ($true) {
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Client root must not traverse a reparse point.'
        }
        if ([string]::Equals(
                $cursor.TrimEnd('\', '/'), $driveRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $cursor = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($cursor)) {
            throw 'Client root escaped its filesystem root.'
        }
    }
}

function Assert-ProgressiveTalentClientClosed(
    [scriptblock]$ProcessProvider = $null
) {
    $processes = @(if ($null -ne $ProcessProvider) {
            & $ProcessProvider
        } else {
            Get-Process -Name 'Origin', 'Launch', 'Patcher' `
                -ErrorAction SilentlyContinue
        })
    if ($processes.Count -eq 0) { return }
    try {
        $description = ($processes | ForEach-Object {
                $name = if ($_.PSObject.Properties['ProcessName']) {
                    $_.ProcessName
                } else { 'unknown' }
                $id = if ($_.PSObject.Properties['Id']) { $_.Id } else { '?' }
                "$name (PID $id)"
            }) -join ', '
        throw "Close Origin and its launcher before changing talent tooltips: $description."
    }
    finally {
        foreach ($process in $processes) {
            if ($process -is [IDisposable]) { $process.Dispose() }
        }
    }
}

function Write-ProgressiveTalentBytesAtomic(
    [string]$Path,
    [byte[]]$Data,
    [string]$ExpectedCurrentHash
) {
    $temporary = "$Path.progressive-talents-$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temporary, $Data)
        if ((Get-ProgressiveTalentFileSha256 $temporary) -ne
            (Get-ProgressiveTalentBytesSha256 $Data)) {
            throw 'staged file hash mismatch'
        }
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
            (Get-ProgressiveTalentFileSha256 $Path) -ne
                $ExpectedCurrentHash) {
            throw 'destination changed while replacement was staged'
        }
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Write-ProgressiveTalentJsonAtomic([string]$Path, [object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 6
    [byte[]]$bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes(
        $json + "`r`n")
    $temporary = "$Path.progressive-talents-$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temporary, $bytes)
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function New-ProgressiveTalentSnapshot(
    [string]$Label,
    [string]$RelativePath,
    [string]$BackupName,
    [string]$Path,
    [byte[]]$Before,
    [byte[]]$After
) {
    return [pscustomobject]@{
        Label = $Label
        RelativePath = $RelativePath
        BackupName = $BackupName
        Path = [IO.Path]::GetFullPath($Path)
        Before = $Before
        After = $After
        BeforeSha256 = Get-ProgressiveTalentBytesSha256 $Before
        AfterSha256 = Get-ProgressiveTalentBytesSha256 $After
    }
}

function Assert-ProgressiveTalentSnapshotCurrent([object]$Snapshot) {
    if (-not (Test-Path -LiteralPath $Snapshot.Path -PathType Leaf) -or
        (Get-ProgressiveTalentFileSha256 $Snapshot.Path) -ne
            $Snapshot.BeforeSha256) {
        throw "$($Snapshot.Label) changed after it was inspected."
    }
}

function New-ProgressiveTalentApplyReceipt(
    [string]$ClientRoot,
    [object[]]$Snapshots
) {
    return [pscustomobject]@{
        SchemaVersion = 1
        PatchId = 'reborn.progressive-talent-tooltips.v1'
        Outcome = 'Prepared'
        CreatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        ClientRoot = [IO.Path]::GetFullPath($ClientRoot)
        Files = @($Snapshots | ForEach-Object {
                [pscustomobject]@{
                    Label = $_.Label
                    RelativePath = $_.RelativePath
                    BackupName = $_.BackupName
                    BeforeLength = $_.Before.Length
                    BeforeSha256 = $_.BeforeSha256
                    AfterLength = $_.After.Length
                    AfterSha256 = $_.AfterSha256
                }
            })
    }
}

function Restore-ProgressiveTalentAppliedWrites(
    [object[]]$Written,
    [Collections.Generic.List[string]]$Errors
) {
    for ($index = $Written.Count - 1; $index -ge 0; $index--) {
        $snapshot = $Written[$index]
        try {
            $current = Get-ProgressiveTalentFileSha256 $snapshot.Path
            if ($current -eq $snapshot.BeforeSha256) { continue }
            if ($current -ne $snapshot.AfterSha256) {
                throw 'target changed after transaction write'
            }
            Write-ProgressiveTalentBytesAtomic $snapshot.Path (
                $snapshot.Before) $snapshot.AfterSha256
            if ((Get-ProgressiveTalentFileSha256 $snapshot.Path) -ne
                $snapshot.BeforeSha256) {
                throw 'restored hash mismatch'
            }
        }
        catch { $Errors.Add("$($snapshot.Label): $($_.Exception.Message)") }
    }
}

function Invoke-ProgressiveTalentApplyTransaction(
    [string]$ClientRoot,
    [string]$BackupRoot,
    [object[]]$Snapshots,
    [scriptblock]$InternalTestBeforeCommit = $null,
    [scriptblock]$InternalTestAfterWrite = $null
) {
    $fullClientRoot = [IO.Path]::GetFullPath($ClientRoot)
    $fullBackupRoot = [IO.Path]::GetFullPath($BackupRoot)
    if (Test-ProgressiveTalentPathWithin $fullBackupRoot $fullClientRoot) {
        throw 'Talent-tooltip backups must be outside the client directory.'
    }
    Assert-ProgressiveTalentClientClosed
    foreach ($snapshot in $Snapshots) {
        Assert-ProgressiveTalentSnapshotCurrent $snapshot
    }
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')
    $directory = Join-Path $fullBackupRoot (
        "progressive-talent-tooltips-$stamp-" +
        [guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    foreach ($snapshot in $Snapshots) {
        $backup = Join-Path $directory $snapshot.BackupName
        if (Test-Path -LiteralPath $backup) {
            throw "Backup already exists: $backup"
        }
        Copy-Item -LiteralPath $snapshot.Path -Destination $backup
        if ((Get-ProgressiveTalentFileSha256 $backup) -ne
            $snapshot.BeforeSha256) {
            throw "$($snapshot.Label) backup verification failed."
        }
    }
    $receipt = New-ProgressiveTalentApplyReceipt $fullClientRoot $Snapshots
    $receiptPath = Join-Path $directory 'receipt.json'
    Write-ProgressiveTalentJsonAtomic $receiptPath $receipt
    $written = [Collections.Generic.List[object]]::new()
    try {
        if ($null -ne $InternalTestBeforeCommit) {
            & $InternalTestBeforeCommit
        }
        Assert-ProgressiveTalentClientClosed
        foreach ($snapshot in $Snapshots) {
            Assert-ProgressiveTalentClientClosed
            Assert-ProgressiveTalentSnapshotCurrent $snapshot
            if ($snapshot.BeforeSha256 -eq $snapshot.AfterSha256) {
                continue
            }
            Write-ProgressiveTalentBytesAtomic $snapshot.Path (
                $snapshot.After) $snapshot.BeforeSha256
            $written.Add($snapshot)
            if ((Get-ProgressiveTalentFileSha256 $snapshot.Path) -ne
                $snapshot.AfterSha256) {
                throw "$($snapshot.Label) post-write hash mismatch."
            }
            if ($null -ne $InternalTestAfterWrite) {
                & $InternalTestAfterWrite $snapshot.Label
            }
        }
        $receipt.Outcome = 'Applied'
        Write-ProgressiveTalentJsonAtomic $receiptPath $receipt
    }
    catch {
        $failure = $_
        $rollbackErrors = [Collections.Generic.List[string]]::new()
        Restore-ProgressiveTalentAppliedWrites @($written) $rollbackErrors
        $receipt.Outcome = if ($rollbackErrors.Count -eq 0) {
            'AutoRolledBack'
        } else { 'AutoRollbackFailed' }
        try { Write-ProgressiveTalentJsonAtomic $receiptPath $receipt }
        catch { $rollbackErrors.Add("receipt: $($_.Exception.Message)") }
        if ($rollbackErrors.Count -gt 0) {
            throw ($failure.Exception.Message + ' Rollback failures: ' +
                ($rollbackErrors -join '; '))
        }
        throw $failure
    }
    return [pscustomobject]@{
        Status = 'Patched'
        Changed = $true
        Backup = $directory
        Receipt = $receiptPath
    }
}

function Read-ProgressiveTalentReceipt(
    [string]$ReceiptPath,
    [string]$ClientRoot
) {
    $fullReceipt = [IO.Path]::GetFullPath($ReceiptPath)
    if (-not (Test-Path -LiteralPath $fullReceipt -PathType Leaf)) {
        throw "Talent-tooltip receipt is missing: $fullReceipt"
    }
    try { $receipt = Get-Content -LiteralPath $fullReceipt -Raw |
            ConvertFrom-Json }
    catch { throw "Talent-tooltip receipt is invalid JSON: $($_.Exception.Message)" }
    $fullClient = [IO.Path]::GetFullPath($ClientRoot)
    if ($receipt.SchemaVersion -ne 1 -or
        $receipt.PatchId -ne 'reborn.progressive-talent-tooltips.v1' -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$receipt.ClientRoot), $fullClient,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Talent-tooltip receipt identity/client root is invalid.'
    }
    $expected = @{
        'Origin.exe' = @('Origin.exe', 'Origin.exe')
        'en_us Skill.ini' = @(
            'Localization\en_us\Settings\Sys\Skill.ini', 'en_us-Skill.ini')
        'zh_cn Skill.ini' = @(
            'Localization\zh_cn\Settings\Sys\Skill.ini', 'zh_cn-Skill.ini')
    }
    $binaryProfile = Get-ProgressiveTalentBinaryProfile
    $enProfile = Get-ProgressiveTalentSkillProfile 'en_us'
    $zhProfile = Get-ProgressiveTalentSkillProfile 'zh_cn'
    $knownHashes = @{
        'Origin.exe' = [pscustomobject]@{
            Before = @($binaryProfile.SourceSha256)
            After = $binaryProfile.PatchedSha256
            BeforeLengths = @($binaryProfile.Length)
            AfterLength = $binaryProfile.Length
        }
        'en_us Skill.ini' = [pscustomobject]@{
            Before = @($enProfile.StockSha256, $enProfile.TooltipSha256)
            After = $enProfile.StockSha256
            BeforeLengths = @(23796, 23856)
            AfterLength = 23796
        }
        'zh_cn Skill.ini' = [pscustomobject]@{
            Before = @($zhProfile.StockSha256)
            After = $zhProfile.StockSha256
            BeforeLengths = @(21760)
            AfterLength = 21760
        }
    }
    $files = @($receipt.Files)
    if ($files.Count -ne $expected.Count) {
        throw 'Talent-tooltip receipt must describe exactly three files.'
    }
    $directory = Split-Path -Parent $fullReceipt
    $seen = @{}
    $records = @()
    foreach ($file in $files) {
        $label = [string]$file.Label
        if (-not $expected.ContainsKey($label) -or $seen.ContainsKey($label) -or
            [string]$file.RelativePath -cne $expected[$label][0] -or
            [string]$file.BackupName -cne $expected[$label][1]) {
            throw 'Talent-tooltip receipt file mapping is invalid.'
        }
        $pins = $knownHashes[$label]
        if ([string]$file.BeforeSha256 -notin $pins.Before -or
            [string]$file.AfterSha256 -cne $pins.After -or
            [int64]$file.BeforeLength -notin $pins.BeforeLengths -or
            [int64]$file.AfterLength -ne $pins.AfterLength) {
            throw "Talent-tooltip receipt hashes/lengths are not pinned for $label."
        }
        $seen[$label] = $true
        $path = [IO.Path]::GetFullPath((Join-Path $fullClient (
                    [string]$file.RelativePath)))
        $backup = [IO.Path]::GetFullPath((Join-Path $directory (
                    [string]$file.BackupName)))
        if (-not (Test-ProgressiveTalentPathWithin $path $fullClient) -or
            -not (Test-ProgressiveTalentPathWithin $backup $directory) -or
            -not (Test-Path -LiteralPath $backup -PathType Leaf) -or
            (Get-ProgressiveTalentFileSha256 $backup) -ne
                [string]$file.BeforeSha256 -or
            (Get-Item -LiteralPath $backup).Length -ne
                [int64]$file.BeforeLength) {
            throw "Talent-tooltip receipt backup is invalid for $label."
        }
        $records += [pscustomobject]@{
            Label = $label
            Path = $path
            Backup = $backup
            BeforeSha256 = [string]$file.BeforeSha256
            AfterSha256 = [string]$file.AfterSha256
            Before = [IO.File]::ReadAllBytes($backup)
        }
    }
    return [pscustomobject]@{
        Path = $fullReceipt
        Value = $receipt
        Records = $records
    }
}

function Invoke-ProgressiveTalentReceiptRollback(
    [string]$ClientRoot,
    [string]$ReceiptPath,
    [scriptblock]$InternalTestAfterWrite = $null
) {
    $validated = Read-ProgressiveTalentReceipt $ReceiptPath $ClientRoot
    $records = @($validated.Records)
    $current = @($records | ForEach-Object {
            if (-not (Test-Path -LiteralPath $_.Path -PathType Leaf)) {
                throw "$($_.Label) is missing during rollback."
            }
            Get-ProgressiveTalentFileSha256 $_.Path
        })
    $allBefore = $true
    $allAfter = $true
    for ($index = 0; $index -lt $records.Count; $index++) {
        $allBefore = $allBefore -and
            $current[$index] -eq $records[$index].BeforeSha256
        $allAfter = $allAfter -and
            $current[$index] -eq $records[$index].AfterSha256
    }
    if ($allBefore) {
        return [pscustomobject]@{
            Status = 'Already rolled back'; Changed = $false
            Receipt = $validated.Path
        }
    }
    if (-not $allAfter -or $validated.Value.Outcome -notin @(
            'Applied', 'RolledBack')) {
        throw 'Receipt rollback refused a changed, mixed, or incomplete state.'
    }
    Assert-ProgressiveTalentClientClosed
    $written = [Collections.Generic.List[object]]::new()
    try {
        for ($index = 0; $index -lt $records.Count; $index++) {
            $record = $records[$index]
            if ($record.BeforeSha256 -eq $record.AfterSha256) {
                continue
            }
            Assert-ProgressiveTalentClientClosed
            [byte[]]$after = [IO.File]::ReadAllBytes($record.Path)
            Write-ProgressiveTalentBytesAtomic $record.Path (
                $record.Before) $record.AfterSha256
            $written.Add([pscustomobject]@{
                    Label = $record.Label; Path = $record.Path
                    Before = $after; BeforeSha256 = $record.AfterSha256
                    AfterSha256 = $record.BeforeSha256
                })
            if ((Get-ProgressiveTalentFileSha256 $record.Path) -ne
                $record.BeforeSha256) {
                throw "$($record.Label) rollback post-write hash mismatch."
            }
            if ($null -ne $InternalTestAfterWrite) {
                & $InternalTestAfterWrite $record.Label
            }
        }
    }
    catch {
        $failure = $_
        $errors = [Collections.Generic.List[string]]::new()
        Restore-ProgressiveTalentAppliedWrites @($written) $errors
        if ($errors.Count -gt 0) {
            throw ($failure.Exception.Message + ' Rollback recovery failures: ' +
                ($errors -join '; '))
        }
        throw $failure
    }
    $validated.Value.Outcome = 'RolledBack'
    Write-ProgressiveTalentJsonAtomic $validated.Path $validated.Value
    return [pscustomobject]@{
        Status = 'Rolled back'; Changed = $true
        Receipt = $validated.Path
    }
}
