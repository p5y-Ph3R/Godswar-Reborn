function Invoke-AvatarPreviewGuardPatch {
    param(
        [string]$ClientExe,
        [string]$Mode,
        [string]$BackupRoot,
        [string]$RepositoryRoot
    )

# Origin.exe VA 0x005F4A20 builds the character-preview avatar. The native
# routine validates its arguments, then immediately assumes all male/female 3D
# avatar objects exist. The recurring crash at VA 0x005F4ADD is:
#
#   mov ecx, [0x0157608C]
#   mov eax, [ecx]             ; ECX was null in the captured dumps
#
# Hook after the native argument checks and before any local C++ object is
# constructed. The cave verifies all three male and three female objects used
# by the routine. A missing object returns through the native untouched
# epilogue; otherwise it replays the displaced instructions and continues.
# This is deliberately fail-closed: it skips one preview build instead of
# sleeping/re-entering the resource loader on the main render thread.
$expectedLength = 6676480
$expectedMachine = 0x014C
$expectedOptionalMagic = 0x010B
$expectedImageBase = 0x00400000

# This older patch is the prerequisite for the additive V4 preload patch.
# Refuse a partial downgrade: V4 must be reverted with its own tool first.
$v4PreloadHookOffset = 0x0C14D6
$v4PreloadOriginalHook = Convert-HexBytes '68 A0 39 95 00'
$v4PreloadCaveOffset = 0x5C3366
$v4PreloadEmptyCave = [byte[]]::new(154)
$v4TimeoutHookOffset = 0x1F58B6
$v4TimeoutOriginalHook = Convert-HexBytes '8B 0D A0 60 57 01'
$v4TimeoutCaveOffset = 0x5C341F
$v4TimeoutEmptyCave = [byte[]]::new(96)

# The client intentionally unloads the selection-avatar resources after world
# entry, but its one-time initialization flag is never reset. When state 2
# installs the LOGIN/character-selection object, clear only that flag and
# replay the displaced push. The native dispatcher then calls the LOGIN
# object's existing initializer and reconstructs both avatar resource groups.
# This hook is state-specific and cannot reinitialize them during gameplay.
$lifecycleHookOffset = 0x0C14C5
$lifecycleHookVa = 0x004C14C5
$lifecycleContinuationOffset = 0x0C14CA
$lifecycleContinuationVa = 0x004C14CA
$lifecycleCaveOffset = 0x5C3300
$lifecycleCaveVa = 0x009C3300
$originalLifecycleHook = Convert-HexBytes '68 04 5A 9E 00'
$patchedLifecycleHook = Convert-HexBytes 'E9 36 1E 50 00'
$lifecycleCaveCode = Convert-HexBytes @'
C6 05 70 5F 57 01 00
68 04 5A 9E 00
E9 B9 E1 AF FF
'@
$emptyLifecycleCave = [byte[]]::new($lifecycleCaveCode.Length)
$lifecycleContinuation = Convert-HexBytes 'C7 05 6C 5F 57 01 04 5A 9E 00 FF D2'

$hookOffset = 0x1F4A82
$hookVa = 0x005F4A82
$caveOffset = 0x5C32B0
$caveVa = 0x009C32B0
$normalContinuationVa = 0x005F4A90
$safeEpilogueVa = 0x005F516D

$originalHook = Convert-HexBytes '33 FF 33 C9 EB 08'
$patchedHook = Convert-HexBytes 'E9 29 E8 3C 00 90'
$caveCode = Convert-HexBytes @'
A1 88 60 57 01 85 C0 74 36
A1 8C 60 57 01 85 C0 74 2D
A1 90 60 57 01 85 C0 74 24
A1 9C 60 57 01 85 C0 74 1B
A1 A0 60 57 01 85 C0 74 12
A1 A4 60 57 01 85 C0 74 09
33 FF 33 C9 E9 A1 17 C3 FF
E9 79 1E C3 FF
'@
$emptyCave = [byte[]]::new($caveCode.Length)

# These instructions are not modified, but pin the diagnosed male/female
# dereference sites so this tool cannot be reused against a different build.
$maleFaultSiteOffset = 0x1F4AD7
$maleFaultSite = Convert-HexBytes '8B 0D 8C 60 57 01 8B 01'
$femaleFaultSiteOffset = 0x1F4B72
$femaleFaultSite = Convert-HexBytes '8B 0D A0 60 57 01 8B 11'
$normalContinuationOffset = 0x1F4A90
$normalContinuation = Convert-HexBytes '8B 86 FC 01 00 00 8B D8 C1 E3 05 2B D8 03 D9'
$safeEpilogueOffset = 0x1F516D
$safeEpilogue = Convert-HexBytes '8B 8C 24 90 01 00 00 64 89 0D 00 00 00 00'

# A second selection-avatar builder at VA 0x005F0590 has the same resource
# assumption and has its own historical crash at VA 0x005F060E. Hook after its
# complete SEH prologue but before local object construction. Missing resources
# use that routine's native untouched cleanup/ret-12 epilogue.
$secondaryHookOffset = 0x1F05C2
$secondaryHookVa = 0x005F05C2
$secondaryCaveOffset = 0x5C3320
$secondaryCaveVa = 0x009C3320
$secondaryNormalContinuationOffset = 0x1F05C8
$secondaryNormalContinuationVa = 0x005F05C8
$secondarySafeEpilogueOffset = 0x1F0DC3
$secondarySafeEpilogueVa = 0x005F0DC3
$originalSecondaryHook = Convert-HexBytes '8B 44 24 50 33 DB'
$patchedSecondaryHook = Convert-HexBytes 'E9 59 2D 3D 00 90'
$secondaryCaveCode = Convert-HexBytes @'
A1 88 60 57 01 85 C0 74 38
A1 8C 60 57 01 85 C0 74 2F
A1 90 60 57 01 85 C0 74 26
A1 9C 60 57 01 85 C0 74 1D
A1 A0 60 57 01 85 C0 74 14
A1 A4 60 57 01 85 C0 74 0B
8B 44 24 50 33 DB E9 67 D2 C2 FF
E9 5D DA C2 FF
'@
$emptySecondaryCave = [byte[]]::new($secondaryCaveCode.Length)
$secondaryNormalContinuation = Convert-HexBytes '89 44 24 1C C7 44 24 38 0F 00 00 00'
$secondarySafeEpilogue = Convert-HexBytes @'
8B 4C 24 40 64 89 0D 00 00 00 00 59 5F 5E 5D 5B
'@
$secondaryFaultSiteOffset = 0x1F0608
$secondaryFaultSite = Convert-HexBytes '8B 0D A0 60 57 01 8B 11'

if ($caveCode.Length -ne 68 -or $lifecycleCaveCode.Length -ne 17 -or
    $secondaryCaveCode.Length -ne 70 -or
    $caveVa + 0x3A + 5 + [BitConverter]::ToInt32($caveCode, 0x3B) -ne
        $normalContinuationVa -or
    $caveVa + 0x3F + 5 + [BitConverter]::ToInt32($caveCode, 0x40) -ne
        $safeEpilogueVa -or
    $hookVa + 5 + [BitConverter]::ToInt32($patchedHook, 1) -ne $caveVa -or
    $lifecycleHookVa + 5 +
        [BitConverter]::ToInt32($patchedLifecycleHook, 1) -ne $lifecycleCaveVa -or
    $lifecycleCaveVa + 0x0C + 5 +
        [BitConverter]::ToInt32($lifecycleCaveCode, 0x0D) -ne
            $lifecycleContinuationVa -or
    $secondaryHookVa + 5 +
        [BitConverter]::ToInt32($patchedSecondaryHook, 1) -ne
            $secondaryCaveVa -or
    $secondaryCaveVa + 0x3C + 5 +
        [BitConverter]::ToInt32($secondaryCaveCode, 0x3D) -ne
            $secondaryNormalContinuationVa -or
    $secondaryCaveVa + 0x41 + 5 +
        [BitConverter]::ToInt32($secondaryCaveCode, 0x42) -ne
            $secondarySafeEpilogueVa) {
    throw 'Internal avatar-preview branch validation failed.'
}

if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
    throw "Origin client executable was not found: $ClientExe"
}
$resolvedClientExe = (Resolve-Path -LiteralPath $ClientExe).Path
$processName = [IO.Path]::GetFileNameWithoutExtension($resolvedClientExe)
$runningClient = @(
    Get-Process -Name $processName -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $resolvedClientExe }
)
if ($runningClient.Count -gt 0) {
    throw "$([IO.Path]::GetFileName($resolvedClientExe)) is running. Close it before changing the executable."
}

$data = [IO.File]::ReadAllBytes($resolvedClientExe)
if ($data.Length -ne $expectedLength) {
    throw "Unsupported Origin.exe size $($data.Length); expected $expectedLength bytes."
}
if (-not (Test-Bytes $data $v4PreloadHookOffset $v4PreloadOriginalHook) -or
    -not (Test-Bytes $data $v4PreloadCaveOffset $v4PreloadEmptyCave) -or
    -not (Test-Bytes $data $v4TimeoutHookOffset $v4TimeoutOriginalHook) -or
    -not (Test-Bytes $data $v4TimeoutCaveOffset $v4TimeoutEmptyCave)) {
    throw 'Origin.exe contains the additive V4 avatar preload patch. Revert it with PatchClientAvatarPreload.ps1 before changing the prerequisite preview guards.'
}
$peMetadata = Get-PeMetadata $data
if ($peMetadata.Machine -ne $expectedMachine -or
    $peMetadata.OptionalMagic -ne $expectedOptionalMagic -or
    $peMetadata.ImageBase -ne $expectedImageBase) {
    throw 'Origin.exe is not the audited x86 PE32 image-base build.'
}
$mappedRanges = @(
    [pscustomobject]@{
        Label = 'lifecycle hook'
        Mapping = Resolve-ExecutableFileRange $peMetadata $lifecycleHookOffset (
            $originalLifecycleHook.Length)
        ExpectedVa = $lifecycleHookVa
    },
    [pscustomobject]@{
        Label = 'lifecycle continuation'
        Mapping = Resolve-ExecutableFileRange $peMetadata (
            $lifecycleContinuationOffset) $lifecycleContinuation.Length
        ExpectedVa = $lifecycleContinuationVa
    },
    [pscustomobject]@{
        Label = 'lifecycle cave'
        Mapping = Resolve-ExecutableFileRange $peMetadata $lifecycleCaveOffset (
            $lifecycleCaveCode.Length)
        ExpectedVa = $lifecycleCaveVa
    },
    [pscustomobject]@{
        Label = 'secondary preview guard hook'
        Mapping = Resolve-ExecutableFileRange $peMetadata $secondaryHookOffset (
            $originalSecondaryHook.Length)
        ExpectedVa = $secondaryHookVa
    },
    [pscustomobject]@{
        Label = 'secondary preview continuation'
        Mapping = Resolve-ExecutableFileRange $peMetadata (
            $secondaryNormalContinuationOffset) $secondaryNormalContinuation.Length
        ExpectedVa = $secondaryNormalContinuationVa
    },
    [pscustomobject]@{
        Label = 'secondary preview safe epilogue'
        Mapping = Resolve-ExecutableFileRange $peMetadata (
            $secondarySafeEpilogueOffset) $secondarySafeEpilogue.Length
        ExpectedVa = $secondarySafeEpilogueVa
    },
    [pscustomobject]@{
        Label = 'secondary preview guard cave'
        Mapping = Resolve-ExecutableFileRange $peMetadata $secondaryCaveOffset (
            $secondaryCaveCode.Length)
        ExpectedVa = $secondaryCaveVa
    },
    [pscustomobject]@{
        Label = 'preview guard hook'
        Mapping = Resolve-ExecutableFileRange $peMetadata $hookOffset $originalHook.Length
        ExpectedVa = $hookVa
    },
    [pscustomobject]@{
        Label = 'preview continuation'
        Mapping = Resolve-ExecutableFileRange $peMetadata $normalContinuationOffset (
            $normalContinuation.Length)
        ExpectedVa = $normalContinuationVa
    },
    [pscustomobject]@{
        Label = 'preview safe epilogue'
        Mapping = Resolve-ExecutableFileRange $peMetadata $safeEpilogueOffset (
            $safeEpilogue.Length)
        ExpectedVa = $safeEpilogueVa
    },
    [pscustomobject]@{
        Label = 'preview guard cave'
        Mapping = Resolve-ExecutableFileRange $peMetadata $caveOffset $caveCode.Length
        ExpectedVa = $caveVa
    }
)
foreach ($mappedRange in $mappedRanges) {
    if ($mappedRange.Mapping.Va -ne $mappedRange.ExpectedVa) {
        throw "Origin.exe $($mappedRange.Label) maps to VA 0x$(
            '{0:X8}' -f $mappedRange.Mapping.Va), not the audited address."
    }
}
if (-not (Test-Bytes $data $maleFaultSiteOffset $maleFaultSite) -or
    -not (Test-Bytes $data $femaleFaultSiteOffset $femaleFaultSite) -or
    -not (Test-Bytes $data $normalContinuationOffset $normalContinuation) -or
    -not (Test-Bytes $data $safeEpilogueOffset $safeEpilogue) -or
    -not (Test-Bytes $data $secondaryFaultSiteOffset $secondaryFaultSite) -or
    -not (Test-Bytes $data $secondaryNormalContinuationOffset (
        $secondaryNormalContinuation)) -or
    -not (Test-Bytes $data $secondarySafeEpilogueOffset $secondarySafeEpilogue) -or
    -not (Test-Bytes $data $lifecycleContinuationOffset $lifecycleContinuation)) {
    throw 'Origin.exe avatar dereference prerequisites do not match the audited build.'
}

$hasOriginalLifecycleHook = Test-Bytes $data $lifecycleHookOffset $originalLifecycleHook
$hasPatchedLifecycleHook = Test-Bytes $data $lifecycleHookOffset $patchedLifecycleHook
$hasEmptyLifecycleCave = Test-Bytes $data $lifecycleCaveOffset $emptyLifecycleCave
$hasPatchedLifecycleCave = Test-Bytes $data $lifecycleCaveOffset $lifecycleCaveCode
$hasOriginalSecondaryHook = Test-Bytes $data $secondaryHookOffset $originalSecondaryHook
$hasPatchedSecondaryHook = Test-Bytes $data $secondaryHookOffset $patchedSecondaryHook
$hasEmptySecondaryCave = Test-Bytes $data $secondaryCaveOffset $emptySecondaryCave
$hasPatchedSecondaryCave = Test-Bytes $data $secondaryCaveOffset $secondaryCaveCode
$hasOriginalHook = Test-Bytes $data $hookOffset $originalHook
$hasPatchedHook = Test-Bytes $data $hookOffset $patchedHook
$hasEmptyCave = Test-Bytes $data $caveOffset $emptyCave
$hasPatchedCave = Test-Bytes $data $caveOffset $caveCode

if ($Mode -eq 'Apply' -and
    $hasPatchedLifecycleHook -and $hasPatchedLifecycleCave -and
    $hasPatchedSecondaryHook -and $hasPatchedSecondaryCave -and
    $hasPatchedHook -and $hasPatchedCave) {
    [pscustomobject]@{
        Mode = $Mode
        Status = 'Already patched'
        Path = $resolvedClientExe
        Sha256 = (Get-FileHash -LiteralPath $resolvedClientExe -Algorithm SHA256).Hash
    }
    return
}
if ($Mode -eq 'Revert' -and
    $hasOriginalLifecycleHook -and $hasEmptyLifecycleCave -and
    $hasOriginalSecondaryHook -and $hasEmptySecondaryCave -and
    $hasOriginalHook -and $hasEmptyCave) {
    [pscustomobject]@{
        Mode = $Mode
        Status = 'Already original'
        Path = $resolvedClientExe
        Sha256 = (Get-FileHash -LiteralPath $resolvedClientExe -Algorithm SHA256).Hash
    }
    return
}
if ($Mode -eq 'Apply' -and
    (-not $hasOriginalLifecycleHook -or -not $hasEmptyLifecycleCave -or
     -not $hasOriginalSecondaryHook -or -not $hasEmptySecondaryCave -or
     -not $hasOriginalHook -or -not $hasEmptyCave)) {
    throw 'Origin.exe does not match the exact unpatched avatar lifecycle/guard state.'
}
if ($Mode -eq 'Revert' -and
    (-not $hasPatchedLifecycleHook -or -not $hasPatchedLifecycleCave -or
     -not $hasPatchedSecondaryHook -or -not $hasPatchedSecondaryCave -or
     -not $hasPatchedHook -or -not $hasPatchedCave)) {
    throw 'Origin.exe does not match the exact patched avatar lifecycle/guard state.'
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path $RepositoryRoot 'backups'
}
$backupDirectory = Join-Path $BackupRoot (
    'origin-avatar-preview-guard-' + $Mode + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff')
)
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backupPath = Join-Path $backupDirectory 'Origin.exe'
Copy-Item -LiteralPath $resolvedClientExe -Destination $backupPath
$beforeHash = (Get-FileHash -LiteralPath $resolvedClientExe -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne $beforeHash) {
    throw "Avatar patch backup verification failed: $backupPath"
}

$before = [byte[]]$data.Clone()
$allowedRanges = @(
    [pscustomobject]@{ Offset = $lifecycleHookOffset; Length = $originalLifecycleHook.Length },
    [pscustomobject]@{ Offset = $lifecycleCaveOffset; Length = $lifecycleCaveCode.Length },
    [pscustomobject]@{ Offset = $secondaryHookOffset; Length = $originalSecondaryHook.Length },
    [pscustomobject]@{ Offset = $secondaryCaveOffset; Length = $secondaryCaveCode.Length },
    [pscustomobject]@{ Offset = $hookOffset; Length = $originalHook.Length },
    [pscustomobject]@{ Offset = $caveOffset; Length = $caveCode.Length }
)
$expectedMutationCount =
    (Measure-ByteDifference $originalLifecycleHook $patchedLifecycleHook) +
    (Measure-ByteDifference $emptyLifecycleCave $lifecycleCaveCode) +
    (Measure-ByteDifference $originalSecondaryHook $patchedSecondaryHook) +
    (Measure-ByteDifference $emptySecondaryCave $secondaryCaveCode) +
    (Measure-ByteDifference $originalHook $patchedHook) +
    (Measure-ByteDifference $emptyCave $caveCode)
if ($expectedMutationCount -ne 169) {
    throw 'Internal avatar-preview mutation-count validation failed.'
}
if ($Mode -eq 'Apply') {
    Copy-Bytes $patchedLifecycleHook $data $lifecycleHookOffset
    Copy-Bytes $lifecycleCaveCode $data $lifecycleCaveOffset
    Copy-Bytes $patchedSecondaryHook $data $secondaryHookOffset
    Copy-Bytes $secondaryCaveCode $data $secondaryCaveOffset
    Copy-Bytes $patchedHook $data $hookOffset
    Copy-Bytes $caveCode $data $caveOffset
}
else {
    Copy-Bytes $originalLifecycleHook $data $lifecycleHookOffset
    Copy-Bytes $emptyLifecycleCave $data $lifecycleCaveOffset
    Copy-Bytes $originalSecondaryHook $data $secondaryHookOffset
    Copy-Bytes $emptySecondaryCave $data $secondaryCaveOffset
    Copy-Bytes $originalHook $data $hookOffset
    Copy-Bytes $emptyCave $data $caveOffset
}

$inMemoryMutationCount = 0
for ($offset = 0; $offset -lt $data.Length; $offset++) {
    if ($before[$offset] -eq $data[$offset]) { continue }
    $inMemoryMutationCount++
    if (-not (Test-AllowedDifference $offset $allowedRanges)) {
        throw "Unexpected avatar patch mutation at file offset 0x$('{0:X}' -f $offset)."
    }
}
if ($inMemoryMutationCount -ne $expectedMutationCount) {
    throw "Avatar patch would change $inMemoryMutationCount bytes; expected $expectedMutationCount."
}

[IO.File]::WriteAllBytes($resolvedClientExe, $data)
$written = [IO.File]::ReadAllBytes($resolvedClientExe)
$expectedLifecycleHook = if ($Mode -eq 'Apply') {
    $patchedLifecycleHook
} else {
    $originalLifecycleHook
}
$expectedLifecycleCave = if ($Mode -eq 'Apply') {
    $lifecycleCaveCode
} else {
    $emptyLifecycleCave
}
$expectedSecondaryHook = if ($Mode -eq 'Apply') {
    $patchedSecondaryHook
} else {
    $originalSecondaryHook
}
$expectedSecondaryCave = if ($Mode -eq 'Apply') {
    $secondaryCaveCode
} else {
    $emptySecondaryCave
}
$expectedHook = if ($Mode -eq 'Apply') { $patchedHook } else { $originalHook }
$expectedCave = if ($Mode -eq 'Apply') { $caveCode } else { $emptyCave }
if ($written.Length -ne $expectedLength -or
    -not (Test-Bytes $written $lifecycleHookOffset $expectedLifecycleHook) -or
    -not (Test-Bytes $written $lifecycleCaveOffset $expectedLifecycleCave) -or
    -not (Test-Bytes $written $secondaryHookOffset $expectedSecondaryHook) -or
    -not (Test-Bytes $written $secondaryCaveOffset $expectedSecondaryCave) -or
    -not (Test-Bytes $written $hookOffset $expectedHook) -or
    -not (Test-Bytes $written $caveOffset $expectedCave) -or
    -not (Test-Bytes $written $maleFaultSiteOffset $maleFaultSite) -or
    -not (Test-Bytes $written $femaleFaultSiteOffset $femaleFaultSite)) {
    throw "Origin.exe avatar patch verification failed. Backup: $backupPath"
}

$writtenMutationCount = 0
for ($offset = 0; $offset -lt $written.Length; $offset++) {
    if ($before[$offset] -eq $written[$offset]) { continue }
    $writtenMutationCount++
    if (-not (Test-AllowedDifference $offset $allowedRanges)) {
        throw "Unexpected written mutation at file offset 0x$('{0:X}' -f $offset). Backup: $backupPath"
    }
}
if ($writtenMutationCount -ne $expectedMutationCount) {
    throw "Written Origin.exe changed $writtenMutationCount bytes; expected $expectedMutationCount. Backup: $backupPath"
}

[pscustomobject]@{
    Mode = $Mode
    Status = if ($Mode -eq 'Apply') { 'Patched' } else { 'Reverted' }
    Path = $resolvedClientExe
    LifecycleHookFileOffset = ('0x{0:X}' -f $lifecycleHookOffset)
    LifecycleHookVa = ('0x{0:X8}' -f $lifecycleHookVa)
    LifecycleCaveFileOffset = ('0x{0:X}' -f $lifecycleCaveOffset)
    LifecycleCaveVa = ('0x{0:X8}' -f $lifecycleCaveVa)
    SecondaryHookFileOffset = ('0x{0:X}' -f $secondaryHookOffset)
    SecondaryHookVa = ('0x{0:X8}' -f $secondaryHookVa)
    SecondaryCaveFileOffset = ('0x{0:X}' -f $secondaryCaveOffset)
    SecondaryCaveVa = ('0x{0:X8}' -f $secondaryCaveVa)
    HookFileOffset = ('0x{0:X}' -f $hookOffset)
    HookVa = ('0x{0:X8}' -f $hookVa)
    CaveFileOffset = ('0x{0:X}' -f $caveOffset)
    CaveVa = ('0x{0:X8}' -f $caveVa)
    GuardedObjects = 6
    GuardedBuilders = 2
    ChangedBytes = $writtenMutationCount
    Backup = $backupPath
    BeforeSha256 = $beforeHash
    AfterSha256 = (Get-FileHash -LiteralPath $resolvedClientExe -Algorithm SHA256).Hash
}
}
