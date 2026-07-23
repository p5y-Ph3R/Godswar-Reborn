param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot = 'C:\Reborn\backups',
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8Bom = [Text.UTF8Encoding]::new($true)
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$utf16LeBom = [Text.UnicodeEncoding]::new($false, $true)
$gb2312 = [Text.Encoding]::GetEncoding(936)

function Set-XmlItem(
    [string]$Text,
    [string]$ItemId,
    [string]$AnchorId,
    [string]$Element
) {
    $pattern = '<[A-Za-z_][\w]*\s+ID="' +
        [regex]::Escape($ItemId) + '"[^<>]*/>'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -gt 1) {
        throw "Duplicate ItemBaseAttribute ID $ItemId."
    }
    if ($matches.Count -eq 1) {
        $match = $matches[0]
        return $Text.Substring(0, $match.Index) + $Element +
            $Text.Substring($match.Index + $match.Length)
    }

    $anchorPattern = '<[A-Za-z_][\w]*\s+ID="' +
        [regex]::Escape($AnchorId) + '"[^<>]*/>'
    $anchors = [regex]::Matches($Text, $anchorPattern)
    if ($anchors.Count -ne 1) {
        throw "Expected one ItemBaseAttribute anchor ID $AnchorId for $ItemId; found $($anchors.Count)."
    }

    $anchor = $anchors[0]
    $anchorEnd = $anchor.Index + $anchor.Length
    $lineStart = $Text.LastIndexOf("`n", [Math]::Max(0, $anchor.Index - 1)) + 1
    $indent = [regex]::Match(
        $Text.Substring($lineStart, $anchor.Index - $lineStart),
        '^[ \t]*'
    ).Value
    $isLineFormatted = $anchorEnd -ge $Text.Length -or
        $Text[$anchorEnd] -eq [char]13 -or $Text[$anchorEnd] -eq [char]10
    $separator = if ($isLineFormatted) {
        if ($Text.Contains("`r`n")) { "`r`n" + $indent } else { "`n" + $indent }
    }
    else {
        ''
    }
    $insert = $anchor.Value + $separator + $Element
    return $Text.Substring(0, $anchor.Index) + $insert +
        $Text.Substring($anchor.Index + $anchor.Length)
}

function Set-LocalizedLine(
    [string]$Text,
    [string]$Key,
    [string]$Value,
    [string]$AnchorKey
) {
    $line = $Key + [char]9 + $Value
    $pattern = '(?m)^' + [regex]::Escape($Key) + '\t[^\r\n]*(?=\r?$)'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -gt 1) {
        throw "Duplicate localization key '$Key'."
    }
    if ($matches.Count -eq 1) {
        $match = $matches[0]
        return $Text.Substring(0, $match.Index) + $line +
            $Text.Substring($match.Index + $match.Length)
    }

    $anchorPattern = '(?m)^' + [regex]::Escape($AnchorKey) +
        '\t[^\r\n]*(?=\r?$)'
    $anchors = [regex]::Matches($Text, $anchorPattern)
    if ($anchors.Count -ne 1) {
        throw "Expected one localization anchor '$AnchorKey' for '$Key'; found $($anchors.Count)."
    }
    $anchor = $anchors[0]
    $newline = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $insert = $anchor.Value + $newline + $line
    return $Text.Substring(0, $anchor.Index) + $insert +
        $Text.Substring($anchor.Index + $anchor.Length)
}

function Set-LuaAssignment(
    [string]$Text,
    [string]$Key,
    [string]$Value
) {
    $prefix = if ($Key.StartsWith('LuaText.')) { $Key } else { $Key }
    $line = $prefix + ' = "' + $Value.Replace('"', '\"') + '"'
    $pattern = '(?m)^' + [regex]::Escape($prefix) +
        '[ \t]*=[ \t]*"[^\r\n]*"[ \t]*(?=\r?$)'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) {
        throw "Expected one Lua assignment '$prefix'; found $($matches.Count)."
    }
    $match = $matches[0]
    return $Text.Substring(0, $match.Index) + $line +
        $Text.Substring($match.Index + $match.Length)
}

function Decode-Utf8([string]$Base64) {
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

$pieceItems = @(
    @{
        Id = '4216'; Anchor = '4215'; TextAnchor = 'MaterialBase6'; Key = 'MaterialBase7'; Icon = '144,0';
        EnName = 'Level 5 Sapphire Pieces';
        EnDescription = 'Collect 99 Level 5 Sapphire Pieces and ask the Gear Mentor to combine them into one Level 5 Sapphire.';
        ZhName = Decode-Utf8 '5LqU57qn6JOd5a6d55+z56KO54mH';
        ZhDescription = Decode-Utf8 '5pS26ZuGOTnkuKrkupTnuqfokp3lrp3nn7PniYfvvIzlj6/lraboo4XlpIfor5vlr7zluIjlkIjmiJDkuIDpopfkuJTnuqfokp3lrp3nn7PjgII='
    },
    @{
        Id = '4226'; Anchor = '4225'; TextAnchor = 'MaterialAppend6'; Key = 'MaterialAppend7'; Icon = '180,0';
        EnName = 'Level 5 Emerald Pieces';
        EnDescription = 'Collect 99 Level 5 Emerald Pieces and ask the Gear Mentor to combine them into one Level 5 Emerald.';
        ZhName = Decode-Utf8 '5LqU57qn57u/5a6d55+z56KO54mH';
        ZhDescription = Decode-Utf8 '5pS26ZuGOTnkuKrkupTnuqfnu7/lrp3nn7PniYfvvIzlj6/lraboo4XlpIfor5vlr7zluIjlkIjmiJDkuIDpopfkuJTnuqfnu7/lrp3nn7PjgII='
    },
    @{
        Id = '4235'; Anchor = '4234'; TextAnchor = 'MaterialOdds5'; Key = 'MaterialOdds6'; Icon = '108,0';
        EnName = 'Level 5 Crystal Pieces';
        EnDescription = 'Collect 99 Level 5 Crystal Pieces and ask the Gear Mentor to combine them into one Level 5 Crystal.';
        ZhName = Decode-Utf8 '5LqU57qn5rC05pm256KO54mH';
        ZhDescription = Decode-Utf8 '5pS26ZuGOTnkuKrkupTnuqfmsLTmmbbnoo7niYfvvIzlj6/ku6Xor7foo4XlpIflvLrljJbluIjlkIjmiJDkuIDpopfkupTnuqfmsLTmmbbjgII='
    }
)

$changes = [Collections.Generic.List[object]]::new()
$patchedByPath = @{}
$encodingByPath = @{}
foreach ($locale in @('en_us', 'zh_cn')) {
    $base = Join-Path $ClientRoot "Localization\$locale"
    $itemPath = Join-Path $base 'Settings\Sys\ItemBaseAttribute.xml'
    $namePath = Join-Path $base 'Text\EquipName.dat'
    $descriptionPath = Join-Path $base 'Text\EquipDescription.dat'
    $luaPath = Join-Path $base 'UI\Base\LuaText.lua'
    foreach ($path in @($itemPath, $namePath, $descriptionPath, $luaPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required client file is missing: $path"
        }
    }

    $itemText = [IO.File]::ReadAllText($itemPath, [Text.Encoding]::UTF8)
    foreach ($piece in $pieceItems) {
        $element = '<' + $piece.Key + ' ID="' + $piece.Id +
            '" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon4.gwo" Icon="' +
            $piece.Icon + '" Random="0" Distribution="0,0" Money="0" Overlap="99" BindType="1" />'
        $itemText = Set-XmlItem $itemText $piece.Id $piece.Anchor $element
    }
    [xml]$itemDocument = $itemText
    foreach ($piece in $pieceItems) {
        $nodes = @($itemDocument.SelectNodes("//*[@ID='$($piece.Id)']"))
        if ($nodes.Count -ne 1 -or
            $nodes[0].Texture -ne './Localization/en_us/UI/Texture/Icon4.gwo' -or
            $nodes[0].Icon -ne $piece.Icon -or
            $nodes[0].Overlap -ne '99') {
            throw "Level-5 piece ItemBaseAttribute validation failed for ID $($piece.Id) ($locale)."
        }
    }

    $textEncoding = if ($locale -eq 'en_us') { $utf16LeBom } else { $gb2312 }
    $nameText = [IO.File]::ReadAllText($namePath, $textEncoding)
    $descriptionText = [IO.File]::ReadAllText($descriptionPath, $textEncoding)
    foreach ($piece in $pieceItems) {
        $name = if ($locale -eq 'en_us') { $piece.EnName } else { $piece.ZhName }
        $description = if ($locale -eq 'en_us') {
            $piece.EnDescription
        }
        else {
            $piece.ZhDescription
        }
        $nameText = Set-LocalizedLine $nameText $piece.Key $name $piece.TextAnchor
        $descriptionText = Set-LocalizedLine (
            $descriptionText
        ) $piece.Key $description $piece.TextAnchor
    }

    $luaEncoding = if ($locale -eq 'en_us') { $utf8NoBom } else { $utf8Bom }
    $luaText = [IO.File]::ReadAllText($luaPath, [Text.Encoding]::UTF8)
    if ($locale -eq 'en_us') {
        $luaText = Set-LuaAssignment $luaText 'NF_L0_FJ90' '|cffFFFF00*Combine pieces into Level 4/5 gems|cffFFFFFF'
        $luaText = Set-LuaAssignment $luaText 'NF_L0_FJ1824' '  I am able to transform a crystal into several crystals of a lower grade. \n\n    Each Level 5 Crystal     can be transformed into 2 Level 4 Crystals.\n    Each Level 3 Crystal     can be transformed into 4 Level 2 Crystals.\n    Each Level 2 Crystal     can be transformed into 8 Level 1 Crystals.\n\n|cffF14187  Notes: Crystals obtained from a bound higher grade crystal will be bound too.|cffFFFFFF'
        $luaText = Set-LuaAssignment $luaText 'LuaText.NF_Break_T201' 'I can combine 99 matching Level 4 or Level 5 gem pieces into the corresponding gem. Put the pieces below, and leave the rest to me.'
        $luaText = Set-LuaAssignment $luaText 'LuaText.NF_Break_T301' 'Those are not supported Level 4 or Level 5 gem pieces.'
        $luaText = Set-LuaAssignment $luaText 'LuaText.NF_Break_T302' 'You need 99 matching gem pieces.'
    }
    else {
        $luaText = Set-LuaAssignment $luaText 'NF_L0_FJ90' (
            Decode-Utf8 'fGNmZkZGRkYwMOKWveeijueJh+WQiOaIkOWbmy/kupTnuqflrp3nn7N8Y2ZmRkZGRkZG'
        )
        $luaText = Set-LuaAssignment $luaText 'NF_L0_FJ1824' (
            Decode-Utf8 'ICDmiJHlj6/ku6XluK7kvaDlsIbpq5jlk4HotKjnmoTmsLTmmbbvvIzliIbop6PmiJDmlbDkuKrkvY7kuIDmoaPmrKHnmoTmsLTmmbblk6bvvIHov5nmmK/kuIDkuKrlvojmo5LnmoTlip/og73lkKfvvJ/miJHog73liIbop6PnmoTmsLTmmbblpoLkuIvvvJpcblxuICAgIOS6lOe6p+awtOaZtiAgICAg5YiG6Kej5oiQMuS4quWbm+e6p+awtOaZtlxuICAgIOS4iee6p+awtOaZtiAgICAg5YiG6Kej5oiQNOS4quS6jOe6p+awtOaZtlxuICAgIOS6jOe6p+awtOaZtiAgICAg5YiG6Kej5oiQOOS4quS4gOe6p+awtOaZtlxuXG5cbnxjZmZGMTQxODcgIOS4jei/h+imgeazqOaEj+eahOaYr++8mue7keWumueahOawtOaZtu+8jOWIhuino+WQjuS5n+aYr+e7keWumueahOWTpu+8gXxjZmZGRkZGRkY='
        )
        $luaText = Set-LuaAssignment $luaText 'LuaText.NF_Break_T201' (
            Decode-Utf8 '5oiR5Y+v5Lul5bCGOTnkuKrnm7jlkIznmoTlm5vnuqfmiJbkupTnuqflrp3nn7Pnoo7niYflkIjmiJDkuIDpopflr7nlupTnrYnnuqfnmoTlrp3nn7PjgII='
        )
        $luaText = Set-LuaAssignment $luaText 'LuaText.NF_Break_T301' (
            Decode-Utf8 '5pS+5YWl55qE5LiN5piv5Y+v5ZCI5oiQ55qE5Zub57qn5oiW5LqU57qn5a6d55+z56KO54mH44CC'
        )
        $luaText = Set-LuaAssignment $luaText 'LuaText.NF_Break_T302' (
            Decode-Utf8 '6ZyA6KaBOTnkuKrnm7jlkIznmoTlrp3nn7Pnoo7niYfjgII='
        )
    }

    foreach ($entry in @(
            @{ Path = $itemPath; Text = $itemText; Encoding = $utf8Bom },
            @{ Path = $namePath; Text = $nameText; Encoding = $textEncoding },
            @{ Path = $descriptionPath; Text = $descriptionText; Encoding = $textEncoding },
            @{ Path = $luaPath; Text = $luaText; Encoding = $luaEncoding }
        )) {
        $patchedByPath[$entry.Path] = $entry.Text
        $encodingByPath[$entry.Path] = $entry.Encoding
        $readEncoding = if ($entry.Path -eq $luaPath -or $entry.Path -eq $itemPath) {
            [Text.Encoding]::UTF8
        }
        else {
            $textEncoding
        }
        $current = [IO.File]::ReadAllText(
            $entry.Path,
            $readEncoding
        )
        if ($current -cne $entry.Text) {
            $changes.Add($entry)
        }
    }
}

if ($Check) {
    if ($changes.Count -ne 0) {
        throw "Gear Mentor client material patch is not installed ($($changes.Count) files differ)."
    }
    Write-Output 'Gear Mentor client material patch verified.'
    exit 0
}

if ($changes.Count -eq 0) {
    Write-Output 'Gear Mentor client material patch is already installed.'
    exit 0
}

$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff')
$backupDirectory = Join-Path $BackupRoot "client-gear-mentor-materials-$timestamp"
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$manifest = [Collections.Generic.List[object]]::new()
$normalizedClientRoot = [IO.Path]::GetFullPath($ClientRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
) + [IO.Path]::DirectorySeparatorChar
foreach ($entry in $changes) {
    $fullPath = [IO.Path]::GetFullPath($entry.Path)
    if (-not $fullPath.StartsWith(
            $normalizedClientRoot,
            [StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Refusing to back up a path outside the client root: $fullPath"
    }
    $relative = $fullPath.Substring($normalizedClientRoot.Length)
    $backupPath = Join-Path $backupDirectory $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backupPath)) | Out-Null
    Copy-Item -LiteralPath $entry.Path -Destination $backupPath
    $manifest.Add([pscustomobject]@{
        Path = $relative
        Sha256 = (Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash
    })
}

foreach ($entry in $changes) {
    [IO.File]::WriteAllText($entry.Path, $entry.Text, $entry.Encoding)
}

[IO.File]::WriteAllText(
    (Join-Path $backupDirectory 'manifest.json'),
    ($manifest | ConvertTo-Json -Depth 4),
    $utf8NoBom
)

& $PSCommandPath -ClientRoot $ClientRoot -BackupRoot $BackupRoot -Check
Write-Output "Installed Gear Mentor material patch; rollback backup: $backupDirectory"
