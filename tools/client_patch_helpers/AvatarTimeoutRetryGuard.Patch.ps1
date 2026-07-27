function Get-AvatarTimeoutRetrySha256 {
    param([byte[]]$Data)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return (($algorithm.ComputeHash($Data) |
            ForEach-Object { $_.ToString('X2') }) -join '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-AvatarTimeoutRetryRelativeBranch {
    param(
        [byte[]]$Code,
        [int]$InstructionOffset,
        [uint64]$CodeVa,
        [uint64]$ExpectedTarget,
        [byte]$ExpectedOpcode
    )

    if ($InstructionOffset -lt 0 -or
        $InstructionOffset + 5 -gt $Code.Length -or
        $Code[$InstructionOffset] -ne $ExpectedOpcode) {
        throw 'Internal avatar timeout/retry branch encoding is invalid.'
    }

    $target = $CodeVa + $InstructionOffset + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
    if ($target -ne $ExpectedTarget) {
        throw "Internal avatar timeout/retry branch targets 0x$(
            '{0:X8}' -f $target), expected 0x$(
            '{0:X8}' -f $ExpectedTarget)."
    }
}

function Assert-AvatarTimeoutRetryShortBranches {
    param(
        [byte[]]$Code,
        [int[]]$InstructionOffsets,
        [int]$ExpectedTargetOffset
    )

    foreach ($offset in $InstructionOffsets) {
        if ($offset -lt 0 -or $offset + 2 -gt $Code.Length -or
            $Code[$offset] -ne 0x74) {
            throw 'Internal avatar timeout/retry short branch is invalid.'
        }

        $target = $offset + 2 + [int][sbyte]$Code[$offset + 1]
        if ($target -ne $ExpectedTargetOffset) {
            throw "Internal avatar timeout/retry short branch targets 0x$(
                '{0:X}' -f $target), expected 0x$(
                '{0:X}' -f $ExpectedTargetOffset)."
        }
    }
}

function Assert-AvatarTimeoutRetryProcessClosed {
    param([string]$ResolvedClientExe)

    $processName = [IO.Path]::GetFileNameWithoutExtension($ResolvedClientExe)
    $running = @(
        Get-Process -Name $processName -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    [string]::Equals(
                        $_.Path,
                        $ResolvedClientExe,
                        [StringComparison]::OrdinalIgnoreCase)
                }
                catch {
                    # An inaccessible matching process may still hold the file.
                    $true
                }
            }
    )
    if ($running.Count -gt 0) {
        throw "$([IO.Path]::GetFileName($ResolvedClientExe)) is running. Close it before changing the executable."
    }
}

function Assert-AvatarTimeoutRetryAllowedMutation {
    param(
        [byte[]]$Before,
        [byte[]]$After,
        [object[]]$AllowedRanges,
        [int]$ExpectedMutationCount,
        [string]$ExpectedHash,
        [string]$Label
    )

    if ($Before.Length -ne $After.Length) {
        throw "$Label changed the Origin.exe length."
    }

    $mutationCount = 0
    for ($offset = 0; $offset -lt $After.Length; $offset++) {
        if ($Before[$offset] -eq $After[$offset]) {
            continue
        }

        $mutationCount++
        if (-not (Test-AllowedDifference $offset $AllowedRanges)) {
            throw "$Label contains an unexpected mutation at file offset 0x$(
                '{0:X}' -f $offset)."
        }
    }

    if ($mutationCount -ne $ExpectedMutationCount) {
        throw "$Label changed $mutationCount bytes; expected $ExpectedMutationCount."
    }
    if ((Get-AvatarTimeoutRetrySha256 $After) -ne $ExpectedHash) {
        throw "$Label failed exact SHA-256 verification."
    }

    return $mutationCount
}

function Invoke-AvatarTimeoutRetryGuardPatch {
    param(
        [string]$ClientExe,
        [string]$Mode,
        [string]$BackupRoot,
        [string]$RepositoryRoot
    )

    $priorSha256 =
        '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
    $patchedSha256 =
        'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C'
    $expectedLength = 6676480
    $expectedMachine = 0x014C
    $expectedOptionalMagic = 0x010B
    $expectedImageBase = 0x00400000

    # In LOGIN state 2, guard exactly the two roots dereferenced by this stock
    # timeout path. Requiring four unrelated preview roots incorrectly diverted
    # valid transitions. Other lifecycle states and both-ready state 2 replay
    # the displaced load and untouched stock continuation. Scoped missing state
    # preserves the stock state-2 writes, skips both unsafe calls, and rejoins
    # stock cleanup.
    $hookOffset = 0x1F58B6
    $hookVa = 0x005F58B6
    $caveOffset = 0x5C341F
    $caveVa = 0x009C341F
    $caveReserveLength = 96
    $normalContinuationOffset = 0x1F58BC
    $normalContinuationVa = 0x005F58BC
    $missingContinuationOffset = 0x1F58EA
    $missingContinuationVa = 0x005F58EA

    $originalHook = Convert-HexBytes '8B 0D A0 60 57 01'
    $patchedHook = Convert-HexBytes 'E9 64 DB 3C 00 90'
    $caveCode = Convert-HexBytes @'
83 3D 4C 5F 57 01 02 75 12
A1 A0 60 57 01 85 C0 74 14
A1 8C 60 57 01 85 C0 74 0B
8B 0D A0 60 57 01 E9 77 24 C3 FF
BF 02 00 00 00 C6 05 66 5C 57 01 01
89 3D 50 5F 57 01 E9 8E 24 C3 FF
'@
    $emptyCave = [byte[]]::new($caveReserveLength)
    $patchedCave = [byte[]]::new($caveReserveLength)
    Copy-Bytes $caveCode $patchedCave 0

    # Pin untouched stock bytes used by each branch. These checks also prove
    # that this standalone patch does not contain the rejected preload hook.
    $normalContinuation = Convert-HexBytes @'
8B 01 8B 90 80 00 00 00 BF 02 00 00 00
'@
    $missingContinuation = Convert-HexBytes @'
D9 05 3C 1D 96 00 8B 0D 68 61 57 01
'@
    $stockStateTwoSet = Convert-HexBytes 'BF 02 00 00 00'
    $stockRetryFlag = Convert-HexBytes 'C6 05 66 5C 57 01 01'
    $stockStateWrite = Convert-HexBytes '89 3D 50 5F 57 01'
    $preloadHookOffset = 0x0C14D6
    $preloadHook = Convert-HexBytes '68 A0 39 95 00'
    $preloadCaveOffset = 0x5C3366
    $preloadCave = [byte[]]::new(154)

    if ($caveCode.Length -ne 61) {
        throw 'Internal avatar timeout/retry cave length validation failed.'
    }
    Assert-AvatarTimeoutRetryRelativeBranch $patchedHook 0 $hookVa `
        $caveVa 0xE9
    Assert-AvatarTimeoutRetryRelativeBranch $caveCode 0x21 $caveVa `
        $normalContinuationVa 0xE9
    Assert-AvatarTimeoutRetryRelativeBranch $caveCode 0x38 $caveVa `
        $missingContinuationVa 0xE9
    Assert-AvatarTimeoutRetryShortBranches $caveCode `
        @(0x10, 0x19) 0x26

    # Lifecycle mismatch and both-ready state replay the exact displaced stock
    # instruction. The scoped missing path preserves the three native writes.
    if ($caveCode[0x07] -ne 0x75 -or
        0x07 + 2 + [int][sbyte]$caveCode[0x08] -ne 0x1B -or
        -not (Test-Bytes $caveCode 0x1B $originalHook) -or
        -not (Test-Bytes $caveCode 0x26 $stockStateTwoSet) -or
        -not (Test-Bytes $caveCode 0x2B $stockRetryFlag) -or
        -not (Test-Bytes $caveCode 0x32 $stockStateWrite)) {
        throw 'Internal avatar timeout/retry branch semantics are invalid.'
    }

    if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
        throw "Origin client executable was not found: $ClientExe"
    }
    $resolvedClientExe = (Resolve-Path -LiteralPath $ClientExe).Path
    if ($Mode -ne 'Status') {
        Assert-AvatarTimeoutRetryProcessClosed $resolvedClientExe
    }

    $data = [IO.File]::ReadAllBytes($resolvedClientExe)
    if ($data.Length -ne $expectedLength) {
        throw "Unsupported Origin.exe size $($data.Length); expected $expectedLength bytes."
    }
    $pe = Get-PeMetadata $data
    if ($pe.Machine -ne $expectedMachine -or
        $pe.OptionalMagic -ne $expectedOptionalMagic -or
        $pe.ImageBase -ne $expectedImageBase) {
        throw 'Origin.exe is not the audited x86 PE32 image-base build.'
    }

    $mappedRanges = @(
        @('timeout hook', $hookOffset, $originalHook.Length, $hookVa),
        @('timeout cave', $caveOffset, $caveReserveLength, $caveVa),
        @(
            'normal continuation',
            $normalContinuationOffset,
            $normalContinuation.Length,
            $normalContinuationVa
        ),
        @(
            'missing-root continuation',
            $missingContinuationOffset,
            $missingContinuation.Length,
            $missingContinuationVa
        )
    )
    foreach ($range in $mappedRanges) {
        $mapping = Resolve-ExecutableFileRange $pe $range[1] $range[2]
        if ($mapping.Va -ne $range[3]) {
            throw "Origin.exe $($range[0]) maps to VA 0x$(
                '{0:X8}' -f $mapping.Va), not the audited address."
        }
    }

    if (-not (Test-Bytes $data $normalContinuationOffset (
                $normalContinuation)) -or
        -not (Test-Bytes $data $missingContinuationOffset (
                $missingContinuation)) -or
        -not (Test-Bytes $data $preloadHookOffset $preloadHook) -or
        -not (Test-Bytes $data $preloadCaveOffset $preloadCave)) {
        throw 'Origin.exe timeout/retry prerequisites do not match the audited build.'
    }

    # Pin the stock copies of every side effect retained by the missing-root
    # branch. The skipped bytes are only the unsafe virtual-call sequences.
    if (-not (Test-Bytes $data 0x1F58C4 $stockStateTwoSet) -or
        -not (Test-Bytes $data 0x1F58CA $stockRetryFlag) -or
        -not (Test-Bytes $data 0x1F58D1 $stockStateWrite)) {
        throw 'Origin.exe state-2 timeout side effects do not match the audited build.'
    }

    $beforeHash = Get-AvatarTimeoutRetrySha256 $data
    $hasPriorState =
        $beforeHash -eq $priorSha256 -and
        (Test-Bytes $data $hookOffset $originalHook) -and
        (Test-Bytes $data $caveOffset $emptyCave)
    $hasPatchedState =
        $beforeHash -eq $patchedSha256 -and
        (Test-Bytes $data $hookOffset $patchedHook) -and
        (Test-Bytes $data $caveOffset $patchedCave)

    if (-not $hasPriorState -and -not $hasPatchedState) {
        throw "Unsupported Origin.exe SHA-256/state: $beforeHash"
    }

    $state = if ($hasPatchedState) {
        'TimeoutRetryGuardPatched'
    }
    else {
        'AuditedPredecessor'
    }
    if ($Mode -eq 'Status') {
        [pscustomobject]@{
            Mode = $Mode
            Status = if ($hasPatchedState) { 'Patched' } else { 'Ready to apply' }
            State = $state
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }
    if ($Mode -eq 'Apply' -and $hasPatchedState) {
        [pscustomobject]@{
            Mode = $Mode
            Status = 'Already patched'
            State = $state
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }
    if ($Mode -eq 'Revert' -and $hasPriorState) {
        [pscustomobject]@{
            Mode = $Mode
            Status = 'Already reverted'
            State = $state
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }
    if (($Mode -eq 'Apply' -and -not $hasPriorState) -or
        ($Mode -eq 'Revert' -and -not $hasPatchedState)) {
        throw "Origin.exe is not in the exact state required for $Mode."
    }

    $before = [byte[]]$data.Clone()
    $allowedRanges = @(
        [pscustomobject]@{
            Offset = $hookOffset
            Length = $originalHook.Length
        },
        [pscustomobject]@{
            Offset = $caveOffset
            Length = $caveReserveLength
        }
    )
    $expectedMutationCount =
        (Measure-ByteDifference $originalHook $patchedHook) +
        (Measure-ByteDifference $emptyCave $patchedCave)
    if ($expectedMutationCount -ne 64) {
        throw 'Internal avatar timeout/retry mutation-count validation failed.'
    }

    if ($Mode -eq 'Apply') {
        Copy-Bytes $patchedHook $data $hookOffset
        Copy-Bytes $patchedCave $data $caveOffset
        $expectedAfterHash = $patchedSha256
    }
    else {
        Copy-Bytes $originalHook $data $hookOffset
        Copy-Bytes $emptyCave $data $caveOffset
        $expectedAfterHash = $priorSha256
    }
    $mutationCount = Assert-AvatarTimeoutRetryAllowedMutation `
        $before $data $allowedRanges $expectedMutationCount `
        $expectedAfterHash 'Staged avatar timeout/retry candidate'

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        $BackupRoot = Join-Path $RepositoryRoot 'backups'
    }
    [IO.Directory]::CreateDirectory($BackupRoot) | Out-Null
    $backupDirectory = Join-Path $BackupRoot (
        'origin-avatar-timeout-retry-guard-' + $Mode + '-' +
        (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
        [guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    $backupPath = Join-Path $backupDirectory 'Origin.exe'
    Copy-Item -LiteralPath $resolvedClientExe -Destination $backupPath
    if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne
        $beforeHash) {
        throw "Avatar timeout/retry backup verification failed: $backupPath"
    }

    $operationId = [guid]::NewGuid().ToString('N')
    $stagePath = "$resolvedClientExe.$operationId.stage"
    $replaceBackup = "$resolvedClientExe.$operationId.replaced"
    $restoreStage = "$resolvedClientExe.$operationId.restore"
    $rollbackBackup = "$resolvedClientExe.$operationId.rollback"
    try {
        [IO.File]::WriteAllBytes($stagePath, $data)
        $staged = [IO.File]::ReadAllBytes($stagePath)
        [void](Assert-AvatarTimeoutRetryAllowedMutation `
            $before $staged $allowedRanges $expectedMutationCount `
            $expectedAfterHash 'Written staging file')

        Assert-AvatarTimeoutRetryProcessClosed $resolvedClientExe
        if ((Get-FileHash -LiteralPath $resolvedClientExe `
                -Algorithm SHA256).Hash -ne $beforeHash) {
            throw 'Origin.exe changed while the timeout/retry patch was staged.'
        }

        [IO.File]::Replace(
            $stagePath,
            $resolvedClientExe,
            $replaceBackup,
            $true)
        $written = [IO.File]::ReadAllBytes($resolvedClientExe)
        [void](Assert-AvatarTimeoutRetryAllowedMutation `
            $before $written $allowedRanges $expectedMutationCount `
            $expectedAfterHash 'Installed Origin.exe')
    }
    catch {
        $writeFailure = $_
        try {
            $currentHash = if (
                Test-Path -LiteralPath $resolvedClientExe -PathType Leaf
            ) {
                (Get-FileHash -LiteralPath $resolvedClientExe `
                    -Algorithm SHA256).Hash
            }
            else {
                $null
            }
            if ($currentHash -ne $beforeHash) {
                Copy-Item -LiteralPath $backupPath -Destination $restoreStage
                if ((Get-FileHash -LiteralPath $restoreStage `
                        -Algorithm SHA256).Hash -ne $beforeHash) {
                    throw 'Automatic-restore stage hash mismatch.'
                }
                Assert-AvatarTimeoutRetryProcessClosed $resolvedClientExe
                if (Test-Path -LiteralPath $resolvedClientExe -PathType Leaf) {
                    [IO.File]::Replace(
                        $restoreStage,
                        $resolvedClientExe,
                        $rollbackBackup,
                        $true)
                }
                else {
                    [IO.File]::Move($restoreStage, $resolvedClientExe)
                }
            }
            if ((Get-FileHash -LiteralPath $resolvedClientExe `
                    -Algorithm SHA256).Hash -ne $beforeHash) {
                throw 'Automatic restore did not reproduce the prior SHA-256.'
            }
        }
        catch {
            throw "Origin.exe write failed and automatic restore also failed. Backup: $backupPath"
        }
        throw $writeFailure
    }
    finally {
        foreach ($temporary in @(
            $stagePath,
            $replaceBackup,
            $restoreStage,
            $rollbackBackup
        )) {
            if (Test-Path -LiteralPath $temporary -PathType Leaf) {
                Remove-Item -LiteralPath $temporary -Force
            }
        }
    }

    [pscustomobject]@{
        Mode = $Mode
        Status = if ($Mode -eq 'Apply') { 'Patched' } else { 'Reverted' }
        State = if ($Mode -eq 'Apply') {
            'TimeoutRetryGuardPatched'
        }
        else {
            'AuditedPredecessor'
        }
        Path = $resolvedClientExe
        ChangedBytes = $mutationCount
        Backup = $backupPath
        BeforeSha256 = $beforeHash
        AfterSha256 = $expectedAfterHash
        HookFileOffset = ('0x{0:X}' -f $hookOffset)
        HookVa = ('0x{0:X8}' -f $hookVa)
        GuardedRoots = 2
    }
}
