[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [switch]$RepositorySource,
    [switch]$Check,
    [string]$BackupDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resultText =
    '|cffF14187Evasion Signets are accepted only for a current Level 4, 5, ' +
    'or 6 Holy Stone (up to reaching Level 7). Level 7+ upgrades have no ' +
    'rollback-protection item and can lose 1 level on failure. Combine ' +
    'Level 7 Holy Stones to reach Level 8 safely.|cffffffff'
$catalystErrorText =
    '|cffF14187The optional catalyst is missing, stale, or does not match ' +
    'this Holy Stone. Evasion Signets protect only Level 4->5 (Copper), ' +
    'Level 5->6 (Silver), and Level 6->7 (Gold).|cffffffff'
$protectionSlotText =
    "Place 1 Goddess' Stone here at Holy Stone Levels 1-9. A matching " +
    'Evasion Signet is accepted only when the current Holy Stone is Level ' +
    '4, 5, or 6.'
$upgradeNotesText =
    '|cffF14187Notes: (1) Level 1 Eclipse Stones have a 90% success rate, ' +
    'Level 2 Eclipse Stones 25%, and Level 3 Eclipse Stones 10%. ' +
    '(2) A failed upgrade normally lowers the Holy Stone by 1 level. ' +
    "A Goddess' Stone adds 10% success at Levels 1-9 but never prevents " +
    'rollback. A matching Evasion Signet adds 10% success and prevents ' +
    'rollback only for Level 4->5 (Copper), Level 5->6 (Silver), and ' +
    'Level 6->7 (Gold). Level 7+ upgrades have no rollback-protection item; ' +
    'combine Level 7 Holy Stones to reach Level 8 safely.' +
    '|cffffffff'
$legacySignetText =
    'Legacy compatibility item; it is not usable. Level 7+ Holy Stone ' +
    'upgrades have no rollback-protection item. Combine Level 7 Holy Stones ' +
    'to reach Level 8 safely.'
$combinationText =
    'Combine four Holy Stones of the same Grade. The major Holy Stone must ' +
    'be Grade 4-9 and gains 1 Grade; the other three are consumed.\nPut ' +
    'the major Holy Stone in the slot below'
$combinationLimitText =
    '|cffF14187Only four Holy Stones of the same Grade can be combined, and ' +
    'the major Holy Stone must be Grade 4-9.|cffffffff'

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
        # The audited English client files are UTF-8. Strict decoding prevents
        # silently converting an unknown legacy code page during a patch.
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

    $insertAt = $anchors[0].Index + $anchors[0].Length
    if ($insertAt -lt $Text.Length -and $Text[$insertAt] -eq "`n") {
        $insertAt++
    }
    elseif ($insertAt -eq $Text.Length) {
        return $Text + (Get-NewLine $Text) + $expectedLine
    }

    $newLine = Get-NewLine $Text
    return $Text.Insert($insertAt, $expectedLine + $newLine)
}

function Add-EvasionResultBranch([string]$Text) {
    $branchPattern =
        '(?m)^[ \t]*elseif[ \t]+SubID[ \t]*/[ \t]*100[ \t]*==' +
        '[ \t]*34[ \t]+then[ \t]*\r?$'
    $branches = [Regex]::Matches($Text, $branchPattern)
    if ($branches.Count -gt 1) {
        throw 'Duplicate SubID / 100 == 34 branches in NpcFunEment.lua.'
    }
    if ($branches.Count -eq 1) {
        $followingLength = [Math]::Min(450, $Text.Length - $branches[0].Index)
        $following = $Text.Substring($branches[0].Index, $followingLength)
        if ($following -notmatch
            'FirstWin_Text1:SetText\(NF_L0_ZBXQ3400\);' -or
            $following -notmatch 'FirstWin_Text1:Visible\(true\);' -or
            $following -notmatch
            'FirstWin_Text1:SetPosition\(25[ \t]*,[ \t]*220\);') {
            throw 'The existing SubID 34 result branch has an unknown layout.'
        }
        return $Text
    }

    $branch30Pattern =
        '(?m)^[ \t]*elseif[ \t]+SubID[ \t]*/[ \t]*100[ \t]*==' +
        '[ \t]*30[ \t]+then[ \t]*\r?$'
    $branch30 = [Regex]::Matches($Text, $branch30Pattern)
    if ($branch30.Count -ne 1) {
        throw "Expected one SubID 30 result branch; found $($branch30.Count)."
    }

    $nextControlPattern =
        '(?m)^[ \t]*(?:elseif[ \t]+SubID[ \t]*/[ \t]*100[ \t]*==|' +
        'end;)[^\r\n]*\r?$'
    $nextControls = [Regex]::Matches(
        $Text.Substring($branch30[0].Index + $branch30[0].Length),
        $nextControlPattern)
    if ($nextControls.Count -lt 1) {
        throw 'Could not locate the end of the SubID 30 result branch.'
    }

    $insertAt =
        $branch30[0].Index +
        $branch30[0].Length +
        $nextControls[0].Index
    while ($insertAt -lt $Text.Length -and
        ($Text[$insertAt] -eq "`r" -or $Text[$insertAt] -eq "`n")) {
        $insertAt++
    }

    $newLine = Get-NewLine $Text
    $branch =
        "`t`telseif SubID / 100 == 34 then$newLine" +
        "`t`t`tFirstWin_Text1:SetText(NF_L0_ZBXQ3400);$newLine" +
        "`t`t`tFirstWin_Text1:Visible(true);$newLine" +
        "`t`t`tFirstWin_Text1:SetPosition(25,220);$newLine"
    return $Text.Insert($insertAt, $branch)
}

function Set-CombinationLimitResult([string]$Text) {
    $branchPattern =
        '(?m)^[ \t]*elseif[ \t]+SubID[ \t]*/[ \t]*100[ \t]*==' +
        '[ \t]*27[ \t]+then[ \t]*\r?$'
    $branches = [Regex]::Matches($Text, $branchPattern)
    if ($branches.Count -ne 1) {
        throw "Expected one SubID 27 result branch; found $($branches.Count)."
    }
    $tail = $Text.Substring(
        $branches[0].Index + $branches[0].Length)
    $next = [Regex]::Match(
        $tail,
        '(?m)^[ \t]*(?:elseif[ \t]+SubID[ \t]*/[ \t]*100[ \t]*==|end;?)')
    if (!$next.Success) {
        throw 'Could not locate the end of the SubID 27 result branch.'
    }
    $bodyStart = $branches[0].Index + $branches[0].Length
    $body = $Text.Substring($bodyStart, $next.Index)
    $messages = [Regex]::Matches(
        $body,
        'FirstWin_Text3:SetText\(HolyLevup(?<id>[56])\)')
    if ($messages.Count -ne 1) {
        throw 'The SubID 27 result branch has an unknown message layout.'
    }
    if ($messages[0].Groups['id'].Value -eq '6') {
        return $Text
    }
    $absolute = $bodyStart + $messages[0].Index
    return $Text.Remove($absolute, $messages[0].Length).Insert(
        $absolute,
        'FirstWin_Text3:SetText(HolyLevup6)')
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

function Set-TabSeparatedValue(
    [string]$Text,
    [string]$Key,
    [string]$Value
) {
    if ($Value.Contains("`t") -or
        $Value.Contains("`r") -or
        $Value.Contains("`n")) {
        throw "The requested value for $Key is not a single data row."
    }
    $pattern =
        "(?m)^$([Regex]::Escape($Key))`t[^`r`n]*(?<cr>`r?)$"
    $matches = [Regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) {
        throw "Expected one description row $Key; found $($matches.Count)."
    }
    $replacement = "$Key`t$Value" + $matches[0].Groups['cr'].Value
    return $Text.Remove($matches[0].Index, $matches[0].Length).Insert(
        $matches[0].Index,
        $replacement)
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ($RepositorySource -and !$PSBoundParameters.ContainsKey('ClientRoot')) {
    $ClientRoot = $repositoryRoot
}
$resolvedRoot = [IO.Path]::GetFullPath($ClientRoot)
$npcRelativePath =
    'Localization\en_us\UI\XML\NpcFun\NpcFunEment.lua'
$luaTextRelativePath = 'Localization\en_us\UI\Base\LuaText.lua'
$descriptionRelativePath = 'Localization\en_us\Text\EquipDescription.dat'
$npcPath = Join-Path $resolvedRoot $npcRelativePath
$luaTextPath = Join-Path $resolvedRoot $luaTextRelativePath
$descriptionPath = Join-Path $resolvedRoot $descriptionRelativePath
$relativePaths = @($luaTextRelativePath, $descriptionRelativePath)
if (!$RepositorySource) {
    $relativePaths = @($npcRelativePath) + $relativePaths
}
foreach ($relativePath in $relativePaths) {
    $path = Join-Path $resolvedRoot $relativePath
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required English client file was not found: $path"
    }
}

$luaTextDocument = Read-EncodedText $luaTextPath
$descriptionDocument = Read-EncodedText $descriptionPath
$patchedLuaText = Set-LuaAssignment -Text $luaTextDocument.Text `
    -Key 'NF_L0_ZBXQ7' -Value $protectionSlotText `
    -AnchorKey 'NF_L0_ZBXQ6'
$patchedLuaText = Set-LuaAssignment -Text $patchedLuaText `
    -Key 'NF_L0_ZBXQ8' -Value $upgradeNotesText `
    -AnchorKey 'NF_L0_ZBXQ7'
$patchedLuaText = Set-LuaAssignment -Text $patchedLuaText `
    -Key 'NF_L0_ZBXQ2400' -Value $catalystErrorText `
    -AnchorKey 'NF_L0_ZBXQ2300'
$patchedLuaText = Set-LuaAssignment -Text $patchedLuaText `
    -Key 'NF_L0_ZBXQ3400' -Value $resultText `
    -AnchorKey 'NF_L0_ZBXQ2400'
$patchedLuaText = Set-LuaAssignment -Text $patchedLuaText `
    -Key 'HolyLevup2' -Value $combinationText `
    -AnchorKey 'HolyLevup1'
$patchedLuaText = Set-LuaAssignment -Text $patchedLuaText `
    -Key 'HolyLevup6' -Value $combinationLimitText `
    -AnchorKey 'HolyLevup5'
$patchedDescriptionText = $descriptionDocument.Text
foreach ($itemId in 9054..9056) {
    $patchedDescriptionText = Set-TabSeparatedValue `
        -Text $patchedDescriptionText `
        -Key "Stone$itemId" `
        -Value $legacySignetText
}

[byte[]]$luaTextBytes = ConvertTo-EncodedBytes $luaTextDocument $patchedLuaText
[byte[]]$descriptionBytes =
    ConvertTo-EncodedBytes $descriptionDocument $patchedDescriptionText
[byte[]]$currentLuaTextBytes = [IO.File]::ReadAllBytes($luaTextPath)
[byte[]]$currentDescriptionBytes = [IO.File]::ReadAllBytes($descriptionPath)
$luaTextChanged = !(Test-ByteArraysEqual $currentLuaTextBytes $luaTextBytes)
$descriptionChanged =
    !(Test-ByteArraysEqual $currentDescriptionBytes $descriptionBytes)
$npcChanged = $false
$npcDocument = $null
[byte[]]$npcBytes = @()
if (!$RepositorySource) {
    $npcDocument = Read-EncodedText $npcPath
    $patchedNpcText = Add-EvasionResultBranch $npcDocument.Text
    $patchedNpcText = Set-CombinationLimitResult $patchedNpcText
    [byte[]]$npcBytes = ConvertTo-EncodedBytes $npcDocument $patchedNpcText
    [byte[]]$currentNpcBytes = [IO.File]::ReadAllBytes($npcPath)
    $npcChanged = !(Test-ByteArraysEqual $currentNpcBytes $npcBytes)
}
$isPatched = !$npcChanged -and !$luaTextChanged -and !$descriptionChanged

if ($Check) {
    $checkResult = [pscustomobject]@{
        ClientRoot = $resolvedRoot
        Compliant = $isPatched
        Mode = if ($RepositorySource) { 'RepositorySource' } else { 'Client' }
        NpcResultBranch = if ($RepositorySource) { 'NotApplicable' } else { !$npcChanged }
        Localization = !$luaTextChanged
        LegacySignetDescriptions = !$descriptionChanged
        Changed = $false
    }
    $checkResult
    if (!$isPatched) {
        throw 'The Holy Stone Evasion-limit client patch is not fully applied.'
    }
    return
}

if ($isPatched) {
    [pscustomobject]@{
        ClientRoot = $resolvedRoot
        Changed = $false
        State = 'Patched'
        BackupDirectory = $null
    }
    return
}

if (!$RepositorySource -and
    (Get-Process -Name 'Origin', 'Launch' -ErrorAction SilentlyContinue)) {
    throw 'Close Origin.exe and Launch.exe before patching the client.'
}

if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
    $backupName = "holy-stone-evasion-limit-$timestamp"
    $backupParent = if ($RepositorySource) {
        Join-Path $repositoryRoot 'artifacts'
    }
    else {
        Join-Path $repositoryRoot 'backups'
    }
    $BackupDirectory = Join-Path $backupParent $backupName
}
$resolvedBackup = [IO.Path]::GetFullPath($BackupDirectory)
if (!$RepositorySource -and $resolvedBackup.StartsWith(
        $resolvedRoot.TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BackupDirectory must be outside the client root.'
}
if (Test-Path -LiteralPath $resolvedBackup) {
    throw "Backup directory already exists: $resolvedBackup"
}

[IO.Directory]::CreateDirectory($resolvedBackup) | Out-Null
foreach ($relativePath in $relativePaths) {
    $sourcePath = Join-Path $resolvedRoot $relativePath
    $backupPath = Join-Path $resolvedBackup $relativePath
    [IO.Directory]::CreateDirectory((Split-Path -Parent $backupPath)) |
        Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $backupPath
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash) {
        throw "Backup hash verification failed for $relativePath."
    }
}

try {
    if ($npcChanged) {
        [IO.File]::WriteAllBytes($npcPath, $npcBytes)
    }
    if ($luaTextChanged) {
        [IO.File]::WriteAllBytes($luaTextPath, $luaTextBytes)
    }
    if ($descriptionChanged) {
        [IO.File]::WriteAllBytes($descriptionPath, $descriptionBytes)
    }

    $verifyLuaText = Read-EncodedText $luaTextPath
    $verifyDescription = Read-EncodedText $descriptionPath
    $checkedLuaText = Set-LuaAssignment -Text $verifyLuaText.Text `
        -Key 'NF_L0_ZBXQ7' -Value $protectionSlotText `
        -AnchorKey 'NF_L0_ZBXQ6'
    $checkedLuaText = Set-LuaAssignment -Text $checkedLuaText `
        -Key 'NF_L0_ZBXQ8' -Value $upgradeNotesText `
        -AnchorKey 'NF_L0_ZBXQ7'
    $checkedLuaText = Set-LuaAssignment -Text $checkedLuaText `
        -Key 'NF_L0_ZBXQ2400' -Value $catalystErrorText `
        -AnchorKey 'NF_L0_ZBXQ2300'
    $checkedLuaText = Set-LuaAssignment -Text $checkedLuaText `
        -Key 'NF_L0_ZBXQ3400' -Value $resultText `
        -AnchorKey 'NF_L0_ZBXQ2400'
    $checkedLuaText = Set-LuaAssignment -Text $checkedLuaText `
        -Key 'HolyLevup2' -Value $combinationText `
        -AnchorKey 'HolyLevup1'
    $checkedLuaText = Set-LuaAssignment -Text $checkedLuaText `
        -Key 'HolyLevup6' -Value $combinationLimitText `
        -AnchorKey 'HolyLevup5'
    $checkedDescription = $verifyDescription.Text
    foreach ($itemId in 9054..9056) {
        $checkedDescription = Set-TabSeparatedValue `
            -Text $checkedDescription `
            -Key "Stone$itemId" `
            -Value $legacySignetText
    }
    $npcValid = $true
    if (!$RepositorySource) {
        $verifyNpc = Read-EncodedText $npcPath
        $npcValid =
            (Set-CombinationLimitResult (
                Add-EvasionResultBranch $verifyNpc.Text)) -ceq
            $verifyNpc.Text
    }
    if (!$npcValid -or
        $checkedLuaText -cne $verifyLuaText.Text -or
        $checkedDescription -cne $verifyDescription.Text) {
        throw 'Post-write verification found an incomplete client patch.'
    }
}
catch {
    foreach ($relativePath in $relativePaths) {
        Copy-Item -LiteralPath (Join-Path $resolvedBackup $relativePath) `
            -Destination (Join-Path $resolvedRoot $relativePath) -Force
    }
    throw
}

[pscustomobject]@{
    ClientRoot = $resolvedRoot
    Changed = $true
    State = 'Patched'
    BackupDirectory = $resolvedBackup
    NpcResultBranchAdded = $npcChanged
    LocalizationUpdated = $luaTextChanged
    LegacySignetDescriptionsUpdated = $descriptionChanged
}
