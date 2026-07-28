[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [Parameter(Mandatory = $true)]
    [string]$BackupDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$itemId = 11095
$itemKey = "Pet$itemId"
$displayName = 'Ambrosia of Rebirth'
$description =
    'Use this to gain a rebirth attempt for a pet performing rebirth 61 through 100.'

function Read-EncodedText([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 2 -and
        $bytes[0] -eq 0xFF -and
        $bytes[1] -eq 0xFE) {
        return [pscustomobject]@{
            Text = [Text.Encoding]::Unicode.GetString(
                $bytes,
                2,
                $bytes.Length - 2)
            Encoding = [Text.UnicodeEncoding]::new(
                $false,
                $true)
        }
    }

    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        return [pscustomobject]@{
            Text = [Text.Encoding]::UTF8.GetString(
                $bytes,
                3,
                $bytes.Length - 3)
            Encoding = [Text.UTF8Encoding]::new($true)
        }
    }

    return [pscustomobject]@{
        Text = [Text.Encoding]::UTF8.GetString($bytes)
        Encoding = [Text.UTF8Encoding]::new($false)
    }
}

function Write-EncodedText(
    [string]$Path,
    [string]$Text,
    [Text.Encoding]$Encoding
) {
    $temporaryPath = "$Path.reborn-$itemId.tmp"
    try {
        [IO.File]::WriteAllText(
            $temporaryPath,
            $Text,
            $Encoding)
        Move-Item -LiteralPath $temporaryPath `
            -Destination $Path `
            -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Add-LocalizationLine(
    [string]$Text,
    [string]$AnchorKey,
    [string]$NewKey,
    [string]$Value,
    [string]$NewLine
) {
    $existingPattern =
        "(?m)^$([Regex]::Escape($NewKey))`t.*$"
    $existing = [Regex]::Matches(
        $Text,
        $existingPattern)
    if ($existing.Count -gt 1) {
        throw "Duplicate localization key $NewKey."
    }

    $expectedLine = "$NewKey`t$Value"
    if ($existing.Count -eq 1) {
        if ($existing[0].Value -cne $expectedLine) {
            throw "Existing localization key $NewKey has unexpected text."
        }

        return $Text
    }

    $anchorPattern =
        "(?m)^$([Regex]::Escape($AnchorKey))`t.*$"
    $anchors = [Regex]::Matches($Text, $anchorPattern)
    if ($anchors.Count -ne 1) {
        throw "Expected one localization anchor $AnchorKey; found $($anchors.Count)."
    }

    return $Text.Insert(
        $anchors[0].Index + $anchors[0].Length,
        $NewLine + $expectedLine)
}

$clientProcesses = @(
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProcessName -in @(
                'Godswar',
                'Launch',
                'GodsWar',
                'Game'
            )
        }
)
if ($clientProcesses.Count -gt 0) {
    throw 'Close the GodsWar client and launcher before patching pet items.'
}

$resolvedRoot = [IO.Path]::GetFullPath($ClientRoot)
$resolvedBackup = [IO.Path]::GetFullPath($BackupDirectory)
if (!$resolvedBackup.StartsWith(
        [IO.Path]::GetFullPath('C:\Reborn\artifacts'),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BackupDirectory must be inside C:\Reborn\artifacts.'
}

New-Item -ItemType Directory `
    -Path $resolvedBackup `
    -Force |
    Out-Null

$patched = [Collections.Generic.List[string]]::new()
foreach ($locale in @('en_us', 'zh_cn')) {
    $localeRoot =
        Join-Path $resolvedRoot "Localization\$locale"
    $itemPath =
        Join-Path $localeRoot 'Settings\Sys\ItemBaseAttribute.xml'
    $namePath =
        Join-Path $localeRoot 'Text\EquipName.dat'
    $descriptionPath =
        Join-Path $localeRoot 'Text\EquipDescription.dat'
    foreach ($path in @(
        $itemPath,
        $namePath,
        $descriptionPath
    )) {
        if (!(Test-Path -LiteralPath $path)) {
            throw "Required client file was not found: $path"
        }
    }

    $localeBackup = Join-Path $resolvedBackup $locale
    New-Item -ItemType Directory `
        -Path $localeBackup `
        -Force |
        Out-Null
    foreach ($path in @(
        $itemPath,
        $namePath,
        $descriptionPath
    )) {
        $backupPath =
            Join-Path $localeBackup ([IO.Path]::GetFileName($path))
        if (!(Test-Path -LiteralPath $backupPath)) {
            Copy-Item -LiteralPath $path `
                -Destination $backupPath
        }
    }

    $item = Read-EncodedText $itemPath
    $newLine = if ($item.Text.Contains("`r`n")) {
        "`r`n"
    }
    else {
        "`n"
    }
    $itemPattern = "<$itemKey\b[^>]+/>"
    $existingItems = [Regex]::Matches(
        $item.Text,
        $itemPattern)
    if ($existingItems.Count -gt 1) {
        throw "Duplicate client item ID $itemId in $locale."
    }

    if ($existingItems.Count -eq 0) {
        $anchorPattern = '<Pet10145\b[^>]+/>'
        $anchors = [Regex]::Matches(
            $item.Text,
            $anchorPattern)
        if ($anchors.Count -ne 1) {
            throw "Expected one Pet10145 item anchor in $locale; found $($anchors.Count)."
        }

        $lineStart = $item.Text.LastIndexOf(
            "`n",
            [Math]::Max(0, $anchors[0].Index - 1))
        $lineStart = if ($lineStart -lt 0) {
            0
        }
        else {
            $lineStart + 1
        }
        $prefix = $item.Text.Substring(
            $lineStart,
            $anchors[0].Index - $lineStart)
        $lineFormatted = [string]::IsNullOrWhiteSpace($prefix)
        $indent = if ($lineFormatted) {
            $prefix
        }
        else {
            ''
        }
        $separator = if ($lineFormatted) {
            $newLine + $indent
        }
        else {
            ''
        }
        $newItem =
            "<$itemKey ID=`"$itemId`" Type=`"consume item`" " +
            "Texture=`"./Localization/$locale/UI/Texture/Icon.gwo`" " +
            "Icon=`"252,36`" Random=`"0`" Distribution=`"0,0`" " +
            "Money=`"0`" Overlap=`"99`" Use=`"1`" ItemType=`"22`" />"
        $item.Text = $item.Text.Insert(
            $anchors[0].Index + $anchors[0].Length,
            $separator + $newItem)
        Write-EncodedText `
            -Path $itemPath `
            -Text $item.Text `
            -Encoding $item.Encoding
    }

    $name = Read-EncodedText $namePath
    $nameNewLine = if ($name.Text.Contains("`r`n")) {
        "`r`n"
    }
    else {
        "`n"
    }
    $updatedName = Add-LocalizationLine `
        -Text $name.Text `
        -AnchorKey 'Pet11094' `
        -NewKey $itemKey `
        -Value $displayName `
        -NewLine $nameNewLine
    if ($updatedName -cne $name.Text) {
        Write-EncodedText `
            -Path $namePath `
            -Text $updatedName `
            -Encoding $name.Encoding
    }

    $itemDescription = Read-EncodedText $descriptionPath
    $descriptionNewLine =
        if ($itemDescription.Text.Contains("`r`n")) {
            "`r`n"
        }
        else {
            "`n"
        }
    $updatedDescription = Add-LocalizationLine `
        -Text $itemDescription.Text `
        -AnchorKey 'Pet11094' `
        -NewKey $itemKey `
        -Value $description `
        -NewLine $descriptionNewLine
    if ($updatedDescription -cne $itemDescription.Text) {
        Write-EncodedText `
            -Path $descriptionPath `
            -Text $updatedDescription `
            -Encoding $itemDescription.Encoding
    }

    [xml]$validation =
        (Read-EncodedText $itemPath).Text
    $itemNodes = @(
        $validation.SelectNodes("//*[@ID='$itemId']")
    )
    if ($itemNodes.Count -ne 1 -or
        $itemNodes[0].Name -cne $itemKey -or
        $itemNodes[0].Icon -cne '252,36') {
        throw "Client item $itemId validation failed for $locale."
    }

    $patched.Add($locale)
}

[pscustomobject]@{
    ItemId = $itemId
    Name = $displayName
    Locales = $patched -join ','
    BackupDirectory = $resolvedBackup
}
