function Invoke-GearEnhancementTabPatch {
    param(
        [string]$ClientRoot,
        [string]$Mode,
        [string]$BackupRoot
    )

$originPath = Join-Path $ClientRoot 'Origin.exe'
$locales = @('en_us', 'zh_cn')
$xmlPaths = @{}
$textPaths = @{}
foreach ($locale in $locales) {
    $xmlPaths[$locale] = Join-Path $ClientRoot (
        "Localization\$locale\UI\XML\EquipForgeExUI.xml")
    $textPaths[$locale] = Join-Path $ClientRoot (
        "Localization\$locale\UI\Base\text.lua")
}

$allPaths = @($originPath) + @($xmlPaths.Values) + @($textPaths.Values)
foreach ($path in $allPaths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required client file is missing: $path"
    }
}

[byte[]]$origin = [IO.File]::ReadAllBytes($originPath)
$pe = Get-PeMetadata $origin
if ($origin.Length -ne 6676480 -or $pe.Machine -ne 0x014C -or
    $pe.OptionalMagic -ne 0x010B -or $pe.ImageBase -ne 0x00400000) {
    throw 'Origin.exe is not the supported 32-bit 6,676,480-byte client build.'
}

# VA 0x0055F3CD is the selected-forge-tab dispatch JNE. The replacement
# always routes through an isolated cave. Index 3 returns to the untouched
# replacement handler, indices 0-2 return to ordinary forging, and index 4
# closes the forge with its established cross-modal reset/hide sequence before
# sending the native fixed-size 48-byte NpcDialogOpen request. The request is
# 30 00 53 27 FF FF FF FF followed by 40 zero bytes. The server
# resolves NPC ID -1 to the character's faction-correct dialog-118 endpoint.
$hookOffset = 0x15F3CD
$hookVa = 0x0055F3CD
$caveOffset = 0x5C3380
$caveVa = 0x009C3380
[byte[]]$originalHook = Convert-HexBytes '0F 85 43 01 00 00'
[byte[]]$patchedHook = Convert-HexBytes 'E9 AE 3F 46 00 90'
[byte[]]$legacyCaveCode = Convert-HexBytes @'
83 F8 03 0F 84 4A C0 B9 FF 83 F8 04 0F 85 84 C1 B9 FF
9C 60 8B C6 E8 55 D7 B9 FF E8 C0 9F B9 FF 31 DB E8 C9
A7 B9 FF 83 EC 08 C7 04 24 08 00 53 27 C7 44 24 04 FF
FF FF FF 8B 0D 50 61 57 01 8B 11 8B 52 1C 6A 08 8D 44
24 04 50 FF D2 83 C4 08 61 9D E9 CF C3 B9 FF
'@
[byte[]]$sendFirstCaveCode = Convert-HexBytes @'
83 F8 03 0F 84 4A C0 B9 FF 83 F8 04 0F 85 84 C1 B9 FF
9C 60 83 EC 30 31 C0 89 E7 B9 0C 00 00 00 FC F3 AB C7
04 24 30 00 53 27 C7 44 24 04 FF FF FF FF 8B 0D 50 61
57 01 8B 11 8B 52 1C 6A 30 8D 44 24 04 50 FF D2 83 C4
30 8B C6 E8 20 D7 B9 FF E8 8B 9F B9 FF 31 DB E8 94 A7
B9 FF 61 9D E9 C3 C3 B9 FF
'@
[byte[]]$caveCode = Convert-HexBytes @'
83 F8 03 0F 84 4A C0 B9 FF 83 F8 04 0F 85 84 C1 B9 FF
9C 60 8B C6 E8 55 D7 B9 FF E8 C0 9F B9 FF 31 DB E8 C9
A7 B9 FF 83 EC 30 31 C0 89 E7 B9 0C 00 00 00 FC F3 AB
C7 04 24 30 00 53 27 C7 44 24 04 FF FF FF FF 8B 0D 50
61 57 01 8B 11 8B 52 1C 6A 30 8D 44 24 04 50 FF D2 83
C4 30 61 9D E9 C3 C3 B9 FF
'@
[byte[]]$emptyCave = [byte[]]::new($caveCode.Length)
[byte[]]$legacyPaddedCave = [byte[]]::new($caveCode.Length)
Copy-Bytes $legacyCaveCode $legacyPaddedCave 0

$hookMapping = Resolve-ExecutableFileRange $pe $hookOffset $patchedHook.Length
$caveMapping = Resolve-ExecutableFileRange $pe $caveOffset $caveCode.Length
if ($hookMapping.Va -ne $hookVa -or $caveMapping.Va -ne $caveVa -or
    $hookMapping.Section -ne '.text' -or $caveMapping.Section -ne '.rdata') {
    throw 'Origin.exe hook/cave PE mappings do not match the supported build.'
}
if ($legacyCaveCode.Length -ne 87 -or
    $sendFirstCaveCode.Length -ne 99 -or $caveCode.Length -ne 99 -or
    (Get-RelativeTarget $patchedHook 1 ($hookVa + 5)) -ne $caveVa -or
    (Get-RelativeTarget $caveCode 5 ($caveVa + 9)) -ne 0x0055F3D3 -or
    (Get-RelativeTarget $caveCode 14 ($caveVa + 18)) -ne 0x0055F516 -or
    (Get-RelativeTarget $caveCode 23 ($caveVa + 27)) -ne 0x00560AF0 -or
    (Get-RelativeTarget $caveCode 28 ($caveVa + 32)) -ne 0x0055D360 -or
    (Get-RelativeTarget $caveCode 35 ($caveVa + 39)) -ne 0x0055DB70 -or
    -not (Test-Bytes $caveCode 39 (Convert-HexBytes (
        '83 EC 30 31 C0 89 E7 B9 0C 00 00 00 FC F3 AB'))) -or
    -not (Test-Bytes $caveCode 54 (Convert-HexBytes 'C7 04 24 30 00 53 27')) -or
    -not (Test-Bytes $caveCode 61 (Convert-HexBytes 'C7 44 24 04 FF FF FF FF')) -or
    -not (Test-Bytes $caveCode 80 (Convert-HexBytes '6A 30 8D 44 24 04 50 FF D2')) -or
    (Get-RelativeTarget $caveCode 95 ($caveVa + 99)) -ne 0x0055F7A6) {
    throw 'Internal GWGE1 branch encoding verification failed.'
}

$hasOriginalHook = Test-Bytes $origin $hookOffset $originalHook
$hasPatchedHook = Test-Bytes $origin $hookOffset $patchedHook
$hasEmptyCave = Test-Bytes $origin $caveOffset $emptyCave
$hasPatchedCave = Test-Bytes $origin $caveOffset $caveCode
$hasSendFirstCave = Test-Bytes $origin $caveOffset $sendFirstCaveCode
$hasLegacyCave = Test-Bytes $origin $caveOffset $legacyPaddedCave
$nativeState = if ($hasOriginalHook -and $hasEmptyCave) {
    'Original'
}
elseif ($hasPatchedHook -and $hasPatchedCave) {
    'Patched'
}
elseif ($hasPatchedHook -and $hasSendFirstCave) {
    'PatchedSendFirst'
}
elseif ($hasPatchedHook -and $hasLegacyCave) {
    'PatchedLegacy'
}
else {
    throw 'Origin.exe has a partial/conflicting forge-tab hook or occupied GWGE1 cave.'
}

$xmlDocuments = @{}
$xmlStates = @{}
$textDocuments = @{}
$textStates = @{}
foreach ($locale in $locales) {
    $xmlDocuments[$locale] = Read-Utf8File $xmlPaths[$locale]
    $xmlStates[$locale] = Get-XmlPatchState $xmlDocuments[$locale].Text (
        "$locale EquipForgeExUI.xml")
    $textDocuments[$locale] = Read-Utf8File $textPaths[$locale]
    $textStates[$locale] = Get-TextPatchState $textDocuments[$locale].Text $locale (
        "$locale text.lua")
}

if ($Mode -eq 'Verify') {
    [pscustomobject]@{
        Mode = $Mode
        NativeState = $nativeState
        EnUsXmlState = $xmlStates['en_us']
        ZhCnXmlState = $xmlStates['zh_cn']
        EnUsTextState = $textStates['en_us']
        ZhCnTextState = $textStates['zh_cn']
        HookVa = ('0x{0:X8}' -f $hookVa)
        CaveVa = ('0x{0:X8}' -f $caveVa)
        CaveBytes = $caveCode.Length
        LauncherPacket = '30005327FFFFFFFF + 40 zero bytes'
        ServerDialog = 118
    }
    return
}

$desiredPatched = $Mode -eq 'Apply'
$alreadyDesired = if ($desiredPatched) {
    $nativeState -eq 'Patched' -and
        @($locales | Where-Object {
            $xmlStates[$_] -ne 'Patched' -or $textStates[$_] -ne 'Patched'
        }).Count -eq 0
}
else {
    $nativeState -eq 'Original' -and
        @($locales | Where-Object {
            $xmlStates[$_] -ne 'Original' -or $textStates[$_] -ne 'Original'
        }).Count -eq 0
}
if ($alreadyDesired) {
    [pscustomobject]@{ Mode = $Mode; State = 'AlreadyDesired'; ClientRoot = $ClientRoot }
    return
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path (Split-Path -Parent $ClientRoot) 'backups'
}
$clientRootFull = [IO.Path]::GetFullPath($ClientRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($clientRootFull -eq [IO.Path]::GetPathRoot($clientRootFull)) {
    throw 'ClientRoot cannot be a filesystem root.'
}
$clientRootPrefix = $clientRootFull + [IO.Path]::DirectorySeparatorChar
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
$backupDirectory = Join-Path $BackupRoot ("client-gear-enhancement-tab-$Mode-$timestamp")
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
foreach ($path in $allPaths) {
    $pathFull = [IO.Path]::GetFullPath($path)
    if (-not $pathFull.StartsWith($clientRootPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to back up a path outside ClientRoot: $pathFull"
    }
    $relativePath = $pathFull.Substring($clientRootPrefix.Length)
    $backupPath = Join-Path $backupDirectory $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force |
        Out-Null
    Copy-Item -LiteralPath $path -Destination $backupPath
}

if ($desiredPatched -and $nativeState -ne 'Patched') {
    Copy-Bytes $patchedHook $origin $hookOffset
    Copy-Bytes $caveCode $origin $caveOffset
}
elseif (-not $desiredPatched -and $nativeState -ne 'Original') {
    Copy-Bytes $originalHook $origin $hookOffset
    Copy-Bytes $emptyCave $origin $caveOffset
}

$updatedXml = @{}
$updatedText = @{}
foreach ($locale in $locales) {
    $updatedXml[$locale] = if (($desiredPatched -and $xmlStates[$locale] -ne 'Patched') -or
        (-not $desiredPatched -and $xmlStates[$locale] -ne 'Original')) {
        Set-XmlPatch $xmlDocuments[$locale].Text $desiredPatched (
            "$locale EquipForgeExUI.xml")
    }
    else { $xmlDocuments[$locale].Text }

    $updatedText[$locale] = if (($desiredPatched -and $textStates[$locale] -ne 'Patched') -or
        (-not $desiredPatched -and $textStates[$locale] -ne 'Original')) {
        Set-TextPatch $textDocuments[$locale].Text $desiredPatched $locale (
            "$locale text.lua")
    }
    else { $textDocuments[$locale].Text }

    $xmlCheck = [Xml.XmlDocument]::new()
    $xmlCheck.PreserveWhitespace = $true
    $xmlCheck.LoadXml($updatedXml[$locale])
}

Write-AtomicBytes $originPath $origin
foreach ($locale in $locales) {
    Write-AtomicBytes $xmlPaths[$locale] (Convert-ToUtf8Bytes $updatedXml[$locale] (
        $xmlDocuments[$locale].HasBom))
    Write-AtomicBytes $textPaths[$locale] (Convert-ToUtf8Bytes $updatedText[$locale] (
        $textDocuments[$locale].HasBom))
}

[byte[]]$writtenOrigin = [IO.File]::ReadAllBytes($originPath)
$expectedHook = if ($desiredPatched) { $patchedHook } else { $originalHook }
$expectedCave = if ($desiredPatched) { $caveCode } else { $emptyCave }
if (-not (Test-Bytes $writtenOrigin $hookOffset $expectedHook) -or
    -not (Test-Bytes $writtenOrigin $caveOffset $expectedCave)) {
    throw 'Origin.exe post-write verification failed.'
}
foreach ($locale in $locales) {
    $xmlReadback = Read-Utf8File $xmlPaths[$locale]
    $textReadback = Read-Utf8File $textPaths[$locale]
    $expectedState = if ($desiredPatched) { 'Patched' } else { 'Original' }
    if ((Get-XmlPatchState $xmlReadback.Text "$locale XML readback") -ne $expectedState -or
        (Get-TextPatchState $textReadback.Text $locale "$locale text readback") -ne
            $expectedState) {
        throw "$locale localization post-write verification failed."
    }
}

[pscustomobject]@{
    Mode = $Mode
    State = if ($desiredPatched) { 'Patched' } else { 'Original' }
    ClientRoot = $ClientRoot
    BackupDirectory = $backupDirectory
    OriginSha256 = (Get-FileHash -LiteralPath $originPath -Algorithm SHA256).Hash
    HookVa = ('0x{0:X8}' -f $hookVa)
    CaveVa = ('0x{0:X8}' -f $caveVa)
    LauncherPacket = '30005327FFFFFFFF + 40 zero bytes'
    ServerDialog = 118
}
}
