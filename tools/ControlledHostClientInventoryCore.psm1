Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

$script:MaximumInventoryFiles = 100000
$script:MaximumInventoryEntries = 100000
$script:MaximumInventoryDepth = 64
$script:MaximumWritableOutputBytes = 4GB
$script:MaximumWritableOutputFileBytes = 16MB
$script:MaximumProtectedBytes = 4GB
$script:MaximumProtectedFileBytes = 64MB
$script:WritableOutputRelativePaths = @(
    'Dump',
    'Localization\en_us\Settings\User',
    'Localization\zh_cn\Settings\User',
    'Log',
    'ScreensHot'
)
$script:WritableOutputFileRelativePaths = @(
    'patcher\patcher.log'
)
$script:ExecutableExtensions =
    [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @(
    '.ax', '.bat', '.cmd', '.com', '.cpl', '.dll', '.drv', '.efi',
    '.exe', '.hta', '.inf', '.jar', '.js', '.jse', '.lnk', '.msi',
    '.msp', '.mst', '.ocx', '.ps1', '.psd1', '.psm1', '.py', '.reg',
    '.scr', '.sh', '.sys', '.url', '.vb', '.vbe', '.vbs', '.wsf',
    '.wsh'
)) {
    [void]$script:ExecutableExtensions.Add($extension)
}

function Get-RebornControlledHostWritableOutputRelativePaths {
    return @($script:WritableOutputRelativePaths)
}

function Get-RebornControlledHostWritableOutputFileRelativePaths {
    return @($script:WritableOutputFileRelativePaths)
}

function Get-RebornControlledHostMaximumWritableOutputFileBytes {
    return [Int64]$script:MaximumWritableOutputFileBytes
}

function Test-RebornControlledHostWritableRelativePath {
    param([Parameter(Mandatory)][string]$RelativePath)

    foreach ($island in $script:WritableOutputRelativePaths) {
        if ($RelativePath.Equals(
                $island,
                [StringComparison]::OrdinalIgnoreCase) -or
            $RelativePath.StartsWith(
                $island + '\',
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Test-RebornControlledHostWritableOutputFileRelativePath {
    param([Parameter(Mandatory)][string]$RelativePath)

    return @($script:WritableOutputFileRelativePaths | Where-Object {
        $RelativePath.Equals(
            $_,
            [StringComparison]::OrdinalIgnoreCase)
    }).Count -eq 1
}

function Assert-RebornControlledHostSafeRelativePath {
    param([Parameter(Mandatory)][string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('/') -or
        $RelativePath.StartsWith('\') -or
        $RelativePath.EndsWith('\')) {
        throw "Client inventory relative path is invalid: $RelativePath"
    }
    foreach ($segment in $RelativePath.Split('\')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or
            $segment -eq '.' -or $segment -eq '..') {
            throw "Client inventory path segment is invalid: $RelativePath"
        }
    }
    return $RelativePath
}

function Test-RebornControlledHostFileHasMzHeader {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        if ($stream.Length -lt 2) {
            return $false
        }
        return $stream.ReadByte() -eq 0x4D -and
            $stream.ReadByte() -eq 0x5A
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-RebornControlledHostWritableFileIsDataOnly {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$RelativePath
    )

    if ($script:ExecutableExtensions.Contains(
            [IO.Path]::GetExtension($RelativePath)) -or
        (Test-RebornControlledHostFileHasMzHeader $Path)) {
        throw (
            'A writable client output directory contains executable or ' +
            "loadable content: $RelativePath")
    }
}

function Get-RebornControlledHostInventorySetSha256 {
    param(
        [Parameter(Mandatory)][object[]]$Files,
        [ValidateRange(0, 4294967296)]
        [Int64]$MaximumProtectedBytes =
            $script:MaximumProtectedBytes,
        [ValidateRange(0, 4294967296)]
        [Int64]$MaximumProtectedFileBytes =
            $script:MaximumProtectedFileBytes
    )

    $ordered =
        [Collections.Generic.SortedDictionary[string, object]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    [Int64]$protectedBytes = 0
    foreach ($file in $Files) {
        $relative = Assert-RebornControlledHostSafeRelativePath (
            [string]$file.RelativePath)
        $sha256 = ([string]$file.Sha256).ToUpperInvariant()
        $length = [Int64]$file.Length
        if ($sha256 -cnotmatch '^[0-9A-F]{64}$' -or $length -lt 0) {
            throw "Client inventory entry is invalid: $relative"
        }
        if ($length -gt $MaximumProtectedFileBytes) {
            throw (
                'Client inventory entry exceeds its bounded file size: ' +
                $relative)
        }
        if ($length -gt $MaximumProtectedBytes - $protectedBytes) {
            throw 'Client inventory exceeds its protected-byte budget.'
        }
        $protectedBytes += $length
        if ($ordered.ContainsKey($relative)) {
            throw "Client inventory path is duplicated: $relative"
        }
        $ordered.Add($relative, [pscustomobject]@{
            RelativePath = $relative
            Length = $length
            Sha256 = $sha256
        })
    }
    if ($ordered.Count -gt $script:MaximumInventoryFiles) {
        throw 'Client inventory exceeds its bounded file count.'
    }

    $stream = [IO.MemoryStream]::new()
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $digest = $null
    try {
        foreach ($entry in $ordered.Values) {
            $line =
                $entry.RelativePath + [char]0 +
                $entry.Length.ToString(
                    [Globalization.CultureInfo]::InvariantCulture) +
                [char]0 + $entry.Sha256 + [char]10
            $bytes = [Text.Encoding]::UTF8.GetBytes($line)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally {
                [Array]::Clear($bytes, 0, $bytes.Length)
            }
        }
        $digest = $algorithm.ComputeHash($stream.ToArray())
        $setSha256 =
            ([BitConverter]::ToString($digest)).Replace('-', '')
    }
    finally {
        if ($null -ne $digest) {
            [Array]::Clear($digest, 0, $digest.Length)
        }
        $algorithm.Dispose()
        $stream.Dispose()
    }
    return [pscustomobject]@{
        SetSha256 = $setSha256
        Files = @($ordered.Values)
        ProtectedBytes = $protectedBytes
    }
}

function Get-RebornControlledHostClientInventory {
    param(
        [Parameter(Mandatory)][string]$ClientRoot,
        [ValidateRange(1, 100000)]
        [int]$MaximumEntries = $script:MaximumInventoryEntries,
        [ValidateRange(1, 64)]
        [int]$MaximumDepth = $script:MaximumInventoryDepth,
        [ValidateRange(0, 4294967296)]
        [Int64]$MaximumWritableBytes =
            $script:MaximumWritableOutputBytes,
        [ValidateRange(0, 4294967296)]
        [Int64]$MaximumWritableFileBytes =
            $script:MaximumWritableOutputFileBytes,
        [ValidateRange(0, 4294967296)]
        [Int64]$MaximumProtectedBytes =
            $script:MaximumProtectedBytes,
        [ValidateRange(0, 4294967296)]
        [Int64]$MaximumProtectedFileBytes =
            $script:MaximumProtectedFileBytes,
        [IO.FileStream]$LockedOriginStream
    )

    $root = Assert-RebornDirectoryPath (
        [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
    ) 'controlled-host client inventory root'
    foreach ($relative in $script:WritableOutputRelativePaths) {
        Assert-RebornDirectoryPath (
            Join-Path $root $relative
        ) "controlled-host writable island $relative" | Out-Null
    }

    $files = [Collections.Generic.List[object]]::new()
    $directories =
        [Collections.Generic.Queue[object]]::new()
    $directories.Enqueue([pscustomobject]@{
        Directory = [IO.DirectoryInfo]::new($root)
        Depth = 0
    })
    $entryCount = 0
    [Int64]$writableBytes = 0
    [Int64]$protectedBytes = 0
    while ($directories.Count -ne 0) {
        $work = $directories.Dequeue()
        foreach ($entry in $work.Directory.EnumerateFileSystemInfos()) {
            $entryCount++
            if ($entryCount -gt $MaximumEntries) {
                throw 'Client tree exceeds its bounded filesystem entry count.'
            }
            if (($entry.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw (
                    'Controlled-host client contains a reparse point: ' +
                    $entry.FullName)
            }
            $relative = $entry.FullName.Substring($root.Length + 1)
            Assert-RebornControlledHostSafeRelativePath $relative |
                Out-Null
            if ($entry -is [IO.DirectoryInfo]) {
                $depth = $work.Depth + 1
                if ($depth -gt $MaximumDepth) {
                    throw 'Client tree exceeds its bounded directory depth.'
                }
                $directories.Enqueue([pscustomobject]@{
                    Directory = $entry
                    Depth = $depth
                })
                continue
            }

            $path = Assert-RebornSingleLinkRegularFilePath `
                $entry.FullName 'controlled-host client file'
            $writableDirectory =
                Test-RebornControlledHostWritableRelativePath $relative
            $writableFile =
                Test-RebornControlledHostWritableOutputFileRelativePath `
                    $relative
            if ($writableDirectory -or $writableFile) {
                if ($writableFile -and
                    [Int64]$entry.Length -gt $MaximumWritableFileBytes) {
                    throw (
                        'Exact writable client output exceeds its bounded ' +
                        "file size: $relative")
                }
                if ([Int64]$entry.Length -gt
                    $MaximumWritableBytes - $writableBytes) {
                    throw (
                        'Writable client output exceeds its bounded total ' +
                        'byte budget.')
                }
                $writableBytes += [Int64]$entry.Length
                Assert-RebornControlledHostWritableFileIsDataOnly `
                    $path $relative
                if ($writableDirectory) {
                    continue
                }
            }
            if ($files.Count -ge $script:MaximumInventoryFiles) {
                throw 'Client inventory exceeds its bounded file count.'
            }

            $before = Get-Item -LiteralPath $path -Force
            if (-not $writableFile -and [Int64]$before.Length -gt
                $MaximumProtectedFileBytes) {
                throw (
                    'Protected client file exceeds its bounded size: ' +
                    $relative)
            }
            if (-not $writableFile -and [Int64]$before.Length -gt
                $MaximumProtectedBytes - $protectedBytes) {
                throw 'Protected client files exceed their byte budget.'
            }
            if (-not $writableFile) {
                $protectedBytes += [Int64]$before.Length
            }
            $sha256 = if (
                $relative.Equals(
                    'Origin.exe',
                    [StringComparison]::OrdinalIgnoreCase) -and
                $null -ne $LockedOriginStream
            ) {
                $expectedOrigin =
                    [IO.Path]::GetFullPath((Join-Path $root 'Origin.exe'))
                if (-not $LockedOriginStream.CanRead -or
                    -not $LockedOriginStream.CanSeek -or
                    $LockedOriginStream.SafeFileHandle.IsClosed -or
                    -not ([IO.Path]::GetFullPath(
                        $LockedOriginStream.Name)).Equals(
                            $expectedOrigin,
                            [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'The controlled-host Origin lock is invalid.'
                }
                $position = $LockedOriginStream.Position
                $algorithm =
                    [Security.Cryptography.SHA256]::Create()
                $digest = $null
                try {
                    $LockedOriginStream.Position = 0
                    $digest =
                        $algorithm.ComputeHash($LockedOriginStream)
                    ([BitConverter]::ToString($digest)).Replace('-', '')
                }
                finally {
                    $LockedOriginStream.Position = $position
                    if ($null -ne $digest) {
                        [Array]::Clear($digest, 0, $digest.Length)
                    }
                    $algorithm.Dispose()
                }
            } else {
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            }
            $after = Get-Item -LiteralPath $path -Force
            if ($before.Length -ne $after.Length -or
                $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc) {
                throw "Client file changed while being inventoried: $relative"
            }
            $files.Add([pscustomobject]@{
                RelativePath = $relative
                Length = [Int64]$after.Length
                Sha256 = $sha256
            })
        }
    }
    $set = Get-RebornControlledHostInventorySetSha256 $files.ToArray()
    return [pscustomobject]@{
        ClientRoot = $root
        SetSha256 = $set.SetSha256
        Files = $set.Files
        ProtectedBytes = $protectedBytes
        WritableBytes = $writableBytes
        WritableOutputRelativePaths =
            Get-RebornControlledHostWritableOutputRelativePaths
        WritableOutputFileRelativePaths =
            Get-RebornControlledHostWritableOutputFileRelativePaths
    }
}

function Assert-RebornControlledHostInventoryEqual {
    param(
        [Parameter(Mandatory)][object]$Expected,
        [Parameter(Mandatory)][object]$Actual,
        [string]$Label = 'controlled-host client inventory'
    )

    $expectedSet =
        Get-RebornControlledHostInventorySetSha256 @($Expected.Files)
    $actualSet =
        Get-RebornControlledHostInventorySetSha256 @($Actual.Files)
    if ([string]$Expected.SetSha256 -cne $expectedSet.SetSha256 -or
        [string]$Actual.SetSha256 -cne $actualSet.SetSha256 -or
        $expectedSet.Files.Count -ne $actualSet.Files.Count) {
        throw "$Label does not match its exact protected file set."
    }
    if ($expectedSet.SetSha256 -ceq $actualSet.SetSha256) {
        return $true
    }

    $actualByPath =
        [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $actualSet.Files) {
        $actualByPath.Add([string]$file.RelativePath, $file)
    }
    foreach ($expectedFile in $expectedSet.Files) {
        $relative = [string]$expectedFile.RelativePath
        $actualFile = $null
        if (-not $actualByPath.TryGetValue(
                $relative,
                [ref]$actualFile)) {
            throw "$Label does not match its exact protected file set."
        }
        if (Test-RebornControlledHostWritableOutputFileRelativePath `
                $relative) {
            if ([Int64]$actualFile.Length -gt
                $script:MaximumWritableOutputFileBytes) {
                throw "$Label exact writable output exceeds its bound."
            }
            continue
        }
        if ([Int64]$expectedFile.Length -ne [Int64]$actualFile.Length -or
            [string]$expectedFile.Sha256 -cne
                [string]$actualFile.Sha256) {
            throw "$Label does not match its exact protected file set."
        }
    }
    return $true
}

Export-ModuleMember -Function @(
    'Get-RebornControlledHostWritableOutputRelativePaths',
    'Get-RebornControlledHostWritableOutputFileRelativePaths',
    'Get-RebornControlledHostMaximumWritableOutputFileBytes',
    'Test-RebornControlledHostWritableRelativePath',
    'Test-RebornControlledHostWritableOutputFileRelativePath',
    'Assert-RebornControlledHostSafeRelativePath',
    'Test-RebornControlledHostFileHasMzHeader',
    'Assert-RebornControlledHostWritableFileIsDataOnly',
    'Get-RebornControlledHostInventorySetSha256',
    'Get-RebornControlledHostClientInventory',
    'Assert-RebornControlledHostInventoryEqual'
)
