Set-StrictMode -Version Latest

function Get-RebornManifestKeyFileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function ConvertFrom-RebornCoordinate {
    param([Parameter(Mandatory)][object]$Value, [string]$Label)

    if ($Value -isnot [string]) {
        throw "$Label must be base64 text."
    }
    try {
        $bytes = [Convert]::FromBase64String([string]$Value)
    }
    catch {
        throw "$Label is not valid base64."
    }
    if ($bytes.Length -ne 32) {
        throw "$Label must contain exactly 32 bytes."
    }
    return $bytes
}

function Get-RebornHeaderCoordinate {
    param([string]$Text, [string]$Name)

    $pattern = (
        'inline\s+constexpr\s+std::uint8_t\s+' +
        [regex]::Escape($Name) +
        '\[32\]\s*=\s*\{(?<values>.*?)\};')
    $match = [regex]::Match(
        $Text,
        $pattern,
        [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        throw "Generated header is missing $Name."
    }
    $hex = @([regex]::Matches(
        $match.Groups['values'].Value,
        '0x(?<value>[0-9A-Fa-f]{2})'))
    if ($hex.Count -ne 32) {
        throw "Generated header coordinate $Name is not 32 bytes."
    }
    $bytes = New-Object byte[] 32
    for ($index = 0; $index -lt 32; $index++) {
        $bytes[$index] = [Convert]::ToByte(
            $hex[$index].Groups['value'].Value, 16)
    }
    return $bytes
}

function Read-RebornManifestTrustBinding {
    param(
        [string]$Path,
        [string]$ExpectedKeyName,
        [string]$ExpectedKeyId,
        [string]$ExpectedPurpose
    )

    try {
        $trust = Get-Content -LiteralPath $Path -Raw -Encoding utf8 |
            ConvertFrom-Json
    }
    catch {
        throw "Manifest trust is not valid JSON: $Path"
    }
    if (
        $trust.schemaVersion -ne 1 -or
        $trust.keyId -cne $ExpectedKeyId -or
        $trust.environment -cne '1' -or
        $trust.minimumSequence -cne '1' -or
        $trust.cngKeyName -cne $ExpectedKeyName -or
        $trust.purpose -cne $ExpectedPurpose
    ) {
        throw "Manifest trust metadata is outside policy: $Path"
    }
    [pscustomobject]@{
        X = ConvertFrom-RebornCoordinate $trust.x "$Path x"
        Y = ConvertFrom-RebornCoordinate $trust.y "$Path y"
        Sha256 = Get-RebornManifestKeyFileSha256 $Path
    }
}

function Test-RebornBytesEqual {
    param([byte[]]$Left, [byte[]]$Right)

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

function Get-RebornManifestKeyArtifactBinding {
    param(
        [string]$HeaderPath,
        [string]$TrustPath,
        [string]$NextTrustPath,
        [string]$CurrentKeyName,
        [string]$NextKeyName
    )

    foreach ($path in @($HeaderPath, $TrustPath, $NextTrustPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Manifest key artifact is absent: $path"
        }
    }
    $current = Read-RebornManifestTrustBinding `
        $TrustPath $CurrentKeyName '53249' `
        'development-only endpoint manifest verification'
    $next = Read-RebornManifestTrustBinding `
        $NextTrustPath $NextKeyName '53250' `
        'development-only next endpoint manifest verification'
    $header = [IO.File]::ReadAllText(
        [IO.Path]::GetFullPath($HeaderPath),
        [Text.UTF8Encoding]::new($false, $true))
    $headerCurrentX = Get-RebornHeaderCoordinate $header 'CurrentX'
    $headerCurrentY = Get-RebornHeaderCoordinate $header 'CurrentY'
    $headerNextX = Get-RebornHeaderCoordinate $header 'NextX'
    $headerNextY = Get-RebornHeaderCoordinate $header 'NextY'
    try {
        if (
            -not (Test-RebornBytesEqual $current.X $headerCurrentX) -or
            -not (Test-RebornBytesEqual $current.Y $headerCurrentY) -or
            -not (Test-RebornBytesEqual $next.X $headerNextX) -or
            -not (Test-RebornBytesEqual $next.Y $headerNextY)
        ) {
            throw 'Manifest trust coordinates do not match the client header.'
        }
        return [pscustomobject]@{
            CurrentX = [Convert]::ToBase64String($current.X)
            CurrentY = [Convert]::ToBase64String($current.Y)
            NextX = [Convert]::ToBase64String($next.X)
            NextY = [Convert]::ToBase64String($next.Y)
            HeaderSha256 =
                Get-RebornManifestKeyFileSha256 $HeaderPath
            CurrentTrustSha256 = $current.Sha256
            NextTrustSha256 = $next.Sha256
        }
    }
    finally {
        foreach ($bytes in @(
            $current.X, $current.Y, $next.X, $next.Y,
            $headerCurrentX, $headerCurrentY, $headerNextX, $headerNextY
        )) {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
    }
}

function New-RebornManifestKeyReceiptRecord {
    param(
        [object]$Artifacts,
        [object]$CurrentKey,
        [object]$NextKey
    )

    foreach ($entry in @(
        @($CurrentKey, $Artifacts.CurrentX, $Artifacts.CurrentY),
        @($NextKey, $Artifacts.NextX, $Artifacts.NextY)
    )) {
        Assert-RebornManifestKeyDescriptor `
            $entry[0] $entry[0].Name $entry[1] $entry[2]
    }
    [ordered]@{
        schemaVersion = 1
        state = 'Issued'
        current = [ordered]@{
            keyName = $CurrentKey.Name
            algorithm = 'ECDSA_P256'
            keyUsage = 'Signing'
            exportPolicy = 'None'
            x = $Artifacts.CurrentX
            y = $Artifacts.CurrentY
            removed = $false
        }
        next = [ordered]@{
            keyName = $NextKey.Name
            algorithm = 'ECDSA_P256'
            keyUsage = 'Signing'
            exportPolicy = 'None'
            x = $Artifacts.NextX
            y = $Artifacts.NextY
            removed = $false
        }
        headerSha256 = $Artifacts.HeaderSha256
        currentTrustSha256 = $Artifacts.CurrentTrustSha256
        nextTrustSha256 = $Artifacts.NextTrustSha256
        issuedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        removalStartedUtc = $null
        removedUtc = $null
    }
}

function Assert-RebornManifestKeyDescriptor {
    param(
        [object]$Descriptor,
        [string]$ExpectedName,
        [string]$ExpectedX,
        [string]$ExpectedY
    )

    if (
        $Descriptor.Name -cne $ExpectedName -or
        $Descriptor.Algorithm -cne 'ECDSA_P256' -or
        $Descriptor.KeyUsage -cne 'Signing' -or
        $Descriptor.ExportPolicy -cne 'None' -or
        $Descriptor.X -cne $ExpectedX -or
        $Descriptor.Y -cne $ExpectedY
    ) {
        throw (
            "Manifest key $ExpectedName does not match its exact " +
            'non-exportable ECDSA P-256 signing authority.')
    }
}

function Write-RebornManifestKeyReceiptAtomic {
    param([object]$Record, [string]$Path, [switch]$NoOverwrite)

    $resolved = [IO.Path]::GetFullPath($Path)
    if ($NoOverwrite -and (Test-Path -LiteralPath $resolved)) {
        throw 'Manifest key receipt already exists; refusing overwrite.'
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $resolved)) |
        Out-Null
    $temporary = "$resolved.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
            ($Record | ConvertTo-Json -Depth 6))
        try {
            $stream = [IO.FileStream]::new(
                $temporary,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush($true)
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            $previous = "$resolved.previous"
            [IO.File]::Replace($temporary, $resolved, $previous, $true)
            if (Test-Path -LiteralPath $previous) {
                [IO.File]::Delete($previous)
            }
        } else {
            [IO.File]::Move($temporary, $resolved)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            [IO.File]::Delete($temporary)
        }
    }
}

function Read-RebornManifestKeyReceipt {
    param(
        [string]$Path,
        [object]$Artifacts,
        [string]$CurrentKeyName,
        [string]$NextKeyName
    )

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.Length -lt 128 -or $item.Length -gt 16384) {
        throw 'Manifest key receipt size is outside policy.'
    }
    try {
        $record = Get-Content -LiteralPath $item.FullName -Raw |
            ConvertFrom-Json
    }
    catch {
        throw 'Manifest key receipt is not valid JSON.'
    }
    if (
        $record.schemaVersion -ne 1 -or
        $record.state -notin @('Issued', 'RemovalPending', 'Removed') -or
        $record.current.keyName -cne $CurrentKeyName -or
        $record.next.keyName -cne $NextKeyName -or
        $record.current.removed -isnot [bool] -or
        $record.next.removed -isnot [bool] -or
        $record.headerSha256 -cne $Artifacts.HeaderSha256 -or
        $record.currentTrustSha256 -cne
            $Artifacts.CurrentTrustSha256 -or
        $record.nextTrustSha256 -cne $Artifacts.NextTrustSha256 -or
        ($record.state -eq 'Issued' -and (
                $record.current.removed -or
                $record.next.removed)) -or
        ($record.state -eq 'Removed' -and (
                -not $record.current.removed -or
                -not $record.next.removed))
    ) {
        throw 'Manifest key receipt does not match issued artifacts.'
    }
    foreach ($entry in @(
        @($record.current, $Artifacts.CurrentX, $Artifacts.CurrentY),
        @($record.next, $Artifacts.NextX, $Artifacts.NextY)
    )) {
        if (
            $entry[0].algorithm -cne 'ECDSA_P256' -or
            $entry[0].keyUsage -cne 'Signing' -or
            $entry[0].exportPolicy -cne 'None' -or
            $entry[0].x -cne $entry[1] -or
            $entry[0].y -cne $entry[2]
        ) {
            throw 'Manifest key receipt cryptographic binding is invalid.'
        }
    }
    return [pscustomobject]@{
        Path = $item.FullName
        Record = $record
    }
}

Export-ModuleMember -Function @(
    'Get-RebornManifestKeyArtifactBinding',
    'New-RebornManifestKeyReceiptRecord',
    'Assert-RebornManifestKeyDescriptor',
    'Write-RebornManifestKeyReceiptAtomic',
    'Read-RebornManifestKeyReceipt'
)
