function Invoke-StandaloneGearEnhancementPatch {
    param(
        [string]$ClientRoot,
        [string]$Mode,
        [string]$BackupRoot,
        [string]$RepositoryRoot
    )

$systemBarXmlPaths = @{}
$npcFunXmlPaths = @{}
foreach ($locale in $locales) {
    $systemBarXmlPaths[$locale] = Join-Path $ClientRoot (
        "Localization\$locale\UI\XML\SystemBar.xml")
    $npcFunXmlPaths[$locale] = Join-Path $ClientRoot (
        "Localization\$locale\UI\XML\NpcFun.xml")
}
$systemBarLuaPath = Join-Path $ClientRoot (
    'Localization\en_us\UI\XML\SystemBar.lua')
$enhancerLuaPath = Join-Path $ClientRoot (
    'Localization\en_us\UI\XML\NpcFun\NpcFunEnhancer.lua')
$forgeSourcePath = Join-Path $ClientRoot (
    'Localization\en_us\UI\XML\EquipForgeExUI.xml')
$allPaths = @($systemBarXmlPaths.Values) + @($npcFunXmlPaths.Values) +
    @($systemBarLuaPath, $enhancerLuaPath)

foreach ($path in @($allPaths) + @($forgeSourcePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required client file is missing: $path"
    }
}
Assert-ForgeSource $forgeSourcePath

$documents = @{}
$desired = @{}
$desiredApply = $Mode -ne 'Revert'
foreach ($path in $allPaths) {
    $documents[$path] = Read-Utf8File $path
    if ($systemBarXmlPaths.Values -contains $path) {
        $desired[$path] = Set-SystemBarXml (
            $documents[$path].Text) $desiredApply $path
    }
    elseif ($npcFunXmlPaths.Values -contains $path) {
        $desired[$path] = Set-NpcFunXml (
            $documents[$path].Text) $desiredApply $path
    }
    elseif ($path -eq $systemBarLuaPath) {
        $desired[$path] = Set-SystemBarLua (
            $documents[$path].Text) $desiredApply $path
    }
    else {
        $desired[$path] = Set-EnhancerLua (
            $documents[$path].Text) $desiredApply $path
    }

    if (($systemBarXmlPaths.Values -contains $path) -or
        ($npcFunXmlPaths.Values -contains $path)) {
        $xmlCheck = [Xml.XmlDocument]::new()
        $xmlCheck.LoadXml($desired[$path])
    }
}

if ($desiredApply) {
    foreach ($path in $npcFunXmlPaths.Values) {
        Assert-PatchedNpcFun $desired[$path] $path
    }
}

$changeCount = @($allPaths | Where-Object {
    $desired[$_] -cne $documents[$_].Text
}).Count

if ($Mode -eq 'Verify') {
    $hasCurrentMarker = @($allPaths | Where-Object {
        $documents[$_].Text.Contains($marker)
    }).Count -gt 0
    $hasLegacyMarker = @($allPaths | Where-Object {
        $documents[$_].Text.Contains($legacyMarker)
    }).Count -gt 0
    [pscustomobject]@{
        Mode = $Mode
        ClientRoot = $ClientRoot
        State = if ($changeCount -eq 0) { 'Patched' }
            elseif ($hasLegacyMarker) { 'UpgradeRequired' }
            elseif (-not $hasCurrentMarker) { 'Original' }
            else { 'Mixed' }
        OriginExeChanged = $false
        Window = 'Exact 350x582 EquipForgeExUI shell on native FirstWin'
        NativeTabs = '800001..800003 (Add, Enhance, Delete)'
        NativeSlots = '800031 Gear, 800033 Attribute Stone, 800032 Catalyst'
        FilesNeedingChange = @($allPaths | Where-Object {
            $desired[$_] -cne $documents[$_].Text
        })
    }
    return
}

if ($changeCount -eq 0) {
    [pscustomobject]@{
        Mode = $Mode
        State = 'AlreadyDesired'
        ClientRoot = $ClientRoot
    }
    return
}

if (Get-Process -Name 'Origin' -ErrorAction SilentlyContinue) {
    throw 'Close Origin.exe before applying or reverting the Gear Enhancement Forge clone.'
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'backups'))
}
$clientRootFull = [IO.Path]::GetFullPath($ClientRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
if ($clientRootFull -eq [IO.Path]::GetPathRoot($clientRootFull)) {
    throw 'ClientRoot cannot be a filesystem root.'
}
$clientRootPrefix = $clientRootFull + [IO.Path]::DirectorySeparatorChar
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
$backupDirectory = Join-Path $BackupRoot (
    "client-standalone-gear-enhancement-$Mode-$timestamp")
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null

foreach ($path in $allPaths) {
    $pathFull = [IO.Path]::GetFullPath($path)
    if (-not $pathFull.StartsWith(
            $clientRootPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to back up a path outside ClientRoot: $pathFull"
    }
    $relativePath = $pathFull.Substring($clientRootPrefix.Length)
    $backupPath = Join-Path $backupDirectory $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force |
        Out-Null
    Copy-Item -LiteralPath $path -Destination $backupPath
}

foreach ($path in $allPaths) {
    Write-AtomicBytes $path (Convert-ToUtf8Bytes (
        $desired[$path]) $documents[$path].HasBom)
}

foreach ($path in $allPaths) {
    $readback = Read-Utf8File $path
    if ($readback.Text -cne $desired[$path]) {
        throw "Post-write verification failed: $path"
    }
    if (($systemBarXmlPaths.Values -contains $path) -or
        ($npcFunXmlPaths.Values -contains $path)) {
        $xmlCheck = [Xml.XmlDocument]::new()
        $xmlCheck.LoadXml($readback.Text)
    }
}

[pscustomobject]@{
    Mode = $Mode
    State = if ($desiredApply) { 'Patched' } else { 'Original' }
    ClientRoot = $ClientRoot
    BackupDirectory = $backupDirectory
    OriginExeChanged = $false
    Window = if ($desiredApply) {
        'Exact 350x582 EquipForgeExUI shell on native FirstWin'
    } else {
        'Shipped T_SimpleWindow FirstWin dialog'
    }
    MenuOrder = if ($desiredApply) {
        'Add, Enhance, Delete'
    } else {
        'Shipped NpcFunEnhancer.lua behavior'
    }
    SlotOrder = if ($desiredApply) {
        'Gear, Attribute Stone, Catalyst'
    } else {
        'Shipped NpcFunEnhancer.lua behavior'
    }
}
}
