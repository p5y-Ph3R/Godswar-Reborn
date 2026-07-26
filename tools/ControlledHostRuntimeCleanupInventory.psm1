Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

$script:MaximumEntries = 10000
$script:MaximumFileBytes = 128MB
$script:MaximumTotalBytes = 4GB
$script:MaximumDepth = 16

function Get-RuntimeCleanupInventory {
    param([Parameter(Mandatory)][string]$RuntimeRoot)

    $runtime = Assert-RebornDirectoryPath (
        [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\')
    ) 'runtime cleanup inventory root'
    $pending =
        [Collections.Generic.Queue[object]]::new()
    $pending.Enqueue([pscustomobject]@{
        Path = $runtime
        RelativePath = ''
        Depth = 0
    })
    $entries =
        [Collections.Generic.List[object]]::new()
    [Int64]$totalBytes = 0
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        $children = @(
            [IO.Directory]::EnumerateFileSystemEntries(
                [string]$directory.Path) |
                Sort-Object
        )
        foreach ($path in $children) {
            if ($entries.Count -ge $script:MaximumEntries) {
                throw 'Runtime cleanup inventory exceeds its entry budget.'
            }
            $name = [IO.Path]::GetFileName($path)
            $relative = if ([string]::IsNullOrEmpty(
                    [string]$directory.RelativePath)) {
                $name
            } else {
                [string]$directory.RelativePath + '\' + $name
            }
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Runtime cleanup inventory contains a reparse point.'
            }
            if (($attributes -band
                    [IO.FileAttributes]::Directory) -ne 0) {
                $depth = [int]$directory.Depth + 1
                if ($depth -gt $script:MaximumDepth) {
                    throw 'Runtime cleanup inventory exceeds its depth budget.'
                }
                $entries.Add([ordered]@{
                    relativePath = $relative
                    kind = 'Directory'
                    length = [Int64]0
                    sha256 = $null
                })
                $pending.Enqueue([pscustomobject]@{
                    Path = $path
                    RelativePath = $relative
                    Depth = $depth
                })
                continue
            }
            $file = Assert-RebornSingleLinkRegularFilePath `
                $path 'runtime cleanup inventory file'
            $length = [IO.FileInfo]::new($file).Length
            if ($length -gt $script:MaximumFileBytes) {
                throw 'Runtime cleanup inventory contains an oversized file.'
            }
            $totalBytes += $length
            if ($totalBytes -gt $script:MaximumTotalBytes) {
                throw 'Runtime cleanup inventory exceeds its byte budget.'
            }
            $entries.Add([ordered]@{
                relativePath = $relative
                kind = 'File'
                length = $length
                sha256 = (
                    Get-FileHash -LiteralPath $file -Algorithm SHA256
                ).Hash
            })
        }
    }
    return @($entries | Sort-Object {
        [string]$_.relativePath
    })
}

function Get-RuntimeCleanupInventorySha256 {
    param([Parameter(Mandatory)][object[]]$Entries)

    $lines = foreach ($entry in $Entries) {
        '{0}|{1}|{2}|{3}' -f
            [string]$entry.kind,
            [string]$entry.relativePath,
            ([Int64]$entry.length).ToString(
                [Globalization.CultureInfo]::InvariantCulture),
            [string]$entry.sha256
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
        ($lines -join "`n"))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $algorithm.ComputeHash($bytes)
        try {
            return ([BitConverter]::ToString(
                $digest)).Replace('-', '')
        }
        finally {
            [Array]::Clear($digest, 0, $digest.Length)
        }
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $algorithm.Dispose()
    }
}

Export-ModuleMember -Function @(
    'Get-RuntimeCleanupInventory',
    'Get-RuntimeCleanupInventorySha256'
)
