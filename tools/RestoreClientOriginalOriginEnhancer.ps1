param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientStandaloneGearEnhancement.ps1'
if (-not (Test-Path -LiteralPath $patcher -PathType Leaf)) {
    throw "Required rollback patcher is missing: $patcher"
}

$salaryFixPath = Join-Path $ClientRoot (
    'Localization\en_us\Text\NPCDescription.dat')
$originExePath = Join-Path $ClientRoot 'Origin.exe'
foreach ($protectedPath in @($salaryFixPath, $originExePath)) {
    if (-not (Test-Path -LiteralPath $protectedPath -PathType Leaf)) {
        throw "Protected client file is missing: $protectedPath"
    }
}

$protectedHashes = @{}
foreach ($protectedPath in @($salaryFixPath, $originExePath)) {
    $protectedHashes[$protectedPath] = (Get-FileHash -LiteralPath (
        $protectedPath) -Algorithm SHA256).Hash
}

$arguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $patcher,
    '-ClientRoot', $ClientRoot,
    '-Mode', 'Revert'
)
if (-not [string]::IsNullOrWhiteSpace($BackupRoot)) {
    $arguments += @('-BackupRoot', $BackupRoot)
}

$rollbackOutput = & powershell @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Origin Enhancer rollback failed with exit code $LASTEXITCODE."
}

$relativeUiPaths = @(
    'Localization\en_us\UI\XML\SystemBar.xml',
    'Localization\zh_cn\UI\XML\SystemBar.xml',
    'Localization\en_us\UI\XML\NpcFun.xml',
    'Localization\zh_cn\UI\XML\NpcFun.xml',
    'Localization\en_us\UI\XML\SystemBar.lua',
    'Localization\en_us\UI\XML\NpcFun\NpcFunEnhancer.lua'
)

$utf8 = [Text.UTF8Encoding]::new($false, $true)
foreach ($relativePath in $relativeUiPaths) {
    $path = Join-Path $ClientRoot $relativePath
    [byte[]]$bytes = [IO.File]::ReadAllBytes($path)
    $offset = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
    $text = $utf8.GetString($bytes, $offset, $bytes.Length - $offset)
    foreach ($forbidden in @(
            'Standalone Gear Enhancement (GWGE2)',
            'Standalone Gear Enhancement Forge Clone (GWGE3)',
            'GearEnhancementBtn_OnClick')) {
        if ($text.Contains($forbidden)) {
            throw "Custom Origin Enhancer marker remains in ${path}: $forbidden"
        }
    }

    if ($path.EndsWith('.xml', [StringComparison]::OrdinalIgnoreCase)) {
        $document = [Xml.XmlDocument]::new()
        $document.LoadXml($text)
    }
}

foreach ($locale in @('en_us', 'zh_cn')) {
    $npcFunPath = Join-Path $ClientRoot (
        "Localization\$locale\UI\XML\NpcFun.xml")
    $document = [Xml.XmlDocument]::new()
    $document.Load($npcFunPath)
    $firstWin = $document.SelectSingleNode('/UIConfig/FirstWin')
    if ($null -eq $firstWin -or
        $firstWin.GetAttribute('Template') -ne 'T_SimpleWindow' -or
        $firstWin.GetAttribute('Rectangle') -ne '380,112,980,402') {
        throw "$npcFunPath does not contain the shipped FirstWin dialog shell."
    }
}

foreach ($protectedPath in @($salaryFixPath, $originExePath)) {
    $readbackHash = (Get-FileHash -LiteralPath $protectedPath -Algorithm (
        'SHA256')).Hash
    if ($readbackHash -cne $protectedHashes[$protectedPath]) {
        throw "Rollback unexpectedly changed protected file: $protectedPath"
    }
}

$rollbackOutput
[pscustomobject]@{
    State = 'OriginalOriginEnhancer'
    ShortcutE = 'Removed'
    CustomDialogWrapper = 'Removed'
    GearMentorLocalizationPreserved = $true
    OriginExeChanged = $false
    VerifiedFiles = $relativeUiPaths
}
