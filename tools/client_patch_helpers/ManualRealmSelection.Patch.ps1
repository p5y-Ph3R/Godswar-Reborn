function Get-ManualRealmSelectionSha256 {
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

function Assert-ManualRealmSelectionProcessClosed {
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
                    $true
                }
            }
    )
    if ($running.Count -gt 0) {
        throw "$([IO.Path]::GetFileName($ResolvedClientExe)) is running. Close it before changing the executable."
    }
}

function Assert-ManualRealmSelectionRelativeCall {
    param(
        [byte[]]$Code,
        [int]$InstructionOffset,
        [uint64]$CodeVa,
        [uint64]$ExpectedTarget,
        [string]$Label
    )

    if ($InstructionOffset -lt 0 -or
        $InstructionOffset + 5 -gt $Code.Length -or
        $Code[$InstructionOffset] -ne 0xE8) {
        throw "Internal $Label call encoding is invalid."
    }
    $target = $CodeVa + $InstructionOffset + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
    if ($target -ne $ExpectedTarget) {
        throw "Internal $Label call targets 0x$('{0:X8}' -f $target), expected 0x$('{0:X8}' -f $ExpectedTarget)."
    }
}

function Assert-ManualRealmSelectionMutation {
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
            throw "$Label contains an unexpected mutation at file offset 0x$('{0:X}' -f $offset)."
        }
    }
    if ($mutationCount -ne $ExpectedMutationCount) {
        throw "$Label changed $mutationCount bytes; expected $ExpectedMutationCount."
    }
    if ((Get-ManualRealmSelectionSha256 $After) -ne $ExpectedHash) {
        throw "$Label failed exact SHA-256 verification."
    }
    return $mutationCount
}

function Invoke-ManualRealmSelectionPatch {
    param(
        [string]$ClientExe,
        [string]$Mode,
        [string]$BackupRoot,
        [string]$RepositoryRoot
    )

    $expectedLength = 6676480

    # The terminal GameServerInfo handler resolves a saved or recommended
    # realm, then invokes the same sender used by the Enter Game button. Only
    # this automatic call is suppressed. The manual UI call remains native.
    $hookOffset = 0x1F9A19
    $hookVa = 0x005F9A19
    $selectionSenderVa = 0x005FBDB0
    $manualCallOffset = 0x1F699A
    $manualCallVa = 0x005F699A

    # The independent character-Back guard can be installed before or after
    # this patch. Pin its complete hook and owned cave so changing the manual
    # selection state cannot silently disturb either guard state.
    $guardHookOffset = 0x1F58B6
    $guardHookVa = 0x005F58B6
    $guardCaveOffset = 0x53E3E0
    $guardCaveVa = 0x0093E3E0
    $guardCaveLength = 112

    $originalHook = Convert-HexBytes 'E8 92 23 00 00'
    $patchedHook = Convert-HexBytes '90 90 90 90 90'
    $originalGuardHook = Convert-HexBytes '8B 0D A0 60 57 01'
    $patchedGuardHook = Convert-HexBytes 'E9 25 8B 34 00 90'
    $guardCaveCode = Convert-HexBytes @'
83 3D 4C 5F 57 01 02 75 12
A1 A0 60 57 01 85 C0 74 14
A1 8C 60 57 01 85 C0 74 0B
8B 0D A0 60 57 01 E9 B6 74 CB FF
BF 02 00 00 00 C6 05 66 5C 57 01 01
89 3D 50 5F 57 01 E9 CD 74 CB FF
'@
    $emptyGuardCave = [byte[]]::new($guardCaveLength)
    $patchedGuardCave = [byte[]]::new($guardCaveLength)
    Copy-Bytes $guardCaveCode $patchedGuardCave 0
    $manualPath = Convert-HexBytes @'
8B CF E8 11 54 00 00 E9 18 01 00 00
'@
    $serverListDispatch = Convert-HexBytes 'E8 F7 EF 10 00'
    $roleReturnGate = Convert-HexBytes @'
80 B8 58 02 00 00 00 0F 85 04 01 00 00
'@
    $lastSelectionPath = Convert-HexBytes @'
6A 00 57 E8 AE 2F 00 00
'@
    $autoCallPrefix = Convert-HexBytes @'
6A FF 6A 00 50 B9 CC 59 9E 00 E8 29 7D E0 FF 8B CF
'@
    $autoCallContinuation = Convert-HexBytes '5E 5B 59 C3'
    $selectServerPacket = Convert-HexBytes @'
66 C7 44 24 30 2C 00 66 C7 44 24 32 04 00
'@
    $loginReturnPacket = Convert-HexBytes @'
66 C7 44 24 54 5C 00 66 C7 44 24 56 06 00
'@

    if ($originalHook.Length -ne 5 -or
        $patchedHook.Length -ne $originalHook.Length -or
        (Measure-ByteDifference $originalHook $patchedHook) -ne 5 -or
        $guardCaveCode.Length -ne 61 -or
        $patchedGuardCave.Length -ne $guardCaveLength) {
        throw 'Internal manual realm-selection patch length validation failed.'
    }
    Assert-ManualRealmSelectionRelativeCall `
        $originalHook 0 $hookVa $selectionSenderVa 'automatic selection'
    Assert-ManualRealmSelectionRelativeCall `
        $manualPath 2 ($manualCallVa - 2) $selectionSenderVa `
        'manual Enter Game selection'
    Assert-ManualRealmSelectionRelativeCall `
        $serverListDispatch 0 0x004EA8E4 0x005F98E0 `
        'terminal server-list dispatch'
    Assert-ManualRealmSelectionRelativeCall `
        $lastSelectionPath 3 0x005F991A 0x005FC8D0 `
        'saved-server lookup'

    if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
        throw "Origin client executable was not found: $ClientExe"
    }
    $resolvedClientExe = (Resolve-Path -LiteralPath $ClientExe).Path
    if ($Mode -ne 'Status') {
        Assert-ManualRealmSelectionProcessClosed $resolvedClientExe
    }

    $data = [IO.File]::ReadAllBytes($resolvedClientExe)
    if ($data.Length -ne $expectedLength) {
        throw "Unsupported Origin.exe size $($data.Length); expected $expectedLength bytes."
    }
    $pe = Get-PeMetadata $data
    if ($pe.Machine -ne 0x014C -or $pe.OptionalMagic -ne 0x010B -or
        $pe.ImageBase -ne 0x00400000) {
        throw 'Origin.exe is not the audited x86 PE32 image-base build.'
    }
    $hookMapping = Resolve-ExecutableFileRange `
        $pe $hookOffset $originalHook.Length
    $manualMapping = Resolve-ExecutableFileRange `
        $pe $manualCallOffset $originalHook.Length
    $guardHookMapping = Resolve-ExecutableFileRange `
        $pe $guardHookOffset $originalGuardHook.Length
    $guardCaveMapping = Resolve-ExecutableFileRange `
        $pe $guardCaveOffset $guardCaveLength
    if ($hookMapping.Va -ne $hookVa -or
        $hookMapping.Section -ne '.text' -or
        $manualMapping.Va -ne $manualCallVa -or
        $manualMapping.Section -ne '.text' -or
        $guardHookMapping.Va -ne $guardHookVa -or
        $guardHookMapping.Section -ne '.text' -or
        $guardCaveMapping.Va -ne $guardCaveVa -or
        $guardCaveMapping.Section -ne '.rdata') {
        throw 'Origin.exe realm-selection call mapping is not the audited layout.'
    }

    if (-not (Test-Bytes $data 0x0EA8E4 $serverListDispatch) -or
        -not (Test-Bytes $data 0x1F990D $roleReturnGate) -or
        -not (Test-Bytes $data 0x1F991A $lastSelectionPath) -or
        -not (Test-Bytes $data 0x1F9A08 $autoCallPrefix) -or
        -not (Test-Bytes $data 0x1F9A1E $autoCallContinuation) -or
        -not (Test-Bytes $data ($manualCallOffset - 2) $manualPath) -or
        -not (Test-Bytes $data 0x1FBF25 $selectServerPacket) -or
        -not (Test-Bytes $data 0x1FC31E $loginReturnPacket)) {
        throw 'Origin.exe realm-selection native prerequisites do not match the audited build.'
    }

    $beforeHash = Get-ManualRealmSelectionSha256 $data
    $states = Get-RealmCompositeStateMap
    if (-not $states.ContainsKey($beforeHash)) {
        throw "Unsupported Origin.exe SHA-256/state: $beforeHash"
    }
    $state = $states[$beforeHash]
    $manualStateValid = if ($state.ManualPatched) {
        Test-Bytes $data $hookOffset $patchedHook
    }
    else {
        Test-Bytes $data $hookOffset $originalHook
    }
    $guardStateValid = if ($state.GuardPatched) {
        (Test-Bytes $data $guardHookOffset $patchedGuardHook) -and
        (Test-Bytes $data $guardCaveOffset $patchedGuardCave)
    }
    else {
        (Test-Bytes $data $guardHookOffset $originalGuardHook) -and
        (Test-Bytes $data $guardCaveOffset $emptyGuardCave)
    }
    if (-not $manualStateValid -or -not $guardStateValid) {
        throw "Unsupported Origin.exe SHA-256/state: $beforeHash"
    }

    if ($Mode -eq 'Status') {
        [pscustomobject]@{
            Mode = $Mode
            Status = if ($state.ManualPatched) {
                'Patched'
            }
            else { 'Ready to apply' }
            State = $state.Name
            PetOwnerMergeOctagram = Get-RealmCompositeOctagramStatus $state
            Path = $resolvedClientExe
            Sha256 = $beforeHash
            HookFileOffset = ('0x{0:X}' -f $hookOffset)
            HookVa = ('0x{0:X8}' -f $hookVa)
            ManualCallVa = ('0x{0:X8}' -f $manualCallVa)
            CharacterBackGuard = if ($state.GuardPatched) {
                'Patched'
            }
            else { 'Original' }
        }
        return
    }
    if ($Mode -eq 'Apply' -and $state.ManualPatched) {
        [pscustomobject]@{
            Mode = $Mode
            Status = 'Already patched'
            State = $state.Name
            PetOwnerMergeOctagram = Get-RealmCompositeOctagramStatus $state
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }
    if ($Mode -eq 'Revert' -and -not $state.ManualPatched) {
        [pscustomobject]@{
            Mode = $Mode
            Status = 'Already reverted'
            State = $state.Name
            PetOwnerMergeOctagram = Get-RealmCompositeOctagramStatus $state
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }

    $before = [byte[]]$data.Clone()
    $allowedRanges = @(
        [pscustomobject]@{ Offset = $hookOffset; Length = $originalHook.Length }
    )
    if ($Mode -eq 'Apply') {
        Copy-Bytes $patchedHook $data $hookOffset
    }
    else {
        Copy-Bytes $originalHook $data $hookOffset
    }
    $peerState = Get-RealmCompositePeerState `
        $states $state 'ManualRealmSelection'
    $expectedAfterHash = $peerState.Hash
    $mutationCount = Assert-ManualRealmSelectionMutation `
        $before $data $allowedRanges 5 $expectedAfterHash `
        'Staged manual realm-selection candidate'

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        $BackupRoot = Join-Path $RepositoryRoot 'backups'
    }
    [IO.Directory]::CreateDirectory($BackupRoot) | Out-Null
    $backupDirectory = Join-Path $BackupRoot (
        'origin-manual-realm-selection-' + $Mode + '-' +
        (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
        [guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    $backupPath = Join-Path $backupDirectory 'Origin.exe'
    Copy-Item -LiteralPath $resolvedClientExe -Destination $backupPath
    if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne
        $beforeHash) {
        throw "Manual realm-selection backup verification failed: $backupPath"
    }

    $operationId = [guid]::NewGuid().ToString('N')
    $stagePath = "$resolvedClientExe.$operationId.stage"
    $replaceBackup = "$resolvedClientExe.$operationId.replaced"
    $restoreStage = "$resolvedClientExe.$operationId.restore"
    $rollbackBackup = "$resolvedClientExe.$operationId.rollback"
    try {
        [IO.File]::WriteAllBytes($stagePath, $data)
        $staged = [IO.File]::ReadAllBytes($stagePath)
        [void](Assert-ManualRealmSelectionMutation `
            $before $staged $allowedRanges 5 $expectedAfterHash `
            'Written manual realm-selection staging file')

        Assert-ManualRealmSelectionProcessClosed $resolvedClientExe
        if ((Get-FileHash -LiteralPath $resolvedClientExe `
                -Algorithm SHA256).Hash -ne $beforeHash) {
            throw 'Origin.exe changed while the manual realm-selection patch was staged.'
        }
        [IO.File]::Replace(
            $stagePath,
            $resolvedClientExe,
            $replaceBackup,
            $true)
        $written = [IO.File]::ReadAllBytes($resolvedClientExe)
        [void](Assert-ManualRealmSelectionMutation `
            $before $written $allowedRanges 5 $expectedAfterHash `
            'Installed Origin.exe')
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
            else { $null }
            if ($currentHash -ne $beforeHash) {
                Copy-Item -LiteralPath $backupPath -Destination $restoreStage
                if ((Get-FileHash -LiteralPath $restoreStage `
                        -Algorithm SHA256).Hash -ne $beforeHash) {
                    throw 'Automatic-restore stage hash mismatch.'
                }
                Assert-ManualRealmSelectionProcessClosed $resolvedClientExe
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
        State = $peerState.Name
        PetOwnerMergeOctagram = Get-RealmCompositeOctagramStatus $peerState
        Path = $resolvedClientExe
        ChangedBytes = $mutationCount
        Backup = $backupPath
        BeforeSha256 = $beforeHash
        AfterSha256 = $expectedAfterHash
        HookFileOffset = ('0x{0:X}' -f $hookOffset)
        HookVa = ('0x{0:X8}' -f $hookVa)
        ManualCallVa = ('0x{0:X8}' -f $manualCallVa)
        CharacterBackGuard = if ($state.GuardPatched) {
            'Preserved patched'
        }
        else { 'Preserved original' }
    }
}
