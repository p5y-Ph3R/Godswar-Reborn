Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkFileHandleSafety.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkHostsAcl.psm1'
)

function Get-RebornHostsFileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-RebornHostsByteSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($Bytes))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-RebornHostsStreamSha256 {
    param([Parameter(Mandatory)][IO.FileStream]$Stream)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    $hash = $null
    try {
        $Stream.Position = 0
        $hash = $algorithm.ComputeHash($Stream)
        return ([BitConverter]::ToString($hash)).Replace('-', '')
    }
    finally {
        if ($null -ne $hash) {
            [Array]::Clear($hash, 0, $hash.Length)
        }
        $algorithm.Dispose()
    }
}

function Write-RebornHostsBytesLocked {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][string[]]$AcceptedCurrentSha256
    )

    $resolved = Assert-RebornSingleLinkRegularFilePath `
        $Path 'hosts mutation target'
    $expectedOutput = Get-RebornHostsByteSha256 $Bytes
    $stream = [IO.File]::Open(
        $resolved,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        Assert-RebornSingleLinkFileStream `
            $stream 'hosts mutation target'
        $current = Get-RebornHostsStreamSha256 $stream
        if ($AcceptedCurrentSha256 -cnotcontains $current) {
            throw (
                'Hosts bytes changed before the exclusive mutation; ' +
                'refusing to overwrite them.')
        }
        if ($stream.Length -gt $Bytes.Length) {
            $stream.Position = 0
            $prefix = New-Object byte[] $Bytes.Length
            try {
                $read = 0
                while ($read -lt $prefix.Length) {
                    $count = $stream.Read(
                        $prefix,
                        $read,
                        $prefix.Length - $read)
                    if ($count -eq 0) {
                        throw 'Hosts prefix was truncated while verifying it.'
                    }
                    $read += $count
                }
                for ($index = 0; $index -lt $prefix.Length; $index++) {
                    if ($prefix[$index] -ne $Bytes[$index]) {
                        throw (
                            'Hosts truncation target is not an exact current ' +
                            'prefix; refusing mutation.')
                    }
                }
            }
            finally {
                [Array]::Clear($prefix, 0, $prefix.Length)
            }
            $stream.SetLength($Bytes.Length)
        } elseif ($stream.Length -lt $Bytes.Length) {
            $existingLength = [int]$stream.Length
            $prefix = New-Object byte[] $existingLength
            try {
                $stream.Position = 0
                $read = 0
                while ($read -lt $prefix.Length) {
                    $count = $stream.Read(
                        $prefix,
                        $read,
                        $prefix.Length - $read)
                    if ($count -eq 0) {
                        throw 'Hosts prefix was truncated while verifying it.'
                    }
                    $read += $count
                }
                for ($index = 0; $index -lt $prefix.Length; $index++) {
                    if ($prefix[$index] -ne $Bytes[$index]) {
                        throw (
                            'Hosts append target is not the intended exact ' +
                            'prefix; refusing mutation.')
                    }
                }
            }
            finally {
                [Array]::Clear($prefix, 0, $prefix.Length)
            }
            $stream.Position = $stream.Length
            $stream.Write(
                $Bytes,
                $existingLength,
                $Bytes.Length - $existingLength)
        }
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
    if ((Get-RebornHostsFileSha256 $resolved) -cne $expectedOutput) {
        throw 'Exclusive hosts mutation did not produce the intended bytes.'
    }
}

function Write-RebornHostsReceiptAtomic {
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Path
    )

    $resolved = Resolve-RebornCanonicalLocalPath $Path 'hosts receipt'
    $issuedReceipt = [IO.Path]::GetFullPath(
        (Join-Path $env:ProgramData (
            'RebornSecureNetworkBackups\' +
            'development-hosts\' +
            'development-hosts-receipt.json')))
    $production = $resolved.Equals(
        $issuedReceipt,
        [StringComparison]::OrdinalIgnoreCase)
    Assert-RebornDirectoryPath (
        Split-Path -Parent $resolved
    ) 'hosts receipt parent' | Out-Null
    if (Test-Path -LiteralPath $resolved) {
        Assert-RebornSingleLinkRegularFilePath `
            $resolved 'active hosts receipt' | Out-Null
    }

    $temporary = "$resolved.$([guid]::NewGuid().ToString('N')).tmp"
    $previous = "$resolved.previous"
    if (Test-Path -LiteralPath $previous -PathType Leaf) {
        Assert-RebornSingleLinkRegularFilePath `
            $previous 'tracked previous hosts receipt' | Out-Null
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            [IO.File]::Delete($previous)
        } else {
            [IO.File]::Move($previous, $resolved)
        }
    }
    try {
        $json = $Value | ConvertTo-Json -Depth 6
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
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
            [IO.File]::Replace($temporary, $resolved, $previous, $true)
        } else {
            [IO.File]::Move($temporary, $resolved)
        }
        if ($production) {
            Protect-RebornDevelopmentHostsArtifact `
                $resolved -File | Out-Null
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Assert-RebornSingleLinkRegularFilePath `
                $temporary 'hosts receipt cleanup file' | Out-Null
            [IO.File]::Delete($temporary)
        }
        if (
            (Test-Path -LiteralPath $resolved -PathType Leaf) -and
            (Test-Path -LiteralPath $previous -PathType Leaf)
        ) {
            Assert-RebornSingleLinkRegularFilePath `
                $previous 'tracked previous hosts receipt' | Out-Null
            [IO.File]::Delete($previous)
        }
    }
}

Export-ModuleMember -Function @(
    'Get-RebornHostsFileSha256',
    'Get-RebornHostsByteSha256',
    'Write-RebornHostsBytesLocked',
    'Write-RebornHostsReceiptAtomic'
)
