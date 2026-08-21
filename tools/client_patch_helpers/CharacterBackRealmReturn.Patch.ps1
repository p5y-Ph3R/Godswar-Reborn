function Get-CharacterBackRealmReturnSha256 {
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

function Assert-CharacterBackRealmReturnProcessClosed {
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

function Assert-CharacterBackRealmReturnBranch {
    param(
        [byte[]]$Code,
        [int]$InstructionOffset,
        [uint64]$CodeVa,
        [uint64]$ExpectedTarget,
        [string]$Label
    )

    if ($InstructionOffset -lt 0 -or
        $InstructionOffset + 5 -gt $Code.Length -or
        $Code[$InstructionOffset] -ne 0xE9) {
        throw "Internal $Label branch encoding is invalid."
    }
    $target = $CodeVa + $InstructionOffset + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
    if ($target -ne $ExpectedTarget) {
        throw "Internal $Label branch targets 0x$('{0:X8}' -f $target), expected 0x$('{0:X8}' -f $ExpectedTarget)."
    }
}

function Get-CharacterBackRealmReturnRelativeCaveXrefs {
    param(
        [byte[]]$Data,
        [object]$Pe,
        [uint64]$StartVa,
        [uint64]$EndVa
    )

    $result = @()
    foreach ($section in $Pe.Sections | Where-Object {
            ($_.Characteristics -band 0x20000000) -ne 0
        }) {
        $first = [int]$section.RawOffset
        $last = [int]($section.RawOffset + $section.RawSize - 5)
        for ($offset = $first; $offset -le $last; $offset++) {
            if ($Data[$offset] -ne 0xE8 -and $Data[$offset] -ne 0xE9) {
                continue
            }
            $sourceVa = [uint64]$Pe.ImageBase + $section.VirtualAddress +
                ([uint64]$offset - $section.RawOffset)
            $target = [int64]$sourceVa + 5 +
                [BitConverter]::ToInt32($Data, $offset + 1)
            if ($target -ge $StartVa -and $target -lt $EndVa) {
                $result += [pscustomobject]@{
                    Offset = $offset
                    Target = [uint64]$target
                }
            }
        }
    }
    return $result
}

function Get-CharacterBackRealmReturnAbsoluteCaveReferences {
    param(
        [byte[]]$Data,
        [uint32]$StartVa,
        [uint32]$EndVa
    )

    $result = @()
    for ($offset = 0; $offset -le $Data.Length - 4; $offset++) {
        $value = [BitConverter]::ToUInt32($Data, $offset)
        if ($value -ge $StartVa -and $value -lt $EndVa) {
            $result += [pscustomobject]@{
                Offset = $offset
                Value = $value
            }
        }
    }
    return $result
}

function Assert-CharacterBackRealmReturnMutation {
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
    if ((Get-CharacterBackRealmReturnSha256 $After) -ne $ExpectedHash) {
        throw "$Label failed exact SHA-256 verification."
    }
    return $mutationCount
}

function Invoke-CharacterBackRealmReturnPatch {
    param(
        [string]$ClientExe,
        [string]$Mode,
        [string]$BackupRoot,
        [string]$RepositoryRoot
    )

    $expectedLength = 6676480

    $hookOffset = 0x1F58B6
    $hookVa = 0x005F58B6
    $normalContinuationVa = 0x005F58BC
    $missingContinuationVa = 0x005F58EA
    $caveOffset = 0x53E3E0
    $caveVa = 0x0093E3E0
    $caveReserveLength = 112

    $originalHook = Convert-HexBytes '8B 0D A0 60 57 01'
    $patchedHook = Convert-HexBytes 'E9 25 8B 34 00 90'
    $caveCode = Convert-HexBytes @'
83 3D 4C 5F 57 01 02 75 12
A1 A0 60 57 01 85 C0 74 14
A1 8C 60 57 01 85 C0 74 0B
8B 0D A0 60 57 01 E9 B6 74 CB FF
BF 02 00 00 00 C6 05 66 5C 57 01 01
89 3D 50 5F 57 01 E9 CD 74 CB FF
'@
    $emptyCave = [byte[]]::new($caveReserveLength)
    $patchedCave = [byte[]]::new($caveReserveLength)
    Copy-Bytes $caveCode $patchedCave 0

    $hookPrefix = Convert-HexBytes '33 C0 E8 CA 65 F9 FF'
    $normalContinuation = Convert-HexBytes @'
8B 01 8B 90 80 00 00 00 BF 02 00 00 00
'@
    $stateAndSecondRoot = Convert-HexBytes @'
53 C6 05 66 5C 57 01 01 89 3D 50 5F 57 01 FF D2
8B 0D 8C 60 57 01 8B 01 8B 90 80 00 00 00 53 FF D2
'@
    $missingContinuation = Convert-HexBytes @'
D9 05 3C 1D 96 00 8B 0D 68 61 57 01
'@
    $backDispatch = Convert-HexBytes 'E8 64 20 00 00'
    $disconnectPath = Convert-HexBytes @'
8B 0D 50 61 57 01 3B CB 88 98 30 02 00 00 74 07
8B 01 8B 50 0C FF D2
'@
    $roleReturnMarker = Convert-HexBytes @'
C6 81 58 02 00 00 01 5B 59 C2 04 00
'@
    $lifecycleHook = Convert-HexBytes 'E9 36 1E 50 00'
    $lifecycleCave = Convert-HexBytes @'
C6 05 70 5F 57 01 00 68 04 5A 9E 00 E9 B9 E1 AF FF
'@
    $cavePrefix = Convert-HexBytes @'
E9 64 EB 05 01 C9 83 E9 28 89 4C 24 18 C3
00 00 00 00 00 00 00 00 00 00
'@
    $caveSuffix = Convert-HexBytes @'
20 00 20 00 20 00 20 00 20 00 20 00 20 00 20 00
'@
    $manualOriginal = Convert-HexBytes 'E8 92 23 00 00'
    $manualPatched = Convert-HexBytes '90 90 90 90 90'

    if ($originalHook.Length -ne 6 -or $caveCode.Length -ne 61 -or
        $patchedCave.Length -ne $caveReserveLength -or
        (Measure-ByteDifference $originalHook $patchedHook) -ne 6 -or
        @($caveCode | Where-Object { $_ -ne 0 }).Count -ne 58) {
        throw 'Internal character Back guard length validation failed.'
    }
    Assert-CharacterBackRealmReturnBranch `
        $patchedHook 0 $hookVa $caveVa 'hook'
    Assert-CharacterBackRealmReturnBranch `
        $caveCode 0x21 $caveVa $normalContinuationVa 'normal continuation'
    Assert-CharacterBackRealmReturnBranch `
        $caveCode 0x38 $caveVa $missingContinuationVa 'missing-root continuation'
    if ($caveCode[0x07] -ne 0x75 -or
        0x07 + 2 + [int][sbyte]$caveCode[0x08] -ne 0x1B -or
        0x10 + 2 + [int][sbyte]$caveCode[0x11] -ne 0x26 -or
        0x19 + 2 + [int][sbyte]$caveCode[0x1A] -ne 0x26 -or
        -not (Test-Bytes $caveCode 0x1B $originalHook)) {
        throw 'Internal character Back guard path validation failed.'
    }

    if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
        throw "Origin client executable was not found: $ClientExe"
    }
    $resolvedClientExe = (Resolve-Path -LiteralPath $ClientExe).Path
    if ($Mode -ne 'Status') {
        Assert-CharacterBackRealmReturnProcessClosed $resolvedClientExe
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
    $caveMapping = Resolve-ExecutableFileRange `
        $pe $caveOffset $caveReserveLength
    if ($hookMapping.Va -ne $hookVa -or $hookMapping.Section -ne '.text' -or
        $caveMapping.Va -ne $caveVa -or $caveMapping.Section -ne '.rdata') {
        throw 'Origin.exe character Back guard ranges are not in the audited layout.'
    }

    if (-not (Test-Bytes $data ($hookOffset - $hookPrefix.Length) $hookPrefix) -or
        -not (Test-Bytes $data 0x1F58BC $normalContinuation) -or
        -not (Test-Bytes $data 0x1F58C9 $stateAndSecondRoot) -or
        -not (Test-Bytes $data 0x1F58EA $missingContinuation) -or
        -not (Test-Bytes $data 0x1F37A7 $backDispatch) -or
        -not (Test-Bytes $data 0x1F5840 $disconnectPath) -or
        -not (Test-Bytes $data 0x1F59E9 $roleReturnMarker) -or
        -not (Test-Bytes $data 0x0C14C5 $lifecycleHook) -or
        -not (Test-Bytes $data 0x5C3300 $lifecycleCave) -or
        -not (Test-Bytes $data ($caveOffset - $cavePrefix.Length) $cavePrefix) -or
        -not (Test-Bytes $data ($caveOffset + $caveReserveLength) $caveSuffix)) {
        throw 'Origin.exe character Back native prerequisites do not match the audited build.'
    }

    $beforeHash = Get-CharacterBackRealmReturnSha256 $data
    $states = Get-RealmCompositeStateMap
    if (-not $states.ContainsKey($beforeHash)) {
        throw "Unsupported Origin.exe SHA-256/state: $beforeHash"
    }
    $state = $states[$beforeHash]
    $expectedHook = if ($state.GuardPatched) {
        $patchedHook
    }
    else {
        $originalHook
    }
    $expectedCave = if ($state.GuardPatched) {
        $patchedCave
    }
    else {
        $emptyCave
    }
    $expectedManual = if ($state.ManualPatched) {
        $manualPatched
    }
    else {
        $manualOriginal
    }
    if (-not (Test-Bytes $data $hookOffset $expectedHook) -or
        -not (Test-Bytes $data $caveOffset $expectedCave) -or
        -not (Test-Bytes $data 0x1F9A19 $expectedManual)) {
        throw "Unsupported Origin.exe SHA-256/state: $beforeHash"
    }

    $relativeXrefs = @(Get-CharacterBackRealmReturnRelativeCaveXrefs `
        $data $pe $caveVa ($caveVa + $caveReserveLength))
    $absoluteRefs = @(Get-CharacterBackRealmReturnAbsoluteCaveReferences `
        $data $caveVa ($caveVa + $caveReserveLength))
    $xrefStateValid = if ($state.GuardPatched) {
        $relativeXrefs.Count -eq 1 -and
            $relativeXrefs[0].Offset -eq $hookOffset -and
            $relativeXrefs[0].Target -eq $caveVa
    }
    else {
        $relativeXrefs.Count -eq 0
    }
    if (-not $xrefStateValid -or $absoluteRefs.Count -ne 0) {
        throw 'Origin.exe character Back cave reference audit failed.'
    }

    if ($Mode -eq 'Status') {
        [pscustomobject]@{
            Mode = $Mode
            Status = if ($state.GuardPatched) {
                'Patched'
            }
            else {
                'Ready to apply'
            }
            State = $state.Name
            PetOwnerMergeOctagram = Get-RealmCompositeOctagramStatus $state
            ManualRealmSelection = $state.ManualPatched
            Path = $resolvedClientExe
            Sha256 = $beforeHash
            HookFileOffset = ('0x{0:X}' -f $hookOffset)
            HookVa = ('0x{0:X8}' -f $hookVa)
            CaveFileOffset = ('0x{0:X}' -f $caveOffset)
            CaveVa = ('0x{0:X8}' -f $caveVa)
            CaveInboundRelativeXrefs = $relativeXrefs.Count
            CaveAbsoluteReferences = $absoluteRefs.Count
        }
        return
    }
    if ($Mode -eq 'Apply' -and $state.GuardPatched) {
        [pscustomobject]@{
            Mode = $Mode
            Status = 'Already patched'
            State = $state.Name
            PetOwnerMergeOctagram = Get-RealmCompositeOctagramStatus $state
            ManualRealmSelection = $state.ManualPatched
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }
    if ($Mode -eq 'Revert' -and -not $state.GuardPatched) {
        [pscustomobject]@{
            Mode = $Mode
            Status = 'Already reverted'
            State = $state.Name
            PetOwnerMergeOctagram = Get-RealmCompositeOctagramStatus $state
            ManualRealmSelection = $state.ManualPatched
            Path = $resolvedClientExe
            Sha256 = $beforeHash
        }
        return
    }

    $before = [byte[]]$data.Clone()
    $allowedRanges = @(
        [pscustomobject]@{ Offset = $hookOffset; Length = $originalHook.Length },
        [pscustomobject]@{ Offset = $caveOffset; Length = $caveReserveLength }
    )
    if ($Mode -eq 'Apply') {
        Copy-Bytes $patchedHook $data $hookOffset
        Copy-Bytes $patchedCave $data $caveOffset
    }
    else {
        Copy-Bytes $originalHook $data $hookOffset
        Copy-Bytes $emptyCave $data $caveOffset
    }
    $peerState = Get-RealmCompositePeerState `
        $states $state 'CharacterBackGuard'
    $expectedAfterHash = $peerState.Hash
    $mutationCount = Assert-CharacterBackRealmReturnMutation `
        $before $data $allowedRanges 64 $expectedAfterHash `
        'Staged character Back guard candidate'

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        $BackupRoot = Join-Path $RepositoryRoot 'backups'
    }
    [IO.Directory]::CreateDirectory($BackupRoot) | Out-Null
    $backupDirectory = Join-Path $BackupRoot (
        'origin-character-back-realm-return-' + $Mode + '-' +
        (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
        [guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    $backupPath = Join-Path $backupDirectory 'Origin.exe'
    Copy-Item -LiteralPath $resolvedClientExe -Destination $backupPath
    if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne
        $beforeHash) {
        throw "Character Back guard backup verification failed: $backupPath"
    }

    $operationId = [guid]::NewGuid().ToString('N')
    $stagePath = "$resolvedClientExe.$operationId.stage"
    $replaceBackup = "$resolvedClientExe.$operationId.replaced"
    $restoreStage = "$resolvedClientExe.$operationId.restore"
    $rollbackBackup = "$resolvedClientExe.$operationId.rollback"
    try {
        [IO.File]::WriteAllBytes($stagePath, $data)
        $staged = [IO.File]::ReadAllBytes($stagePath)
        [void](Assert-CharacterBackRealmReturnMutation `
            $before $staged $allowedRanges 64 $expectedAfterHash `
            'Written character Back guard staging file')

        Assert-CharacterBackRealmReturnProcessClosed $resolvedClientExe
        if ((Get-FileHash -LiteralPath $resolvedClientExe `
                -Algorithm SHA256).Hash -ne $beforeHash) {
            throw 'Origin.exe changed while the character Back guard was staged.'
        }
        [IO.File]::Replace(
            $stagePath,
            $resolvedClientExe,
            $replaceBackup,
            $true)
        $written = [IO.File]::ReadAllBytes($resolvedClientExe)
        [void](Assert-CharacterBackRealmReturnMutation `
            $before $written $allowedRanges 64 $expectedAfterHash `
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
            else {
                $null
            }
            if ($currentHash -ne $beforeHash) {
                Copy-Item -LiteralPath $backupPath -Destination $restoreStage
                if ((Get-FileHash -LiteralPath $restoreStage `
                        -Algorithm SHA256).Hash -ne $beforeHash) {
                    throw 'Automatic-restore stage hash mismatch.'
                }
                Assert-CharacterBackRealmReturnProcessClosed $resolvedClientExe
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
        ManualRealmSelection = $state.ManualPatched
        Path = $resolvedClientExe
        ChangedBytes = $mutationCount
        Backup = $backupPath
        BeforeSha256 = $beforeHash
        AfterSha256 = $expectedAfterHash
        HookFileOffset = ('0x{0:X}' -f $hookOffset)
        HookVa = ('0x{0:X8}' -f $hookVa)
        CaveFileOffset = ('0x{0:X}' -f $caveOffset)
        CaveVa = ('0x{0:X8}' -f $caveVa)
    }
}
