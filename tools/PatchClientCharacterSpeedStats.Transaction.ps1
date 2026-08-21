Set-StrictMode -Version Latest

function Write-BytesAtomic(
    [string]$Path,
    [byte[]]$Data,
    [string]$ExpectedCurrentHash,
    [bool]$ExpectedCurrentAbsent = $false,
    [switch]$VerifyCurrent
) {
    $temporary = "$Path.character-stats-$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temporary, $Data)
        if ($VerifyCurrent) {
            $exists = Test-Path -LiteralPath $Path -PathType Leaf
            if ($ExpectedCurrentAbsent) {
                if ($exists) {
                    throw 'destination appeared while replacement was staged'
                }
            } elseif (-not $exists -or
                (Get-FileSha256 $Path) -ne $ExpectedCurrentHash) {
                throw 'destination changed while replacement was staged'
            }
        }
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Get-Utf8Bytes(
    [string]$Text,
    [bool]$IncludeBom = $false
) {
    [byte[]]$body = [Text.UTF8Encoding]::new($false, $true).GetBytes($Text)
    if (-not $IncludeBom) { return $body }
    [byte[]]$result = [byte[]]::new($body.Length + 3)
    $result[0] = 0xEF
    $result[1] = 0xBB
    $result[2] = 0xBF
    [Array]::Copy($body, 0, $result, 3, $body.Length)
    return $result
}

function Test-Utf8Bom([string]$Path) {
    [byte[]]$prefix = [byte[]]::new(3)
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Read($prefix, 0, 3) -ne 3) { return $false }
    }
    finally {
        $stream.Dispose()
    }
    return $prefix[0] -eq 0xEF -and $prefix[1] -eq 0xBB -and
        $prefix[2] -eq 0xBF
}

function Get-BytesSha256([byte[]]$Data) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($Data)).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-FileSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Add-CharacterStatsMutation(
    [Collections.Generic.List[object]]$Journal,
    [object]$Snapshot,
    [string]$ExpectedHash,
    [bool]$ExpectedAbsent
) {
    $Journal.Add([pscustomobject]@{
            Label = $Snapshot.Label
            Path = $Snapshot.Path
            Backup = $Snapshot.Backup
            PreHash = $Snapshot.Hash
            PreAbsent = $Snapshot.WasAbsent
            WrittenHash = $ExpectedHash
            WrittenAbsent = $ExpectedAbsent
        }) | Out-Null
}

function Assert-CharacterStatsSnapshotCurrent([object]$Snapshot) {
    if ($Snapshot.WasAbsent) {
        if (Test-Path -LiteralPath $Snapshot.Path -PathType Leaf) {
            throw "$($Snapshot.Label) appeared after its backup was verified."
        }
        return
    }
    if (-not (Test-Path -LiteralPath $Snapshot.Path -PathType Leaf) -or
        (Get-FileSha256 $Snapshot.Path) -ne $Snapshot.Hash) {
        throw "$($Snapshot.Label) changed after its backup was verified."
    }
}

function Restore-CharacterStatsMutation(
    [object]$Mutation
) {
    $exists = Test-Path -LiteralPath $Mutation.Path -PathType Leaf
    $currentHash = if ($exists) { Get-FileSha256 $Mutation.Path } else { $null }
    $isPreState = if ($Mutation.PreAbsent) {
        -not $exists
    } else {
        $exists -and $currentHash -eq $Mutation.PreHash
    }
    if ($isPreState) { return }
    $isWrittenState = if ($Mutation.WrittenAbsent) {
        -not $exists
    } else {
        $exists -and $currentHash -eq $Mutation.WrittenHash
    }
    if (-not $isWrittenState) {
        throw 'target changed after the transaction write'
    }
    if ($null -ne $Mutation.Backup) {
        [byte[]]$backupBytes = [IO.File]::ReadAllBytes($Mutation.Backup)
        if ((Get-BytesSha256 $backupBytes) -ne $Mutation.PreHash) {
            throw 'verified backup changed before rollback'
        }
        Write-BytesAtomic $Mutation.Path $backupBytes (
            $Mutation.WrittenHash) $Mutation.WrittenAbsent -VerifyCurrent
    } elseif ($exists) {
        Remove-Item -LiteralPath $Mutation.Path -Force
    }
}
