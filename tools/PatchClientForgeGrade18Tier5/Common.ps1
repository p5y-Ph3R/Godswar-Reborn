function Get-AttributeValue([string]$Element, [string]$Name) {
    $match = [regex]::Match($Element, "(?<=\s)$([regex]::Escape($Name))=`"([^`"]*)`"")
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

function Set-AttributeValue([string]$Element, [string]$Name, [string]$Value) {
    $pattern = "(?<=\s)$([regex]::Escape($Name))=`"[^`"]*`""
    $match = [regex]::Match($Element, $pattern)
    if (-not $match.Success) { throw "Required attribute '$Name' is missing." }
    return $Element.Substring(0, $match.Index) + "$Name=`"$Value`"" + $Element.Substring($match.Index + $match.Length)
}

function Split-Values([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return @() }
    return @($Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
}

function Format-Decimal([decimal]$Value) {
    return $Value.ToString('0.############', $invariant)
}

function Decode-Utf8([string]$Base64) {
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

function Get-ClientRelativePath([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup source is outside the client root: $fullPath"
    }
    return $fullPath.Substring($rootPath.Length)
}
