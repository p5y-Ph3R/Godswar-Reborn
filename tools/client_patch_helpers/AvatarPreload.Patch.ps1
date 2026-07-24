function Get-ByteArraySha256 {
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

function Assert-RelativeBranch {
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
        throw 'Internal avatar-preload branch encoding is invalid.'
    }
    $target = $CodeVa + $InstructionOffset + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
    if ($target -ne $ExpectedTarget) {
        throw "Internal avatar-preload branch targets 0x$(
            '{0:X8}' -f $target), expected 0x$(
            '{0:X8}' -f $ExpectedTarget)."
    }
}

function Assert-ShortBranches {
    param(
        [byte[]]$Code,
        [int[]]$InstructionOffsets,
        [int]$ExpectedTargetOffset
    )

    foreach ($offset in $InstructionOffsets) {
        if ($offset -lt 0 -or $offset + 2 -gt $Code.Length -or
            $Code[$offset] -notin @(0x74, 0x75)) {
            throw 'Internal avatar-preload short branch encoding is invalid.'
        }
        $target = $offset + 2 + [int][sbyte]$Code[$offset + 1]
        if ($target -ne $ExpectedTargetOffset) {
            throw "Internal avatar-preload short branch targets 0x$(
                '{0:X}' -f $target), expected 0x$(
                '{0:X}' -f $ExpectedTargetOffset)."
        }
    }
}

function Invoke-AvatarPreloadPatch {
    param(
        [string]$ClientExe,
        [string]$Mode,
        [string]$BackupRoot,
        [string]$RepositoryRoot
    )

    $priorSha256 =
        '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
    $patchedSha256 =
        'E0F5BC951C6E37550F4D9CC1E25BFDCB4F020466ADD854DC2E7EA04E0D22F81C'
    $stockNetSha256 =
        '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
    $expectedLength = 6676480
    $expectedMachine = 0x014C
    $expectedOptionalMagic = 0x010B
    $expectedImageBase = 0x00400000

    # Run the native LOGIN initializer synchronously after state 2 registers
    # its selection object. The existing stock call remains a fallback unless
    # all six avatar roots were built successfully.
    $preloadHookOffset = 0x0C14D6
    $preloadHookVa = 0x004C14D6
    $preloadCaveOffset = 0x5C3366
    $preloadCaveVa = 0x009C3366
    $preloadCaveReserveLength = 154
    $preloadContinuationOffset = 0x0C14DB
    $preloadContinuationVa = 0x004C14DB
    $nativeInitializerVa = 0x00467280
    $originalPreloadHook = Convert-HexBytes '68 A0 39 95 00'
    $patchedPreloadHook = Convert-HexBytes 'E9 8B 1E 50 00'
    $preloadCaveCode = Convert-HexBytes @'
9C 60 83 3D 4C 5F 57 01 02 75 5C
81 3D 6C 5F 57 01 04 5A 9E 00 75 50
83 3D 48 5F 57 01 00 74 47
B9 04 5A 9E 00 E8 F0 3E AA FF
A1 88 60 57 01 85 C0 74 34
A1 8C 60 57 01 85 C0 74 2B
A1 90 60 57 01 85 C0 74 22
A1 9C 60 57 01 85 C0 74 19
A1 A0 60 57 01 85 C0 74 10
A1 A4 60 57 01 85 C0 74 07
C6 05 70 5F 57 01 01
61 9D 68 A0 39 95 00 E9 02 E1 AF FF
'@
    $emptyPreloadCave = [byte[]]::new($preloadCaveReserveLength)
    $patchedPreloadCave = [byte[]]::new($preloadCaveReserveLength)
    Copy-Bytes $preloadCaveCode $patchedPreloadCave 0

    # If a stock timeout/reset path runs before resources exist, skip its two
    # unsafe avatar virtual calls, request state 2 again, and continue cleanup.
    $timeoutHookOffset = 0x1F58B6
    $timeoutHookVa = 0x005F58B6
    $timeoutCaveOffset = 0x5C341F
    $timeoutCaveVa = 0x009C341F
    $timeoutCaveReserveLength = 96
    $timeoutNormalContinuationOffset = 0x1F58BC
    $timeoutNormalContinuationVa = 0x005F58BC
    $timeoutMissingContinuationOffset = 0x1F58EA
    $timeoutMissingContinuationVa = 0x005F58EA
    $originalTimeoutHook = Convert-HexBytes '8B 0D A0 60 57 01'
    $patchedTimeoutHook = Convert-HexBytes 'E9 64 DB 3C 00 90'
    $timeoutCaveCode = Convert-HexBytes @'
A1 88 60 57 01 85 C0 74 38
A1 8C 60 57 01 85 C0 74 2F
A1 90 60 57 01 85 C0 74 26
A1 9C 60 57 01 85 C0 74 1D
A1 A0 60 57 01 85 C0 74 14
A1 A4 60 57 01 85 C0 74 0B
8B 0D A0 60 57 01 E9 5C 24 C3 FF
BF 02 00 00 00 C6 05 66 5C 57 01 01
89 3D 50 5F 57 01 E9 73 24 C3 FF
'@
    $emptyTimeoutCave = [byte[]]::new($timeoutCaveReserveLength)
    $patchedTimeoutCave = [byte[]]::new($timeoutCaveReserveLength)
    Copy-Bytes $timeoutCaveCode $patchedTimeoutCave 0

    $preloadContinuation = Convert-HexBytes @'
E9 AF 01 00 00 8B 0D 54 61 57 01 8B 01 8B 50 1C
'@
    $timeoutNormalContinuation = Convert-HexBytes @'
8B 01 8B 90 80 00 00 00 BF 02 00 00 00
'@
    $timeoutMissingContinuation = Convert-HexBytes @'
D9 05 3C 1D 96 00 8B 0D 68 61 57 01
'@

    # Pin the already-installed V3 avatar guards. V4 is additive and must not
    # silently replace or weaken those known working protections.
    $v3Prerequisites = @(
        [pscustomobject]@{
            Offset = 0x0C14C5
            Bytes = Convert-HexBytes 'E9 36 1E 50 00'
        },
        [pscustomobject]@{
            Offset = 0x5C3300
            Bytes = Convert-HexBytes @'
C6 05 70 5F 57 01 00 68 04 5A 9E 00 E9 B9 E1 AF FF
'@
        },
        [pscustomobject]@{
            Offset = 0x1F4A82
            Bytes = Convert-HexBytes 'E9 29 E8 3C 00 90'
        },
        [pscustomobject]@{
            Offset = 0x5C32B0
            Bytes = Convert-HexBytes @'
A1 88 60 57 01 85 C0 74 36 A1 8C 60 57 01 85 C0 74 2D
A1 90 60 57 01 85 C0 74 24 A1 9C 60 57 01 85 C0 74 1B
A1 A0 60 57 01 85 C0 74 12 A1 A4 60 57 01 85 C0 74 09
33 FF 33 C9 E9 A1 17 C3 FF E9 79 1E C3 FF
'@
        },
        [pscustomobject]@{
            Offset = 0x1F05C2
            Bytes = Convert-HexBytes 'E9 59 2D 3D 00 90'
        },
        [pscustomobject]@{
            Offset = 0x5C3320
            Bytes = Convert-HexBytes @'
A1 88 60 57 01 85 C0 74 38 A1 8C 60 57 01 85 C0 74 2F
A1 90 60 57 01 85 C0 74 26 A1 9C 60 57 01 85 C0 74 1D
A1 A0 60 57 01 85 C0 74 14 A1 A4 60 57 01 85 C0 74 0B
8B 44 24 50 33 DB E9 67 D2 C2 FF E9 5D DA C2 FF
'@
        }
    )

    if ($preloadCaveCode.Length -ne 115 -or
        $timeoutCaveCode.Length -ne 88) {
        throw 'Internal avatar-preload cave length validation failed.'
    }
    Assert-RelativeBranch $patchedPreloadHook 0 $preloadHookVa `
        $preloadCaveVa 0xE9
    Assert-RelativeBranch $preloadCaveCode 0x25 $preloadCaveVa `
        $nativeInitializerVa 0xE8
    Assert-RelativeBranch $preloadCaveCode 0x6E $preloadCaveVa `
        $preloadContinuationVa 0xE9
    Assert-ShortBranches $preloadCaveCode `
        @(0x09, 0x15, 0x1E, 0x31, 0x3A, 0x43, 0x4C, 0x55, 0x5E) 0x67
    Assert-RelativeBranch $patchedTimeoutHook 0 $timeoutHookVa `
        $timeoutCaveVa 0xE9
    Assert-RelativeBranch $timeoutCaveCode 0x3C $timeoutCaveVa `
        $timeoutNormalContinuationVa 0xE9
    Assert-RelativeBranch $timeoutCaveCode 0x53 $timeoutCaveVa `
        $timeoutMissingContinuationVa 0xE9
    Assert-ShortBranches $timeoutCaveCode `
        @(0x07, 0x10, 0x19, 0x22, 0x2B, 0x34) 0x41

    if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
        throw "Origin client executable was not found: $ClientExe"
    }
    $resolvedClientExe = (Resolve-Path -LiteralPath $ClientExe).Path
    if ($Mode -ne 'Status') {
        Assert-AvatarPreloadProcessClosed $resolvedClientExe
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
        @('preload hook', $preloadHookOffset, 5, $preloadHookVa),
        @('preload cave', $preloadCaveOffset, $preloadCaveReserveLength,
            $preloadCaveVa),
        @('preload continuation', $preloadContinuationOffset,
            $preloadContinuation.Length, $preloadContinuationVa),
        @('timeout hook', $timeoutHookOffset, 6, $timeoutHookVa),
        @('timeout cave', $timeoutCaveOffset, $timeoutCaveReserveLength,
            $timeoutCaveVa),
        @('timeout normal continuation', $timeoutNormalContinuationOffset,
            $timeoutNormalContinuation.Length, $timeoutNormalContinuationVa),
        @('timeout missing continuation', $timeoutMissingContinuationOffset,
            $timeoutMissingContinuation.Length, $timeoutMissingContinuationVa)
    )
    foreach ($range in $mappedRanges) {
        $mapping = Resolve-ExecutableFileRange $pe $range[1] $range[2]
        if ($mapping.Va -ne $range[3]) {
            throw "Origin.exe $($range[0]) maps to VA 0x$(
                '{0:X8}' -f $mapping.Va), not the audited address."
        }
    }

    foreach ($prerequisite in $v3Prerequisites) {
        if (-not (Test-Bytes $data $prerequisite.Offset $prerequisite.Bytes)) {
            throw 'Origin.exe does not contain the audited V3 avatar guards.'
        }
    }
    $validPreloadContinuation = Test-Bytes $data `
        $preloadContinuationOffset $preloadContinuation
    $validTimeoutNormal = Test-Bytes $data `
        $timeoutNormalContinuationOffset $timeoutNormalContinuation
    $validTimeoutMissing = Test-Bytes $data `
        $timeoutMissingContinuationOffset $timeoutMissingContinuation
    if (-not $validPreloadContinuation -or
        -not $validTimeoutNormal -or
        -not $validTimeoutMissing) {
        throw 'Origin.exe preload/reset continuations do not match the audited build.'
    }

    $beforeHash = Get-ByteArraySha256 $data
    $hasPriorState =
        $beforeHash -eq $priorSha256 -and
        (Test-Bytes $data $preloadHookOffset $originalPreloadHook) -and
        (Test-Bytes $data $preloadCaveOffset $emptyPreloadCave) -and
        (Test-Bytes $data $timeoutHookOffset $originalTimeoutHook) -and
        (Test-Bytes $data $timeoutCaveOffset $emptyTimeoutCave)
    $hasPatchedState =
        $beforeHash -eq $patchedSha256 -and
        (Test-Bytes $data $preloadHookOffset $patchedPreloadHook) -and
        (Test-Bytes $data $preloadCaveOffset $patchedPreloadCave) -and
        (Test-Bytes $data $timeoutHookOffset $patchedTimeoutHook) -and
        (Test-Bytes $data $timeoutCaveOffset $patchedTimeoutCave)

    if (-not $hasPriorState -and -not $hasPatchedState) {
        throw "Unsupported Origin.exe SHA-256/state: $beforeHash"
    }
    if ($Mode -eq 'Status') {
        [pscustomobject]@{
            Mode = $Mode
            Status = if ($hasPatchedState) { 'Patched' } else { 'Ready to apply' }
            State = if ($hasPatchedState) { 'V4Patched' } else { 'V3PriorPatch' }
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }
    if ($Mode -eq 'Apply' -and $hasPatchedState) {
        [pscustomobject]@{
            Mode = $Mode
            Status = 'Already patched'
            State = 'V4Patched'
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }
    if ($Mode -eq 'Revert' -and $hasPriorState) {
        [pscustomobject]@{
            Mode = $Mode
            Status = 'Already reverted'
            State = 'V3PriorPatch'
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }
    if (($Mode -eq 'Apply' -and -not $hasPriorState) -or
        ($Mode -eq 'Revert' -and -not $hasPatchedState)) {
        throw "Origin.exe is not in the exact state required for $Mode."
    }

    # The Net shim pins the matching Origin hash. Require a stock network DLL
    # before crossing either side of that pairing boundary.
    Assert-AvatarPreloadNetworkStock $resolvedClientExe $stockNetSha256

    $before = [byte[]]$data.Clone()
    $allowedRanges = @(
        [pscustomobject]@{ Offset = $preloadHookOffset; Length = 5 },
        [pscustomobject]@{
            Offset = $preloadCaveOffset
            Length = $preloadCaveCode.Length
        },
        [pscustomobject]@{ Offset = $timeoutHookOffset; Length = 6 },
        [pscustomobject]@{
            Offset = $timeoutCaveOffset
            Length = $timeoutCaveReserveLength
        }
    )
    if ($Mode -eq 'Apply') {
        Copy-Bytes $patchedPreloadHook $data $preloadHookOffset
        Copy-Bytes $preloadCaveCode $data $preloadCaveOffset
        Copy-Bytes $patchedTimeoutHook $data $timeoutHookOffset
        Copy-Bytes $patchedTimeoutCave $data $timeoutCaveOffset
        $expectedAfterHash = $patchedSha256
    }
    else {
        Copy-Bytes $originalPreloadHook $data $preloadHookOffset
        Copy-Bytes ([byte[]]::new($preloadCaveCode.Length)) `
            $data $preloadCaveOffset
        Copy-Bytes $originalTimeoutHook $data $timeoutHookOffset
        Copy-Bytes $emptyTimeoutCave $data $timeoutCaveOffset
        $expectedAfterHash = $priorSha256
    }

    $expectedMutationCount =
        (Measure-ByteDifference $originalPreloadHook $patchedPreloadHook) +
        (Measure-ByteDifference `
            ([byte[]]::new($preloadCaveCode.Length)) $preloadCaveCode) +
        (Measure-ByteDifference $originalTimeoutHook $patchedTimeoutHook) +
        (Measure-ByteDifference $emptyTimeoutCave $patchedTimeoutCave)
    if ($expectedMutationCount -ne 206) {
        throw 'Internal avatar-preload mutation-count validation failed.'
    }
    $mutationCount = 0
    for ($offset = 0; $offset -lt $data.Length; $offset++) {
        if ($before[$offset] -eq $data[$offset]) { continue }
        $mutationCount++
        if (-not (Test-AllowedDifference $offset $allowedRanges)) {
            throw "Unexpected avatar-preload mutation at file offset 0x$(
                '{0:X}' -f $offset)."
        }
    }
    if ($mutationCount -ne $expectedMutationCount -or
        (Get-ByteArraySha256 $data) -ne $expectedAfterHash) {
        throw 'Avatar-preload candidate hash/mutation validation failed.'
    }

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        $BackupRoot = Join-Path $RepositoryRoot 'backups'
    }
    [IO.Directory]::CreateDirectory($BackupRoot) | Out-Null
    $backupDirectory = Join-Path $BackupRoot (
        'origin-avatar-preload-v4-' + $Mode + '-' +
        (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
        [guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    $backupPath = Join-Path $backupDirectory 'Origin.exe'
    Copy-Item -LiteralPath $resolvedClientExe -Destination $backupPath
    if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne
        $beforeHash) {
        throw "Avatar-preload backup verification failed: $backupPath"
    }

    $operationId = [guid]::NewGuid().ToString('N')
    $stagePath = "$resolvedClientExe.$operationId.stage"
    $replaceBackup = "$resolvedClientExe.$operationId.replaced"
    $restoreStage = "$resolvedClientExe.$operationId.restore"
    $rollbackBackup = "$resolvedClientExe.$operationId.rollback"
    try {
        [IO.File]::WriteAllBytes($stagePath, $data)
        if ((Get-FileHash -LiteralPath $stagePath -Algorithm SHA256).Hash -ne
            $expectedAfterHash) {
            throw 'Staged Origin.exe failed exact SHA-256 verification.'
        }
        Assert-AvatarPreloadProcessClosed $resolvedClientExe
        [IO.File]::Replace(
            $stagePath,
            $resolvedClientExe,
            $replaceBackup,
            $true)
        $written = [IO.File]::ReadAllBytes($resolvedClientExe)
        if ($written.Length -ne $expectedLength -or
            (Get-ByteArraySha256 $written) -ne $expectedAfterHash) {
            throw 'Written Origin.exe failed exact SHA-256 verification.'
        }
        $writtenMutationCount = 0
        for ($offset = 0; $offset -lt $written.Length; $offset++) {
            if ($before[$offset] -eq $written[$offset]) { continue }
            $writtenMutationCount++
            if (-not (Test-AllowedDifference $offset $allowedRanges)) {
                throw "Unexpected written mutation at file offset 0x$(
                    '{0:X}' -f $offset)."
            }
        }
        if ($writtenMutationCount -ne $expectedMutationCount) {
            throw "Written Origin.exe changed $writtenMutationCount bytes; expected $expectedMutationCount."
        }
    }
    catch {
        $writeFailure = $_
        try {
            $currentHash = if (
                Test-Path -LiteralPath $resolvedClientExe -PathType Leaf
            ) {
                (Get-FileHash -LiteralPath $resolvedClientExe `
                    -Algorithm SHA256).Hash
            } else {
                $null
            }
            if ($currentHash -ne $beforeHash) {
                Copy-Item -LiteralPath $backupPath -Destination $restoreStage
                if ((Get-FileHash -LiteralPath $restoreStage `
                        -Algorithm SHA256).Hash -ne $beforeHash) {
                    throw 'Automatic-restore stage hash mismatch.'
                }
                Assert-AvatarPreloadProcessClosed $resolvedClientExe
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
        State = if ($Mode -eq 'Apply') { 'V4Patched' } else { 'V3PriorPatch' }
        Path = $resolvedClientExe
        ChangedBytes = $mutationCount
        Backup = $backupPath
        BeforeSha256 = $beforeHash
        AfterSha256 = $expectedAfterHash
        PreloadHookVa = ('0x{0:X8}' -f $preloadHookVa)
        TimeoutGuardHookVa = ('0x{0:X8}' -f $timeoutHookVa)
    }
}
