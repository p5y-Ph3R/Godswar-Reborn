Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

$script:ManagedReleaseFiles = @(
    'Godswar.Server.deps.json',
    'Godswar.Server.dll',
    'Godswar.Server.exe',
    'Godswar.Server.pdb',
    'Godswar.Server.runtimeconfig.json',
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
    'Microsoft.Extensions.Logging.Abstractions.dll',
    'Npgsql.dll'
)

function Get-RebornControlledHostManagedReleaseSet {
    param([Parameter(Mandatory)][string]$ReleaseDirectory)

    $release = Assert-RebornDirectoryPath (
        [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\')
    ) 'controlled-host managed release'
    $entries = @(Get-ChildItem -LiteralPath $release -Force)
    if ($entries.Count -ne $script:ManagedReleaseFiles.Count) {
        throw (
            'The controlled-host managed release must contain exactly ' +
            "$($script:ManagedReleaseFiles.Count) reviewed files.")
    }
    foreach ($entry in $entries) {
        if ($entry.PSIsContainer -or
            $script:ManagedReleaseFiles -cnotcontains $entry.Name) {
            throw (
                'The controlled-host managed release contains an unexpected ' +
                "entry: $($entry.Name)")
        }
    }

    $files = foreach ($name in $script:ManagedReleaseFiles) {
        $path = Assert-RebornSingleLinkRegularFilePath (
            Join-Path $release $name
        ) "controlled-host managed release file $name"
        [pscustomobject]@{
            Name = $name
            Path = $path
            Length = (Get-Item -LiteralPath $path -Force).Length
            Sha256 = (
                Get-FileHash -LiteralPath $path -Algorithm SHA256
            ).Hash
        }
    }

    $stream = [IO.MemoryStream]::new()
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($file in $files) {
            $nameBytes = [Text.Encoding]::UTF8.GetBytes($file.Name)
            $digestBytes = [Text.Encoding]::ASCII.GetBytes($file.Sha256)
            try {
                $stream.Write($nameBytes, 0, $nameBytes.Length)
                $stream.WriteByte(0)
                $stream.Write($digestBytes, 0, $digestBytes.Length)
                $stream.WriteByte(10)
            }
            finally {
                [Array]::Clear($nameBytes, 0, $nameBytes.Length)
                [Array]::Clear($digestBytes, 0, $digestBytes.Length)
            }
        }
        $setDigest = $hash.ComputeHash($stream.ToArray())
        try {
            $setSha256 = (
                [BitConverter]::ToString($setDigest)
            ).Replace('-', '')
        }
        finally {
            [Array]::Clear($setDigest, 0, $setDigest.Length)
        }
    }
    finally {
        $hash.Dispose()
        $stream.Dispose()
    }

    [pscustomobject]@{
        ReleaseDirectory = $release
        SetSha256 = $setSha256
        Files = @($files)
    }
}

Export-ModuleMember -Function @(
    'Get-RebornControlledHostManagedReleaseSet'
)
