param(
    [string]$ClientRoot = "C:\Godswar Origin"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression

function Read-U16([byte[]]$bytes, [int]$offset) {
    return [BitConverter]::ToUInt16($bytes, $offset)
}

function Read-U32([byte[]]$bytes, [int]$offset) {
    return [BitConverter]::ToUInt32($bytes, $offset)
}

function Write-U16([System.IO.Stream]$stream, [int]$value) {
    $bytes = [BitConverter]::GetBytes([uint16]$value)
    $stream.Write($bytes, 0, $bytes.Length)
}

function Write-U32([System.IO.Stream]$stream, [uint32]$value) {
    $bytes = [BitConverter]::GetBytes($value)
    $stream.Write($bytes, 0, $bytes.Length)
}

function Expand-XofMszip([byte[]]$bytes) {
    $header = [Text.Encoding]::ASCII.GetString($bytes, 0, 16)
    if ($header -ne 'xof 0303bzip0032') {
        throw "Unsupported XOF header: $header"
    }

    $expectedLength = Read-U32 $bytes 16
    $cursor = 20
    $output = [System.IO.MemoryStream]::new()

    while ($cursor -lt $bytes.Length) {
        if ($cursor + 6 -gt $bytes.Length) {
            throw "Truncated chunk header at offset $cursor."
        }

        $uncompressedLength = Read-U16 $bytes $cursor
        $compressedLength = Read-U16 $bytes ($cursor + 2)
        $chunkStart = $cursor + 4
        if ($chunkStart + $compressedLength -gt $bytes.Length) {
            throw "Chunk length exceeds file size at offset $cursor."
        }

        if ($bytes[$chunkStart] -ne [byte][char]'C' -or $bytes[$chunkStart + 1] -ne [byte][char]'K') {
            throw "Missing MSZIP CK signature at offset $chunkStart."
        }

        $input = [System.IO.MemoryStream]::new($bytes, $chunkStart + 2, $compressedLength - 2)
        $deflate = [System.IO.Compression.DeflateStream]::new($input, [System.IO.Compression.CompressionMode]::Decompress)
        $chunk = [System.IO.MemoryStream]::new()
        $deflate.CopyTo($chunk)
        $deflate.Dispose()
        $input.Dispose()

        if ($chunk.Length -ne $uncompressedLength) {
            throw "Unexpected decompressed chunk size at offset $cursor. Expected $uncompressedLength, got $($chunk.Length)."
        }

        $chunkBytes = $chunk.ToArray()
        $output.Write($chunkBytes, 0, $chunkBytes.Length)
        $chunk.Dispose()

        $cursor += 4 + $compressedLength
    }

    if ($output.Length -ne $expectedLength -and ($output.Length + 16) -ne $expectedLength) {
        throw "Unexpected decompressed file size. Expected $expectedLength, got $($output.Length)."
    }

    return ,$output.ToArray()
}

function Compress-XofMszip([byte[]]$expanded, [uint32]$declaredLength) {
    $output = [System.IO.MemoryStream]::new()
    $header = [Text.Encoding]::ASCII.GetBytes('xof 0303bzip0032')
    $output.Write($header, 0, $header.Length)
    Write-U32 $output $declaredLength

    $offset = 0
    while ($offset -lt $expanded.Length) {
        $chunkLength = [Math]::Min(32768, $expanded.Length - $offset)
        $compressed = [System.IO.MemoryStream]::new()
        $deflate = [System.IO.Compression.DeflateStream]::new($compressed, [System.IO.Compression.CompressionLevel]::Optimal, $true)
        $deflate.Write($expanded, $offset, $chunkLength)
        $deflate.Dispose()

        $compressedBytes = $compressed.ToArray()
        $compressed.Dispose()
        $mszipLength = $compressedBytes.Length + 2
        if ($mszipLength -gt [uint16]::MaxValue) {
            throw "Compressed chunk is too large: $mszipLength"
        }

        Write-U16 $output $chunkLength
        Write-U16 $output $mszipLength
        $output.WriteByte([byte][char]'C')
        $output.WriteByte([byte][char]'K')
        $output.Write($compressedBytes, 0, $compressedBytes.Length)

        $offset += $chunkLength
    }

    return ,$output.ToArray()
}

function Replace-AsciiSameLength([byte[]]$bytes, [string]$from, [string]$to) {
    if ($from.Length -ne $to.Length) {
        throw "Replacement lengths must match: '$from' -> '$to'"
    }

    $fromBytes = [Text.Encoding]::ASCII.GetBytes($from)
    $toBytes = [Text.Encoding]::ASCII.GetBytes($to)
    $count = 0

    for ($i = 0; $i -le $bytes.Length - $fromBytes.Length; $i++) {
        $match = $true
        for ($j = 0; $j -lt $fromBytes.Length; $j++) {
            if ($bytes[$i + $j] -ne $fromBytes[$j]) {
                $match = $false
                break
            }
        }

        if (-not $match) {
            continue
        }

        [Array]::Copy($toBytes, 0, $bytes, $i, $toBytes.Length)
        $count++
        $i += $fromBytes.Length - 1
    }

    return $count
}

function Test-ByteArrayEqual([byte[]]$left, [byte[]]$right) {
    if ($left.Length -ne $right.Length) {
        return $false
    }

    for ($i = 0; $i -lt $left.Length; $i++) {
        if ($left[$i] -ne $right[$i]) {
            return $false
        }
    }

    return $true
}

function Patch-JcsFile([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $declaredLength = Read-U32 $bytes 16
    [byte[]]$expanded = Expand-XofMszip $bytes
    $replacementCount = 0
    $replacementCount += Replace-AsciiSameLength $expanded 'male_body_effect_0014' 'male_body_effect_0012'
    $replacementCount += Replace-AsciiSameLength $expanded 'female_body_effect_0014' 'female_body_effect_0012'
    $replacementCount += Replace-AsciiSameLength $expanded 'body_effect_0014' 'body_effect_0012'
    $replacementCount += Replace-AsciiSameLength $expanded '14.tga' '12.tga'

    if ($replacementCount -eq 0) {
        return [pscustomobject]@{
            Path = $path
            Replacements = 0
            Status = 'unchanged'
        }
    }

    $patched = Compress-XofMszip $expanded $declaredLength
    $roundTrip = Expand-XofMszip $patched
    if (-not (Test-ByteArrayEqual $expanded $roundTrip)) {
        throw "Round-trip validation failed for $path"
    }

    [IO.File]::WriteAllBytes($path, $patched)
    [pscustomobject]@{
        Path = $path
        Replacements = $replacementCount
        Status = 'patched'
    }
}

$targets = @(
    (Join-Path $ClientRoot 'Characters\effect'),
    (Join-Path $ClientRoot 'Characters_New\effect')
)

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path (Resolve-Path '.').Path "backups\armor-rank12-jcs-internal-refs-$timestamp"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

$patchedFiles = @()
foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target)) {
        throw "Effect folder not found: $target"
    }

    $backupDir = Join-Path $backupRoot (($target -replace '^[A-Z]:\\', '') -replace '[\\/:*?"<>| ]', '_')
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

    foreach ($file in Get-ChildItem -LiteralPath $target -Filter '*body_effect_0012*.jcs' -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $backupDir $file.Name) -Force
        $patchedFiles += Patch-JcsFile $file.FullName
    }

    $maleTexture = Join-Path $target 'male_body_effect_0012.tga'
    if (Test-Path -LiteralPath $maleTexture) {
        Copy-Item -LiteralPath $maleTexture -Destination (Join-Path $target '12.tga') -Force
    }
}

$patchedFiles
Write-Output "Backup: $backupRoot"
