function Get-ClientRelativePath([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup source is outside the client root: $fullPath"
    }
    return $fullPath.Substring($rootPath.Length)
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
            throw "Origin.exe prerequisite '$Name' mismatch at 0x$('{0:X}' -f ($Offset + $index))."
        }
    }
}
