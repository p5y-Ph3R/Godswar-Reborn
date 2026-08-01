function ConvertFrom-B20JsonInteger {
    param(
        [object]$Value,
        [string]$Context
    )

    Assert-B20Condition (
        $Value -is [int] -or $Value -is [long]) (
        "$Context must be a JSON integer.")
    return [long]$Value
}

function Assert-B20JsonTrue {
    param(
        [object]$Value,
        [string]$Context
    )

    Assert-B20Condition (
        $Value -is [bool] -and $Value) (
        "$Context must be the JSON Boolean true.")
}

function ConvertFrom-B20UtcTimestamp {
    param(
        [object]$Value,
        [string]$Context
    )

    Assert-B20Condition (
        $Value -is [string] -and
        $Value -cmatch (
            '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}' +
            '(?:\.\d{1,7})?Z$')) (
        "$Context must be an RFC 3339 UTC string.")
    $formats = [string[]]@(
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'")
    $parsed = [DateTimeOffset]::MinValue
    $valid = [DateTimeOffset]::TryParseExact(
        $Value,
        $formats,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal -bor
            [Globalization.DateTimeStyles]::AdjustToUniversal,
        [ref]$parsed)
    Assert-B20Condition $valid "$Context is not a valid timestamp."
    return $parsed
}

function Assert-B20NoDuplicateJsonProperties {
    param([Parameter(Mandatory)][string]$Json)

    $tokenPattern =
        '"(?:\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4})|[^"\\\x00-\x1F])*"' +
        '|[{}\[\]:,]' +
        '|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?' +
        '|true|false|null'
    $objects = [Collections.Generic.Stack[
        Collections.Generic.HashSet[string]]]::new()
    $previous = $null
    foreach ($match in [regex]::Matches(
        $Json,
        $tokenPattern,
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
        $token = $match.Value
        if ($token -ceq '{') {
            $objects.Push(
                [Collections.Generic.HashSet[string]]::new(
                    [StringComparer]::Ordinal))
        }
        elseif ($token -ceq '}') {
            if ($objects.Count -gt 0) {
                $null = $objects.Pop()
            }
        }
        elseif (
            $token -ceq ':' -and
            $objects.Count -gt 0 -and
            $null -ne $previous -and
            $previous.StartsWith('"', [StringComparison]::Ordinal)) {
            $name = $previous | ConvertFrom-Json
            Assert-B20Condition ($objects.Peek().Add($name)) (
                "Duplicate JSON property '$name' is not allowed.")
        }
        $previous = $token
    }
}

function Assert-B20NoReparsePoints {
    param(
        [string]$Root,
        [string]$Candidate,
        [string]$Context
    )

    $cursor = $Candidate
    while ($true) {
        $item = Get-Item -LiteralPath $cursor
        Assert-B20Condition (
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) (
            "$Context must not traverse a reparse point.")
        if ($cursor -ceq $Root) {
            break
        }
        $cursor = Split-Path -Parent $cursor
        Assert-B20Condition (-not [string]::IsNullOrWhiteSpace($cursor)) (
            "$Context escaped its evidence root.")
    }
}
