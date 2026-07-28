function Get-PetLevelSavvyRefreshSha256 {
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

function Assert-PetLevelSavvyRefreshProcessClosed {
    param([string]$ResolvedClientExe)

    $processName =
        [IO.Path]::GetFileNameWithoutExtension($ResolvedClientExe)
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
                    $true
                }
            }
    )
    if ($running.Count -gt 0) {
        throw "$([IO.Path]::GetFileName($ResolvedClientExe)) is running. Close it before changing the executable."
    }
}

function Assert-PetLevelSavvyRefreshRelativeBranch {
    param(
        [byte[]]$Code,
        [int]$InstructionOffset,
        [uint64]$CodeVa,
        [uint64]$ExpectedTarget
    )

    if ($InstructionOffset -lt 0 -or
        $InstructionOffset + 5 -gt $Code.Length -or
        $Code[$InstructionOffset] -ne 0xE9) {
        throw 'Internal pet-level savvy-refresh branch encoding is invalid.'
    }

    $target = $CodeVa + $InstructionOffset + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
    if ($target -ne $ExpectedTarget) {
        throw "Internal pet-level savvy-refresh branch targets 0x$(
            '{0:X8}' -f $target), expected 0x$(
            '{0:X8}' -f $ExpectedTarget)."
    }
}

function Assert-PetLevelSavvyRefreshMutation {
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
    if ((Get-PetLevelSavvyRefreshSha256 $After) -ne $ExpectedHash) {
        throw "$Label failed exact SHA-256 verification."
    }

    return $mutationCount
}

function Invoke-PetLevelSavvyRefreshPatch {
    param(
        [string]$ClientExe,
        [string]$Mode,
        [string]$BackupRoot,
        [string]$RepositoryRoot
    )

    $baseSha256 =
        '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
    $timeoutSha256 =
        'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C'
    $basePatchedSha256 =
        '7FB43C8D6BBA42CE533EE4CB78075CA88D3D6C11F2F79224C56A8A4F50BA07F9'
    $timeoutPatchedSha256 =
        '2BD6B3DD6FA9F608D0580264F1E548309F2C4F469E8CB69190CFE19083C8E0F7'

    $expectedLength = 6676480
    $expectedMachine = 0x014C
    $expectedOptionalMagic = 0x010B
    $expectedImageBase = 0x00400000

    # S2C 10286 is registered to handler 0x006A18F0. Its native 20-byte
    # prefix updates the pet's level, remaining EXP, and next-level cost.
    # Redirect the next complete instruction to audited executable padding.
    # Exact 44-byte packets append six uint32 value*100 basic-savvy totals in
    # native stat order. Legacy 20-byte packets restore flags and immediately
    # replay the displaced instruction without touching savvy.
    $hookOffset = 0x2A195C
    $hookVa = 0x006A195C
    $continuationOffset = 0x2A1967
    $continuationVa = 0x006A1967
    $caveOffset = 0x5C3480
    $caveVa = 0x009C3480
    $caveReserveLength = 64

    $originalHook = Convert-HexBytes @'
C7 84 24 E4 00 00 00 07 00 00 00
'@
    $patchedHook = Convert-HexBytes @'
E9 1F 1B 32 00 90 90 90 90 90 90
'@
    $caveCode = Convert-HexBytes @'
9C
66 83 3E 2C
75 16
51 56 57
83 C6 14
81 C7 84 00 00 00
B9 06 00 00 00
F3 A5
5F 5E 59
9D
C7 84 24 E4 00 00 00 07 00 00 00
E9 B9 E4 CD FF
'@
    $emptyCave = [byte[]]::new($caveReserveLength)
    $patchedCave = [byte[]]::new($caveReserveLength)
    Copy-Bytes $caveCode $patchedCave 0

    $handlerRegistration = Convert-HexBytes @'
C7 44 24 1C 2E 28 00 00 C7 44 24 20 F0 18 6A 00
'@
    $nativePrefixWrites = Convert-HexBytes @'
8A 46 08 88 47 40
8B 4E 0C 89 4F 78
8B 56 10 89 57 7C
'@
    $continuation = Convert-HexBytes @'
89 9C 24 E0 00 00 00 66 89 9C 24 D0 00 00 00
'@
    $snapshotSavvyDestination = Convert-HexBytes @'
8D 96 84 00 00 00
'@

    if ($caveCode.Length -ne 46 -or
        $patchedHook.Length -ne $originalHook.Length) {
        throw 'Internal pet-level savvy-refresh code length validation failed.'
    }
    Assert-PetLevelSavvyRefreshRelativeBranch `
        $patchedHook 0 $hookVa $caveVa
    Assert-PetLevelSavvyRefreshRelativeBranch `
        $caveCode 41 $caveVa $continuationVa

    # Pin the exact-length gate, its legacy restore target, the six-dword copy,
    # register/flag restoration, and the displaced instruction replay.
    if (-not (Test-Bytes $caveCode 0 (
                Convert-HexBytes '9C 66 83 3E 2C 75 16')) -or
        5 + 2 + [int][sbyte]$caveCode[6] -ne 29 -or
        -not (Test-Bytes $caveCode 7 (
                Convert-HexBytes '51 56 57')) -or
        -not (Test-Bytes $caveCode 10 (
                Convert-HexBytes @'
83 C6 14 81 C7 84 00 00 00 B9 06 00 00 00 F3 A5
'@)) -or
        -not (Test-Bytes $caveCode 26 (
                Convert-HexBytes '5F 5E 59 9D')) -or
        -not (Test-Bytes $caveCode 30 $originalHook)) {
        throw 'Internal pet-level savvy-refresh semantics are invalid.'
    }

    if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
        throw "Origin client executable was not found: $ClientExe"
    }
    $resolvedClientExe = (Resolve-Path -LiteralPath $ClientExe).Path
    if ($Mode -ne 'Status') {
        Assert-PetLevelSavvyRefreshProcessClosed $resolvedClientExe
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

    foreach ($range in @(
        @('pet-level hook', $hookOffset, $originalHook.Length, $hookVa),
        @(
            'pet-level continuation',
            $continuationOffset,
            $continuation.Length,
            $continuationVa
        ),
        @('pet-level cave', $caveOffset, $caveReserveLength, $caveVa)
    )) {
        $mapping = Resolve-ExecutableFileRange $pe $range[1] $range[2]
        if ($mapping.Va -ne $range[3]) {
            throw "Origin.exe $($range[0]) maps to VA 0x$(
                '{0:X8}' -f $mapping.Va), not the audited address."
        }
    }

    if (-not (Test-Bytes $data 0x29BF99 $handlerRegistration) -or
        -not (Test-Bytes $data 0x2A194A $nativePrefixWrites) -or
        -not (Test-Bytes $data $continuationOffset $continuation) -or
        -not (Test-Bytes $data 0x2A6447 $snapshotSavvyDestination)) {
        throw 'Origin.exe pet-level handler prerequisites do not match the audited build.'
    }

    $beforeHash = Get-PetLevelSavvyRefreshSha256 $data
    $states = @{
        $baseSha256 = [pscustomobject]@{
            Name = 'AuditedBase'
            Patched = $false
            PeerHash = $basePatchedSha256
            PeerName = 'PetLevelSavvyRefreshPatched'
        }
        $timeoutSha256 = [pscustomobject]@{
            Name = 'TimeoutRetryGuardPatched'
            Patched = $false
            PeerHash = $timeoutPatchedSha256
            PeerName = 'TimeoutAndPetLevelSavvyRefreshPatched'
        }
        $basePatchedSha256 = [pscustomobject]@{
            Name = 'PetLevelSavvyRefreshPatched'
            Patched = $true
            PeerHash = $baseSha256
            PeerName = 'AuditedBase'
        }
        $timeoutPatchedSha256 = [pscustomobject]@{
            Name = 'TimeoutAndPetLevelSavvyRefreshPatched'
            Patched = $true
            PeerHash = $timeoutSha256
            PeerName = 'TimeoutRetryGuardPatched'
        }
    }
    if (-not $states.ContainsKey($beforeHash)) {
        throw "Unsupported Origin.exe SHA-256/state: $beforeHash"
    }
    $state = $states[$beforeHash]
    $hasOriginalState =
        -not $state.Patched -and
        (Test-Bytes $data $hookOffset $originalHook) -and
        (Test-Bytes $data $caveOffset $emptyCave)
    $hasPatchedState =
        $state.Patched -and
        (Test-Bytes $data $hookOffset $patchedHook) -and
        (Test-Bytes $data $caveOffset $patchedCave)
    if (-not $hasOriginalState -and -not $hasPatchedState) {
        throw "Unsupported Origin.exe SHA-256/state: $beforeHash"
    }

    if ($Mode -eq 'Status') {
        [pscustomobject]@{
            Mode = $Mode
            Status = if ($state.Patched) {
                'Patched'
            }
            else {
                'Ready to apply'
            }
            State = $state.Name
            Path = $resolvedClientExe
            Sha256 = $beforeHash
            LegacyPacketLength = 20
            ExtendedPacketLength = 44
        }
        return
    }
    if ($Mode -eq 'Apply' -and $state.Patched) {
        [pscustomobject]@{
            Mode = $Mode
            Status = 'Already patched'
            State = $state.Name
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }
    if ($Mode -eq 'Revert' -and -not $state.Patched) {
        [pscustomobject]@{
            Mode = $Mode
            Status = 'Already reverted'
            State = $state.Name
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
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
    if ($expectedMutationCount -ne 44) {
        throw 'Internal pet-level savvy-refresh mutation-count validation failed.'
    }

    if ($Mode -eq 'Apply') {
        Copy-Bytes $patchedHook $data $hookOffset
        Copy-Bytes $patchedCave $data $caveOffset
    }
    else {
        Copy-Bytes $originalHook $data $hookOffset
        Copy-Bytes $emptyCave $data $caveOffset
    }
    $expectedAfterHash = $state.PeerHash
    $mutationCount = Assert-PetLevelSavvyRefreshMutation `
        $before $data $allowedRanges $expectedMutationCount `
        $expectedAfterHash 'Staged pet-level savvy-refresh candidate'

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        $BackupRoot = Join-Path $RepositoryRoot 'backups'
    }
    [IO.Directory]::CreateDirectory($BackupRoot) | Out-Null
    $backupDirectory = Join-Path $BackupRoot (
        'origin-pet-level-savvy-refresh-' + $Mode + '-' +
        (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
        [guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    $backupPath = Join-Path $backupDirectory 'Origin.exe'
    Copy-Item -LiteralPath $resolvedClientExe -Destination $backupPath
    if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne
        $beforeHash) {
        throw "Pet-level savvy-refresh backup verification failed: $backupPath"
    }

    $operationId = [guid]::NewGuid().ToString('N')
    $stagePath = "$resolvedClientExe.$operationId.stage"
    $replaceBackup = "$resolvedClientExe.$operationId.replaced"
    $restoreStage = "$resolvedClientExe.$operationId.restore"
    $rollbackBackup = "$resolvedClientExe.$operationId.rollback"
    try {
        [IO.File]::WriteAllBytes($stagePath, $data)
        $staged = [IO.File]::ReadAllBytes($stagePath)
        [void](Assert-PetLevelSavvyRefreshMutation `
            $before $staged $allowedRanges $expectedMutationCount `
            $expectedAfterHash 'Written staging file')

        Assert-PetLevelSavvyRefreshProcessClosed $resolvedClientExe
        if ((Get-FileHash -LiteralPath $resolvedClientExe `
                -Algorithm SHA256).Hash -ne $beforeHash) {
            throw 'Origin.exe changed while the pet-level patch was staged.'
        }

        [IO.File]::Replace(
            $stagePath,
            $resolvedClientExe,
            $replaceBackup,
            $true)
        $written = [IO.File]::ReadAllBytes($resolvedClientExe)
        [void](Assert-PetLevelSavvyRefreshMutation `
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
                Copy-Item -LiteralPath $backupPath `
                    -Destination $restoreStage
                if ((Get-FileHash -LiteralPath $restoreStage `
                        -Algorithm SHA256).Hash -ne $beforeHash) {
                    throw 'Automatic-restore stage hash mismatch.'
                }
                Assert-PetLevelSavvyRefreshProcessClosed $resolvedClientExe
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
        State = $state.PeerName
        Path = $resolvedClientExe
        ChangedBytes = $mutationCount
        Backup = $backupPath
        BeforeSha256 = $beforeHash
        AfterSha256 = $expectedAfterHash
        HookFileOffset = ('0x{0:X}' -f $hookOffset)
        HookVa = ('0x{0:X8}' -f $hookVa)
        CaveFileOffset = ('0x{0:X}' -f $caveOffset)
        CaveVa = ('0x{0:X8}' -f $caveVa)
        LegacyPacketLength = 20
        ExtendedPacketLength = 44
        SavvyFieldCount = 6
    }
}
