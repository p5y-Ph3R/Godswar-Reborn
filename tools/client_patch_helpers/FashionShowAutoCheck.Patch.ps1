function Get-FashionShowAutoCheckSha256 {
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

function Assert-FashionShowAutoCheckProcessClosed {
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

function Assert-FashionShowAutoCheckRelativeBranch {
    param(
        [byte[]]$Code,
        [int]$InstructionOffset,
        [uint64]$CodeVa,
        [byte]$Opcode,
        [uint64]$ExpectedTarget
    )

    if ($InstructionOffset -lt 0 -or
        $InstructionOffset + 5 -gt $Code.Length -or
        $Code[$InstructionOffset] -ne $Opcode) {
        throw 'Internal Fashion Show branch encoding is invalid.'
    }
    $target = $CodeVa + $InstructionOffset + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
    if ($target -ne $ExpectedTarget) {
        throw "Internal Fashion Show branch targets 0x$('{0:X8}' -f $target), expected 0x$('{0:X8}' -f $ExpectedTarget)."
    }
}

function Assert-FashionShowAutoCheckMutation {
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
    if ((Get-FashionShowAutoCheckSha256 $After) -ne $ExpectedHash) {
        throw "$Label failed exact SHA-256 verification."
    }
    return $mutationCount
}

function Invoke-FashionShowAutoCheckPatch {
    param(
        [string]$ClientExe,
        [string]$Mode,
        [string]$BackupRoot,
        [string]$RepositoryRoot
    )

    $baseSha256 =
        '92F6740BD0095F869C4FF54E7269CB4E21B8B43BB89A078AF711A5C1973AD181'
    $patchedSha256 =
        '9354BDB00376E16F5C2D1E682637790D90C3930B8F3655456F8F49F3314C6728'
    $expectedLength = 6676480

    # The validated item-copy path enters with EAX=destination equipment
    # record, EBP=the actor, and EBX=the incoming record. Slot 12 lives at
    # actor+0x7438 and its old occupancy marker is record+0xF4. The trampoline
    # only checks Show for the local actor when that old marker is zero.
    $hookOffset = 0x0ADB4E
    $hookVa = 0x004ADB4E
    $continuationOffset = 0x0ADB53
    $continuationVa = 0x004ADB53

    # This is the terminal 96-byte executable .rdata allocation. Existing
    # QuestView and character-speed patches own 0x5C3F00..0x5C3F9F; this
    # patch exclusively owns the adjacent, previously documented unallocated
    # 0x5C3FA0..0x5C3FFF range.
    $caveOffset = 0x5C3FA0
    $caveVa = 0x009C3FA0
    $caveReserveLength = 0x60

    $originalHook = Convert-HexBytes 'B9 3E 00 00 00'
    $patchedHook = Convert-HexBytes 'E9 4D 64 51 00'
    $caveCode = Convert-HexBytes @'
9C 60
8D 95 38 74 00 00
3B C2
75 2B
83 B8 F4 00 00 00 00
75 22
E8 F6 F6 BA FF
85 C0
74 19
3B 68 08
75 14
8B 88 08 53 00 00
85 C9
74 0A
8B 11
6A 01
FF 92 DC 00 00 00
61 9D
B9 3E 00 00 00
E9 70 9B AE FF
'@
    $emptyCave = [byte[]]::new($caveReserveLength)
    $patchedCave = [byte[]]::new($caveReserveLength)
    Copy-Bytes $caveCode $patchedCave 0

    $slotTypeBranches = Convert-HexBytes @'
8B 83 F4 00 00 00 83 B8 14 02 00 00 0C
0F 85 78 01 00 00 8D 85 38 74 00 00 E9 E8 00 00 00
8B 83 F4 00 00 00 83 B8 14 02 00 00 0D
0F 85 5A 01 00 00 8D 85 30 75 00 00 E9 CA 00 00 00
8B 83 F4 00 00 00 83 B8 14 02 00 00 0E
0F 85 3C 01 00 00 8D 85 28 76 00 00
'@
    $continuation = Convert-HexBytes @'
8D 7C 24 18 8B F0 F3 A5 B9 3E 00 00 00 8B F8 8B F3
F3 A5 8B 8C 24 18 03 00 00
'@
    $nativeAutoShow = Convert-HexBytes @'
8B 43 08 39 B8 2C 75 00 00 74 54 8B 8B 08 53 00 00
8B 11 8B 82 E0 00 00 00 FF D0 84 C0 75 40 39 3D 90
D1 5A 01 75 38 8B 0D 50 61 57 01 8B 11 8B 52 1C 6A
0C 8D 44 24 20 50 66 C7 44 24 26 D8 27 66 C7 44 24
24 0C 00 89 7C 24 2C FF D2 8B 8B 08 53 00 00 8B 01
8B 90 DC 00 00 00 6A 01 FF D2
'@
    $showControlConstruction = Convert-HexBytes @'
8B 0D 54 61 57 01 8B 11 8B 42 40 6A 01 68 03 AE 01
00 FF D0 89 86 08 53 00 00 8B 10 8B C8 8B 82 DC 00
00 00 6A 00 FF D0
'@

    if ($caveCode.Length -ne 67 -or
        $patchedHook.Length -ne $originalHook.Length) {
        throw 'Internal Fashion Show patch length validation failed.'
    }
    Assert-FashionShowAutoCheckRelativeBranch `
        $patchedHook 0 $hookVa 0xE9 $caveVa
    Assert-FashionShowAutoCheckRelativeBranch `
        $caveCode 21 $caveVa 0xE8 0x005736B0
    Assert-FashionShowAutoCheckRelativeBranch `
        $caveCode 62 $caveVa 0xE9 $continuationVa

    # Pin the transition, local-player, Show-control, preservation, and
    # displaced-instruction semantics. Every short branch converges on the
    # shared restore before replay and never enters the setter for a rejected
    # transition or remote actor.
    if (-not (Test-Bytes $caveCode 0 (
                Convert-HexBytes @'
9C 60 8D 95 38 74 00 00 3B C2 75 2B
'@)) -or
        10 + 2 + [int][sbyte]$caveCode[11] -ne 55 -or
        -not (Test-Bytes $caveCode 12 (
                Convert-HexBytes '83 B8 F4 00 00 00 00 75 22')) -or
        19 + 2 + [int][sbyte]$caveCode[20] -ne 55 -or
        -not (Test-Bytes $caveCode 26 (
                Convert-HexBytes '85 C0 74 19 3B 68 08 75 14')) -or
        28 + 2 + [int][sbyte]$caveCode[29] -ne 55 -or
        33 + 2 + [int][sbyte]$caveCode[34] -ne 55 -or
        -not (Test-Bytes $caveCode 35 (
                Convert-HexBytes '8B 88 08 53 00 00 85 C9 74 0A')) -or
        43 + 2 + [int][sbyte]$caveCode[44] -ne 55 -or
        -not (Test-Bytes $caveCode 45 (
                Convert-HexBytes '8B 11 6A 01 FF 92 DC 00 00 00')) -or
        -not (Test-Bytes $caveCode 55 (
                Convert-HexBytes '61 9D')) -or
        -not (Test-Bytes $caveCode 57 $originalHook)) {
        throw 'Internal Fashion Show transition semantics are invalid.'
    }

    if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
        throw "Origin client executable was not found: $ClientExe"
    }
    $resolvedClientExe = (Resolve-Path -LiteralPath $ClientExe).Path
    if ($Mode -ne 'Status') {
        Assert-FashionShowAutoCheckProcessClosed $resolvedClientExe
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
        $pe $hookOffset $patchedHook.Length
    $caveMapping = Resolve-ExecutableFileRange `
        $pe $caveOffset $caveReserveLength
    $rdata = @($pe.Sections | Where-Object Name -eq '.rdata')
    if ($hookMapping.Va -ne $hookVa -or
        $hookMapping.Section -ne '.text' -or
        $caveMapping.Va -ne $caveVa -or
        $caveMapping.Section -ne '.rdata' -or
        $rdata.Count -ne 1 -or
        [uint64]$caveOffset + $caveReserveLength -ne
            [uint64]$rdata[0].RawOffset + $rdata[0].RawSize -or
        $caveOffset -ne (0x5C3F20 + 0x80)) {
        throw 'Origin.exe Fashion Show hook/allocation PE mapping is not the audited non-overlapping layout.'
    }

    if (-not (Test-Bytes $data 0x0ADA48 $slotTypeBranches) -or
        -not (Test-Bytes $data $continuationOffset $continuation) -or
        -not (Test-Bytes $data 0x17862A $nativeAutoShow) -or
        -not (Test-Bytes $data 0x1734B3 $showControlConstruction)) {
        throw 'Origin.exe Fashion Show native prerequisites do not match the audited build.'
    }

    $beforeHash = Get-FashionShowAutoCheckSha256 $data
    $states = @{
        $baseSha256 = [pscustomobject]@{
            Name = 'AuditedFashionBase'
            Patched = $false
            PeerHash = $patchedSha256
            PeerName = 'FashionShowAutoCheckPatched'
        }
        $patchedSha256 = [pscustomobject]@{
            Name = 'FashionShowAutoCheckPatched'
            Patched = $true
            PeerHash = $baseSha256
            PeerName = 'AuditedFashionBase'
        }
    }
    if (-not $states.ContainsKey($beforeHash)) {
        throw "Unsupported Origin.exe SHA-256/state: $beforeHash"
    }
    $state = $states[$beforeHash]
    $validState = if ($state.Patched) {
        (Test-Bytes $data $hookOffset $patchedHook) -and
        (Test-Bytes $data $caveOffset $patchedCave)
    }
    else {
        (Test-Bytes $data $hookOffset $originalHook) -and
        (Test-Bytes $data $caveOffset $emptyCave)
    }
    if (-not $validState) {
        throw "Unsupported Origin.exe SHA-256/state: $beforeHash"
    }

    if ($Mode -eq 'Status') {
        [pscustomobject]@{
            Mode = $Mode
            Status = if ($state.Patched) { 'Patched' } else { 'Ready to apply' }
            State = $state.Name
            Path = $resolvedClientExe
            Sha256 = $beforeHash
            HookVa = ('0x{0:X8}' -f $hookVa)
            CaveVa = ('0x{0:X8}' -f $caveVa)
            CaveOwnership = '0x5C3FA0-0x5C3FFF (exclusive)'
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
        [pscustomobject]@{ Offset = $hookOffset; Length = $originalHook.Length },
        [pscustomobject]@{ Offset = $caveOffset; Length = $caveReserveLength }
    )
    $expectedMutationCount =
        (Measure-ByteDifference $originalHook $patchedHook) +
        (Measure-ByteDifference $emptyCave $patchedCave)
    if ($expectedMutationCount -ne 57) {
        throw 'Internal Fashion Show mutation-count validation failed.'
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
    $mutationCount = Assert-FashionShowAutoCheckMutation `
        $before $data $allowedRanges $expectedMutationCount `
        $expectedAfterHash 'Staged Fashion Show candidate'

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        $BackupRoot = Join-Path $RepositoryRoot 'backups'
    }
    [IO.Directory]::CreateDirectory($BackupRoot) | Out-Null
    $backupDirectory = Join-Path $BackupRoot (
        'origin-fashion-show-auto-check-' + $Mode + '-' +
        (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
        [guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    $backupPath = Join-Path $backupDirectory 'Origin.exe'
    Copy-Item -LiteralPath $resolvedClientExe -Destination $backupPath
    if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne
        $beforeHash) {
        throw "Fashion Show backup verification failed: $backupPath"
    }

    $operationId = [guid]::NewGuid().ToString('N')
    $stagePath = "$resolvedClientExe.$operationId.stage"
    $replaceBackup = "$resolvedClientExe.$operationId.replaced"
    $restoreStage = "$resolvedClientExe.$operationId.restore"
    $rollbackBackup = "$resolvedClientExe.$operationId.rollback"
    try {
        [IO.File]::WriteAllBytes($stagePath, $data)
        $staged = [IO.File]::ReadAllBytes($stagePath)
        [void](Assert-FashionShowAutoCheckMutation `
            $before $staged $allowedRanges $expectedMutationCount `
            $expectedAfterHash 'Written Fashion Show staging file')

        Assert-FashionShowAutoCheckProcessClosed $resolvedClientExe
        if ((Get-FileHash -LiteralPath $resolvedClientExe `
                -Algorithm SHA256).Hash -ne $beforeHash) {
            throw 'Origin.exe changed while the Fashion Show patch was staged.'
        }
        [IO.File]::Replace(
            $stagePath,
            $resolvedClientExe,
            $replaceBackup,
            $true)
        $written = [IO.File]::ReadAllBytes($resolvedClientExe)
        [void](Assert-FashionShowAutoCheckMutation `
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
            else { $null }
            if ($currentHash -ne $beforeHash) {
                Copy-Item -LiteralPath $backupPath -Destination $restoreStage
                if ((Get-FileHash -LiteralPath $restoreStage `
                        -Algorithm SHA256).Hash -ne $beforeHash) {
                    throw 'Automatic-restore stage hash mismatch.'
                }
                Assert-FashionShowAutoCheckProcessClosed $resolvedClientExe
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
        CaveBytes = $caveCode.Length
        CaveReserveBytes = $caveReserveLength
    }
}
