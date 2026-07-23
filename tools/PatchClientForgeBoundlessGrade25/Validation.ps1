function Get-ClientRelativePath([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith(
            $rootPath,
            [StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Backup source is outside the client root: $fullPath"
    }
    return $fullPath.Substring($rootPath.Length)
}

function Assert-BinaryContext([byte[]]$Bytes, [hashtable]$Site) {
    if ($Site.Offset -lt $Site.Prefix.Count -or
        $Site.Offset + $Site.Suffix.Count -ge $Bytes.Count) {
        throw "Origin.exe site '$($Site.Name)' is outside the file."
    }
    for ($index = 0; $index -lt $Site.Prefix.Count; $index++) {
        if ($Bytes[$Site.Offset - $Site.Prefix.Count + $index] -ne
            $Site.Prefix[$index]) {
            throw "Origin.exe prefix mismatch at $($Site.Name)."
        }
    }
    for ($index = 0; $index -lt $Site.Suffix.Count; $index++) {
        if ($Bytes[$Site.Offset + 1 + $index] -ne $Site.Suffix[$index]) {
            throw "Origin.exe suffix mismatch at $($Site.Name)."
        }
    }
    if ($Site.Allowed -notcontains $Bytes[$Site.Offset]) {
        throw "Origin.exe byte mismatch at $($Site.Name): got 0x$(
            '{0:X2}' -f $Bytes[$Site.Offset]
        )."
    }
}

function Assert-ExactBytes(
    [byte[]]$Bytes,
    [int]$Offset,
    [byte[]]$Expected,
    [string]$Name
) {
    if ($Offset -lt 0 -or $Offset + $Expected.Count -gt $Bytes.Count) {
        throw "Origin.exe prerequisite '$Name' is outside the file."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($Bytes[$Offset + $index] -ne $Expected[$index]) {
            throw "Origin.exe prerequisite '$Name' mismatch at 0x$(
                '{0:X}' -f ($Offset + $index)
            )."
        }
    }
}

function Assert-ItemAppendAttributePrerequisite(
    [string]$Path,
    [string]$Locale
) {
    [xml]$document = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    $nodes = @($document.SelectNodes('/ItemAppendAttribute/*[@ID]'))
    if ($nodes.Count -lt 193) {
        throw "ItemAppendAttribute $Locale has only $($nodes.Count) rows."
    }
    foreach ($node in $nodes) {
        for ($level = 1; $level -le 25; $level++) {
            if (-not $node.HasAttribute("L$level")) {
                throw "ItemAppendAttribute $Locale ID $($node.ID) lacks L$level."
            }
        }
    }
}

function Assert-LocalizationKeys(
    [string]$Text,
    [string]$Locale,
    [string]$Label
) {
    foreach ($key in @('MaterialBase6', 'MaterialAppend6', 'MaterialOdds5')) {
        $matches = [regex]::Matches(
            $Text,
            ('(?m)^{0}\t[^\r\n]*(?=\r?$)' -f [regex]::Escape($key))
        )
        if ($matches.Count -ne 1) {
            throw "$Label $Locale must contain exactly one '$key' key; found $($matches.Count)."
        }
    }
}
