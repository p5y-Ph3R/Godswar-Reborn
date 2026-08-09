[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$RepositoryRoot,
    [switch]$CheckOnly,
    [string]$BackupDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$menuText = '|cffFFFF00*Mount Gear Drilling|cffffffff'
$requirementsText =
    '|cffFFFF00Mount Gear Drilling|cffffffff\n' +
    'Open up to two Zephyr Spirit sockets on unequipped mount gear.\n' +
    '1st socket: 230 Gold.\n' +
    '2nd socket: 2,300 Gold.\n' +
    'Place eligible mount gear in the box below.'
$eligibilityText =
    '|cffF14187Only mount headgear, armor, soul, ornament, and amulet ' +
    'equipment are eligible. Mounts and character equipment are not ' +
    'accepted.|cffffffff'
$targetNotMountGearText =
    '|cffF14187The item in the first box is not mount gear. Place unequipped ' +
    'mount headgear, armor, soul, ornament, or amulet equipment.|cffffffff'

function Read-EncodedText([string]$Path) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    $encoding = $null
    [byte[]]$preamble = @()
    $offset = 0

    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        [byte[]]$preamble = 0xEF, 0xBB, 0xBF
        $offset = 3
    }
    elseif ($bytes.Length -ge 2 -and
        $bytes[0] -eq 0xFF -and
        $bytes[1] -eq 0xFE) {
        $encoding = [Text.UnicodeEncoding]::new($false, $false, $true)
        [byte[]]$preamble = 0xFF, 0xFE
        $offset = 2
    }
    elseif ($bytes.Length -ge 2 -and
        $bytes[0] -eq 0xFE -and
        $bytes[1] -eq 0xFF) {
        $encoding = [Text.UnicodeEncoding]::new($true, $false, $true)
        [byte[]]$preamble = 0xFE, 0xFF
        $offset = 2
    }
    else {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
    }

    try {
        $text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
    }
    catch [Text.DecoderFallbackException] {
        throw "Unsupported or corrupt text encoding in $Path."
    }

    return [pscustomobject]@{
        Path = $Path
        Text = $text
        Encoding = $encoding
        Preamble = $preamble
    }
}

function ConvertTo-EncodedBytes([object]$Document, [string]$Text) {
    [byte[]]$body = $Document.Encoding.GetBytes($Text)
    [byte[]]$result = [byte[]]::new($Document.Preamble.Length + $body.Length)
    if ($Document.Preamble.Length -gt 0) {
        [Array]::Copy(
            $Document.Preamble,
            0,
            $result,
            0,
            $Document.Preamble.Length)
    }
    [Array]::Copy(
        $body,
        0,
        $result,
        $Document.Preamble.Length,
        $body.Length)
    return $result
}

function Test-ByteArraysEqual([byte[]]$Left, [byte[]]$Right) {
    if ($Left.Length -ne $Right.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) {
            return $false
        }
    }
    return $true
}

function Get-NewLine([string]$Text) {
    $crlfCount = [Regex]::Matches($Text, "`r`n").Count
    $bareLfCount = [Regex]::Matches($Text, "(?<!`r)`n").Count
    if ($crlfCount -ge $bareLfCount -and $crlfCount -gt 0) {
        return "`r`n"
    }
    return "`n"
}

function Set-LuaAssignment(
    [string]$Text,
    [string]$Key,
    [string]$Value,
    [string]$AnchorKey
) {
    if ($Value.Contains('"') -or
        $Value.Contains("`r") -or
        $Value.Contains("`n")) {
        throw "The requested value for $Key is not a single Lua string literal."
    }

    $pattern =
        "(?m)^(?<indent>[ `t]*)$([Regex]::Escape($Key))[ `t]*=[ `t]*" +
        '"[^"\r\n]*"(?<cr>\r?)$'
    $matches = [Regex]::Matches($Text, $pattern)
    if ($matches.Count -gt 1) {
        throw "Duplicate Lua localization key $Key."
    }

    $expectedLine = "$Key = `"$Value`""
    if ($matches.Count -eq 1) {
        $match = $matches[0]
        $replacement =
            $match.Groups['indent'].Value +
            $expectedLine +
            $match.Groups['cr'].Value
        return $Text.Remove($match.Index, $match.Length).Insert(
            $match.Index,
            $replacement)
    }

    $anchorPattern =
        "(?m)^[ `t]*$([Regex]::Escape($AnchorKey))[ `t]*=[ `t]*" +
        '"[^"\r\n]*"\r?$'
    $anchors = [Regex]::Matches($Text, $anchorPattern)
    if ($anchors.Count -ne 1) {
        throw "Expected one Lua localization anchor $AnchorKey; found $($anchors.Count)."
    }

    $newLine = Get-NewLine $Text
    $insertAt = $anchors[0].Index + $anchors[0].Length
    if ($insertAt -lt $Text.Length -and $Text[$insertAt] -eq "`n") {
        $insertAt++
    }
    elseif ($insertAt -eq $Text.Length) {
        return $Text + $newLine + $expectedLine
    }
    return $Text.Insert($insertAt, $expectedLine + $newLine)
}

function Get-UniqueRegion(
    [string]$Text,
    [string]$StartPattern,
    [string]$EndPattern,
    [string]$Description
) {
    $starts = [Regex]::Matches($Text, $StartPattern)
    if ($starts.Count -ne 1) {
        throw "Expected one $Description start; found $($starts.Count)."
    }

    $tailStart = $starts[0].Index + $starts[0].Length
    $ends = [Regex]::Matches($Text.Substring($tailStart), $EndPattern)
    if ($ends.Count -lt 1) {
        throw "Could not locate the end of $Description."
    }

    return [pscustomobject]@{
        Start = $starts[0].Index
        Length = $starts[0].Length + $ends[0].Index
    }
}

function Add-MountGearDrillMenuBranch([string]$Text) {
    $region = Get-UniqueRegion -Text $Text `
        -StartPattern '(?m)^[ \t]*elseif[ \t]+math\.mod\(SubID[ \t]*,[ \t]*100\)[ \t]*==[ \t]*1[ \t]+then\r?$' `
        -EndPattern '(?m)^[ \t]*elseif[ \t]+math\.mod\(SubID[ \t]*,[ \t]*100\)[ \t]*==[ \t]*2[ \t]+then\r?$' `
        -Description 'Holy Stone menu branch'
    $block = $Text.Substring($region.Start, $region.Length)
    $mountHeader =
        '(?m)^[ \t]*elseif[ \t]+\([ \t]*SubID[ \t]*-[ \t]*1[ \t]*\)' +
        '[ \t]*/[ \t]*100[ \t]*==[ \t]*8[ \t]+then\r?$'
    $mountBranches = [Regex]::Matches($block, $mountHeader)
    if ($mountBranches.Count -gt 1) {
        throw 'Duplicate Mount Gear Drilling menu branches in NpcFunEment.lua.'
    }
    if ($mountBranches.Count -eq 1) {
        $followingLength = [Math]::Min(
            350,
            $block.Length - $mountBranches[0].Index)
        $following = $block.Substring(
            $mountBranches[0].Index,
            $followingLength)
        if ($following -notmatch
                'Button:SetText\(NF_L0_ZBXQ801\);' -or
            $following -notmatch 'Button:Visible\(true\);') {
            throw 'The existing Mount Gear Drilling menu branch has an unknown layout.'
        }

        $position = [Regex]::Match(
            $following,
            'Button:SetPosition\([^\r\n]*\);')
        if (!$position.Success) {
            throw 'The existing Mount Gear Drilling menu branch has no button position.'
        }
        if ($position.Value -match
                '^Button:SetPosition\(320[ \t]*,[ \t]*110\);$') {
            return $Text
        }
        if ($position.Value -notmatch
                '^Button:SetPosition\((?:320[ \t]*,[ \t]*(?:155|160)|25[ \t]*,[ \t]*(?:85|285))\);$') {
            throw 'The existing Mount Gear Drilling menu branch has an unknown button position.'
        }

        $absolutePosition =
            $region.Start + $mountBranches[0].Index + $position.Index
        return $Text.Remove(
            $absolutePosition,
            $position.Length).Insert(
                $absolutePosition,
                'Button:SetPosition(320,110);')
    }

    $standardHeader =
        '(?m)^[ \t]*elseif[ \t]+\([ \t]*SubID[ \t]*-[ \t]*1[ \t]*\)' +
        '[ \t]*/[ \t]*100[ \t]*==[ \t]*7[ \t]+then\r?$'
    if ([Regex]::Matches($block, $standardHeader).Count -ne 1 -or
        [Regex]::Matches(
            $block,
            'Button:SetText\(NF_L0_ZBXQ701\);').Count -ne 1) {
        throw 'The Equipment Advance Drilling menu anchor has an unknown layout.'
    }

    $ends = [Regex]::Matches($block, '(?m)^[ \t]*end;[ \t]*\r?$')
    if ($ends.Count -ne 1) {
        throw "Expected one Holy Stone menu terminator; found $($ends.Count)."
    }

    $newLine = Get-NewLine $Text
    $branch =
        "`telseif ( SubID - 1 ) / 100 == 8 then$newLine" +
        "`t   local Button = win:GetChild(`"FirstWin_Button`" .. BtnID);$newLine" +
        "`t   Button:SetText(NF_L0_ZBXQ801);$newLine" +
        "`t   Button:Visible(true);$newLine" +
        "`t   Button:SetPosition(320,110);$newLine"
    $absoluteInsert = $region.Start + $ends[0].Index
    return $Text.Insert($absoluteInsert, $branch)
}

function Add-MountGearDrillPage([string]$Text) {
    $region = Get-UniqueRegion -Text $Text `
        -StartPattern '(?m)^function[ \t]+NpcFunEment_SetMsg\(Type,Index,PreSubID,SubID\)\r?$' `
        -EndPattern '(?m)^[ \t]*elseif[ \t]+Index[ \t]*==[ \t]*2[ \t]+and[ \t]+PreSubID[ \t]*==[ \t]*501[ \t]+then\r?$' `
        -Description 'Holy Stone page-selection branch'
    $block = $Text.Substring($region.Start, $region.Length)
    $mountHeader =
        '(?m)^[ \t]*elseif[ \t]+SubID[ \t]*==[ \t]*801[ \t]+then\r?$'
    $mountBranches = [Regex]::Matches($block, $mountHeader)
    if ($mountBranches.Count -gt 1) {
        throw 'Duplicate Mount Gear Drilling pages in NpcFunEment.lua.'
    }
    if ($mountBranches.Count -eq 1) {
        $followingLength = [Math]::Min(
            650,
            $block.Length - $mountBranches[0].Index)
        $following = $block.Substring(
            $mountBranches[0].Index,
            $followingLength)
        $requiredPatterns = @(
            'FirstWin_Text1:SetText\(NF_L0_ZBXQ13\);',
            'FirstWin_Text2:SetText\(NF_L0_ZBXQ14\);',
            'FirstWin_ItemBtn1:Visible\(true\);',
            'FirstWin_ItemBtn1:SetPosition\(60[ \t]*,[ \t]*160\);',
            'NPCFUN:HaveMessageBox\(true\);'
        )
        foreach ($pattern in $requiredPatterns) {
            if ($following -notmatch $pattern) {
                throw 'The existing Mount Gear Drilling page has an unknown layout.'
            }
        }
        return $Text
    }

    if ([Regex]::Matches(
            $block,
            '(?m)^[ \t]*elseif[ \t]+SubID[ \t]*==[ \t]*301[ \t]+then\r?$').Count -ne 1) {
        throw 'The Equipment Drilling page anchor has an unknown layout.'
    }
    $ends = [Regex]::Matches($block, '(?m)^[ \t]*end;[ \t]*\r?$')
    if ($ends.Count -ne 1) {
        throw "Expected one Holy Stone page terminator; found $($ends.Count)."
    }

    $newLine = Get-NewLine $Text
    $branch =
        "`t   elseif SubID == 801 then$newLine" +
        "`t      FirstWin_Text1:SetText(NF_L0_ZBXQ13);$newLine" +
        "`t      FirstWin_Text1:Visible(true);$newLine" +
        "`t      FirstWin_Text1:SetPosition(25,30);$newLine" +
        "`t      FirstWin_Text2:SetText(NF_L0_ZBXQ14);$newLine" +
        "`t      FirstWin_Text2:Visible(true);$newLine" +
        "`t      FirstWin_Text2:SetPosition(25,115);$newLine" +
        "`t      FirstWin_ItemBtn1:Visible(true);$newLine" +
        "`t      FirstWin_ItemBtn1:SetPosition(60,160);$newLine" +
        "`t      NPCFUN:HaveMessageBox(true);$newLine"
    $absoluteInsert = $region.Start + $ends[0].Index
    return $Text.Insert($absoluteInsert, $branch)
}

function Add-MountGearDrillResultBranch([string]$Text) {
    $region = Get-UniqueRegion -Text $Text `
        -StartPattern '(?m)^[ \t]*if[ \t]+math\.mod\(SubID[ \t]*,[ \t]*100\)[ \t]*==[ \t]*0[ \t]+then\r?$' `
        -EndPattern '(?m)^[ \t]*elseif[ \t]+math\.mod\(SubID[ \t]*,[ \t]*100\)[ \t]*==[ \t]*1[ \t]+then\r?$' `
        -Description 'Holy Stone result branch'
    $block = $Text.Substring($region.Start, $region.Length)
    $resultHeader =
        '(?m)^[ \t]*elseif[ \t]+SubID[ \t]*/[ \t]*100[ \t]*==' +
        '[ \t]*35[ \t]+then\r?$'
    $resultBranches = [Regex]::Matches($block, $resultHeader)
    if ($resultBranches.Count -gt 1) {
        throw 'Duplicate Mount Gear Drilling result branches in NpcFunEment.lua.'
    }
    if ($resultBranches.Count -eq 1) {
        $followingLength = [Math]::Min(
            350,
            $block.Length - $resultBranches[0].Index)
        $following = $block.Substring(
            $resultBranches[0].Index,
            $followingLength)
        if ($following -notmatch
                'FirstWin_Text1:SetText\(NF_L0_ZBXQ3500\);' -or
            $following -notmatch 'FirstWin_Text1:Visible\(true\);' -or
            $following -notmatch
                'FirstWin_Text1:SetPosition\(45[ \t]*,[ \t]*100\);') {
            throw 'The existing Mount Gear Drilling result branch has an unknown layout.'
        }
        return $Text
    }

    $ends = [Regex]::Matches($block, '(?m)^[ \t]*end;[ \t]*\r?$')
    if ($ends.Count -ne 1) {
        throw "Expected one Holy Stone result terminator; found $($ends.Count)."
    }

    $newLine = Get-NewLine $Text
    $branch =
        "`t   elseif SubID / 100 == 35 then$newLine" +
        "`t      FirstWin_Text1:SetText(NF_L0_ZBXQ3500);$newLine" +
        "`t      FirstWin_Text1:Visible(true);$newLine" +
        "`t      FirstWin_Text1:SetPosition(45,100);$newLine"
    $absoluteInsert = $region.Start + $ends[0].Index
    return $Text.Insert($absoluteInsert, $branch)
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$resolvedClientRoot = [IO.Path]::GetFullPath($ClientRoot)
$resolvedRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$npcRelativePath = 'Localization\en_us\UI\XML\NpcFun\NpcFunEment.lua'
$luaTextRelativePath = 'Localization\en_us\UI\Base\LuaText.lua'
$npcPath = Join-Path $resolvedClientRoot $npcRelativePath
$luaTextPath = Join-Path $resolvedClientRoot $luaTextRelativePath
foreach ($requiredPath in @($npcPath, $luaTextPath)) {
    if (!(Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required English client file was not found: $requiredPath"
    }
}

$npcDocument = Read-EncodedText $npcPath
$luaTextDocument = Read-EncodedText $luaTextPath
$patchedNpcText = Add-MountGearDrillMenuBranch $npcDocument.Text
$patchedNpcText = Add-MountGearDrillPage $patchedNpcText
$patchedNpcText = Add-MountGearDrillResultBranch $patchedNpcText
$patchedLuaText = Set-LuaAssignment -Text $luaTextDocument.Text `
    -Key 'NF_L0_ZBXQ13' -Value $requirementsText `
    -AnchorKey 'NF_L0_ZBXQ12'
$patchedLuaText = Set-LuaAssignment -Text $patchedLuaText `
    -Key 'NF_L0_ZBXQ14' -Value $eligibilityText `
    -AnchorKey 'NF_L0_ZBXQ13'
$patchedLuaText = Set-LuaAssignment -Text $patchedLuaText `
    -Key 'NF_L0_ZBXQ801' -Value $menuText `
    -AnchorKey 'NF_L0_ZBXQ701'
$patchedLuaText = Set-LuaAssignment -Text $patchedLuaText `
    -Key 'NF_L0_ZBXQ3500' -Value $targetNotMountGearText `
    -AnchorKey 'NF_L0_ZBXQ3400'

[byte[]]$npcBytes = ConvertTo-EncodedBytes $npcDocument $patchedNpcText
[byte[]]$luaTextBytes = ConvertTo-EncodedBytes $luaTextDocument $patchedLuaText
[byte[]]$currentNpcBytes = [IO.File]::ReadAllBytes($npcPath)
[byte[]]$currentLuaTextBytes = [IO.File]::ReadAllBytes($luaTextPath)
$npcChanged = !(Test-ByteArraysEqual $currentNpcBytes $npcBytes)
$localizationChanged = !(Test-ByteArraysEqual $currentLuaTextBytes $luaTextBytes)
$isPatched = !$npcChanged -and !$localizationChanged

if ($CheckOnly) {
    [pscustomobject]@{
        ClientRoot = $resolvedClientRoot
        RepositoryRoot = $resolvedRepositoryRoot
        Compliant = $isPatched
        NpcMenuAndPage = !$npcChanged
        Localization = !$localizationChanged
        Changed = $false
    }
    if (!$isPatched) {
        throw 'The Mount Gear Drilling client patch is not fully applied.'
    }
    return
}

if ($isPatched) {
    [pscustomobject]@{
        ClientRoot = $resolvedClientRoot
        RepositoryRoot = $resolvedRepositoryRoot
        Changed = $false
        State = 'Patched'
        BackupDirectory = $null
    }
    return
}

if (($npcChanged -or $localizationChanged) -and
    (Get-Process -Name 'Origin', 'Launch' -ErrorAction SilentlyContinue)) {
    throw 'Close Origin.exe and Launch.exe before patching the client.'
}

if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
    $BackupDirectory = Join-Path $resolvedRepositoryRoot `
        "backups\mount-gear-drilling-$timestamp"
}
$resolvedBackup = [IO.Path]::GetFullPath($BackupDirectory)
$clientPrefix = $resolvedClientRoot.TrimEnd('\') + '\'
if ($resolvedBackup.StartsWith(
        $clientPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BackupDirectory must be outside the client root.'
}
if (Test-Path -LiteralPath $resolvedBackup) {
    throw "Backup directory already exists: $resolvedBackup"
}

$backupFiles = @(
    [pscustomobject]@{
        Source = $npcPath
        Backup = Join-Path $resolvedBackup "client\$npcRelativePath"
    },
    [pscustomobject]@{
        Source = $luaTextPath
        Backup = Join-Path $resolvedBackup "client\$luaTextRelativePath"
    }
)
[IO.Directory]::CreateDirectory($resolvedBackup) | Out-Null
foreach ($file in $backupFiles) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $file.Backup)) |
        Out-Null
    Copy-Item -LiteralPath $file.Source -Destination $file.Backup
    $sourceHash = (Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash
    $backupHash = (Get-FileHash -LiteralPath $file.Backup -Algorithm SHA256).Hash
    if ($sourceHash -ne $backupHash) {
        throw "Backup hash verification failed for $($file.Source)."
    }
}

try {
    if ($npcChanged) {
        [IO.File]::WriteAllBytes($npcPath, $npcBytes)
    }
    if ($localizationChanged) {
        [IO.File]::WriteAllBytes($luaTextPath, $luaTextBytes)
    }

    $verifyNpc = Read-EncodedText $npcPath
    $verifyNpcText = Add-MountGearDrillMenuBranch $verifyNpc.Text
    $verifyNpcText = Add-MountGearDrillPage $verifyNpcText
    $verifyNpcText = Add-MountGearDrillResultBranch $verifyNpcText
    $verifyLuaText = Read-EncodedText $luaTextPath
    $verifyLocalization = Set-LuaAssignment -Text $verifyLuaText.Text `
        -Key 'NF_L0_ZBXQ13' -Value $requirementsText `
        -AnchorKey 'NF_L0_ZBXQ12'
    $verifyLocalization = Set-LuaAssignment -Text $verifyLocalization `
        -Key 'NF_L0_ZBXQ14' -Value $eligibilityText `
        -AnchorKey 'NF_L0_ZBXQ13'
    $verifyLocalization = Set-LuaAssignment -Text $verifyLocalization `
        -Key 'NF_L0_ZBXQ801' -Value $menuText `
        -AnchorKey 'NF_L0_ZBXQ701'
    $verifyLocalization = Set-LuaAssignment -Text $verifyLocalization `
        -Key 'NF_L0_ZBXQ3500' -Value $targetNotMountGearText `
        -AnchorKey 'NF_L0_ZBXQ3400'
    if ($verifyNpcText -cne $verifyNpc.Text -or
        $verifyLocalization -cne $verifyLuaText.Text) {
        throw 'Post-write verification found an incomplete client patch.'
    }
}
catch {
    foreach ($file in $backupFiles) {
        Copy-Item -LiteralPath $file.Backup -Destination $file.Source -Force
    }
    throw
}

[pscustomobject]@{
    ClientRoot = $resolvedClientRoot
    RepositoryRoot = $resolvedRepositoryRoot
    Changed = $true
    State = 'Patched'
    BackupDirectory = $resolvedBackup
    NpcMenuAndPageUpdated = $npcChanged
    LocalizationUpdated = $localizationChanged
}
