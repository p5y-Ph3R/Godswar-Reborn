[CmdletBinding()]
param(
    [ValidateSet('Development', 'B20H', 'Status')]
    [string]$Target = 'Status',
    [string]$ClientRoot = 'C:\Godswar Origin',
    [switch]$Launch,
    [switch]$AllowMutation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$launcherAddresses = @{
    B20H = '127.1.1.110'
    Development = '127.1.1.111'
}
$launcherHashes = @{
    B20H = '15E39CAB15178E3610E359253F51132468DADAFEF4BCAFC79C38812D32B6A93C'
    Development = '33E35DA3D744A87353A59E68E420649955BDCCD1AB200D20130FEE6C3417D7DA'
}
$launcherAddressOffsets = @(0x3BF00, 0x3BF2C, 0x3BF40)

function Test-BytesAtOffset {
    param(
        [byte[]]$Bytes,
        [int]$Offset,
        [byte[]]$Expected
    )

    if ($Offset -lt 0 -or $Offset + $Expected.Length -gt $Bytes.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Bytes[$Offset + $index] -ne $Expected[$index]) {
            return $false
        }
    }
    return $true
}

function Get-LauncherTarget {
    param([string]$Path)

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $bytes = [IO.File]::ReadAllBytes($Path)
    foreach ($candidate in @('B20H', 'Development')) {
        if ($hash -cne $launcherHashes[$candidate]) {
            continue
        }

        $expected = [Text.Encoding]::ASCII.GetBytes(
            $launcherAddresses[$candidate])
        $matches = $true
        foreach ($offset in $launcherAddressOffsets) {
            if (-not (Test-BytesAtOffset $bytes $offset $expected)) {
                $matches = $false
                break
            }
        }
        if ($matches) {
            return $candidate
        }
    }
    return 'Unknown'
}

function Get-ConfigTarget {
    param([string]$Ip)

    if ($Ip -ceq $launcherAddresses.Development) {
        return 'Development'
    }
    if ($Ip -ceq $launcherAddresses.B20H) {
        return 'B20H'
    }
    return 'Unknown'
}

$resolvedClient = [IO.Path]::GetFullPath($ClientRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar)
$configPath = Join-Path $resolvedClient 'config.ini'
$launcherPath = Join-Path $resolvedClient 'Launch.exe'
foreach ($path in @($configPath, $launcherPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required client file is missing: $path"
    }
}

$raw = Get-Content -LiteralPath $configPath -Raw
$currentIpMatch = [regex]::Match(
    $raw,
    '(?im)^\s*IP\s*=\s*([^\r\n]+)\s*$')
$currentPortMatch = [regex]::Match(
    $raw,
    '(?im)^\s*PORT\s*=\s*([^\r\n]+)\s*$')
if (-not $currentIpMatch.Success) {
    throw 'Client config.ini has no SERVER IP value.'
}
$currentIp = $currentIpMatch.Groups[1].Value.Trim()
$currentPort = if ($currentPortMatch.Success) {
    $currentPortMatch.Groups[1].Value.Trim()
} else {
    '5998'
}
$configTarget = Get-ConfigTarget $currentIp
$launcherTarget = Get-LauncherTarget $launcherPath

if ($Target -eq 'Status') {
    [pscustomobject]@{
        Target = if ($configTarget -ceq $launcherTarget) {
            $configTarget
        } else {
            'Mixed'
        }
        ConfigTarget = $configTarget
        LauncherTarget = $launcherTarget
        Ip = $currentIp
        Port = $currentPort
        Config = $configPath
        Launcher = $launcherPath
        LauncherSha256 =
            (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash
    }
    return
}

if (-not $AllowMutation) {
    throw 'Changing the installed client target requires -AllowMutation.'
}
if ($launcherTarget -eq 'Unknown') {
    throw 'Launch.exe is not a recognized pristine or development-patched build. Refusing to modify it.'
}

$clientPrefix = $resolvedClient + [IO.Path]::DirectorySeparatorChar
$runningClientProcesses = @(Get-Process -Name @(
        'Launch', 'Origin', 'Patcher') -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            if ([string]::IsNullOrWhiteSpace($_.Path)) {
                return $true
            }
            return [IO.Path]::GetFullPath($_.Path).StartsWith(
                $clientPrefix,
                [StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $true
        }
    })
if ($runningClientProcesses.Count -gt 0) {
    $runningNames = ($runningClientProcesses |
        ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', '
    throw "Close this client before changing its endpoint: $runningNames"
}

$targetIp = $launcherAddresses[$Target]
$targetPort = '5998'
if ($Target -eq 'Development') {
    foreach ($port in @(5998, 7000)) {
        if (-not (Test-NetConnection $targetIp -Port $port `
                -InformationLevel Quiet -WarningAction SilentlyContinue)) {
            throw "Development endpoint $targetIp`:$port is unreachable."
        }
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$backupDirectory = Join-Path `
    $repositoryRoot `
    ('artifacts\development-stack\client-config-backups\' +
        [DateTimeOffset]::UtcNow.UtcDateTime.ToString('yyyyMMddTHHmmssfffZ'))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$configBackupPath = Join-Path $backupDirectory 'config.ini'
$launcherBackupPath = Join-Path $backupDirectory 'Launch.exe'
Copy-Item -LiteralPath $configPath -Destination $configBackupPath
Copy-Item -LiteralPath $launcherPath -Destination $launcherBackupPath
foreach ($pair in @(
        @($configPath, $configBackupPath),
        @($launcherPath, $launcherBackupPath))) {
    if ((Get-FileHash $pair[0] -Algorithm SHA256).Hash -cne
        (Get-FileHash $pair[1] -Algorithm SHA256).Hash) {
        throw "Client backup verification failed for $($pair[0])."
    }
}

$configTemporaryPath =
    "$configPath.$([Guid]::NewGuid().ToString('N')).tmp"
$configReplacementBackup =
    "$configPath.$([Guid]::NewGuid().ToString('N')).replace.bak"
$launcherTemporaryPath =
    "$launcherPath.$([Guid]::NewGuid().ToString('N')).tmp"
$launcherReplacementBackup =
    "$launcherPath.$([Guid]::NewGuid().ToString('N')).replace.bak"

try {
    if ($launcherTarget -cne $Target) {
        $launcherBytes = [IO.File]::ReadAllBytes($launcherPath)
        $currentAddressBytes = [Text.Encoding]::ASCII.GetBytes(
            $launcherAddresses[$launcherTarget])
        $targetAddressBytes = [Text.Encoding]::ASCII.GetBytes($targetIp)
        foreach ($offset in $launcherAddressOffsets) {
            if (-not (Test-BytesAtOffset `
                    $launcherBytes $offset $currentAddressBytes)) {
                throw ('Launch.exe endpoint bytes do not match the ' +
                    "recognized $launcherTarget build at 0x$($offset.ToString('X')).")
            }
            [Array]::Copy(
                $targetAddressBytes,
                0,
                $launcherBytes,
                $offset,
                $targetAddressBytes.Length)
        }

        [IO.File]::WriteAllBytes($launcherTemporaryPath, $launcherBytes)
        $temporaryLauncherHash =
            (Get-FileHash $launcherTemporaryPath -Algorithm SHA256).Hash
        if ($temporaryLauncherHash -cne $launcherHashes[$Target]) {
            throw 'The patched Launch.exe hash did not match the reviewed build.'
        }
        [IO.File]::Replace(
            $launcherTemporaryPath,
            $launcherPath,
            $launcherReplacementBackup)
    }

    $updated = [regex]::Replace(
        $raw,
        '(?im)^(\s*IP\s*=\s*)[^\r\n]+$',
        "`${1}$targetIp")
    if ($currentPortMatch.Success) {
        $updated = [regex]::Replace(
            $updated,
            '(?im)^(\s*PORT\s*=\s*)[^\r\n]+$',
            "`${1}$targetPort")
    }
    else {
        $updated = $updated.TrimEnd("`r", "`n") +
            [Environment]::NewLine + "PORT=$targetPort" +
            [Environment]::NewLine
    }
    [IO.File]::WriteAllText(
        $configTemporaryPath,
        $updated,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::Replace(
        $configTemporaryPath,
        $configPath,
        $configReplacementBackup)

    $verifyConfig = Get-Content -LiteralPath $configPath -Raw
    if ($verifyConfig -notmatch
            "(?im)^\s*IP\s*=\s*$([regex]::Escape($targetIp))\s*$" -or
        $verifyConfig -notmatch
            "(?im)^\s*PORT\s*=\s*$targetPort\s*$" -or
        (Get-LauncherTarget $launcherPath) -cne $Target) {
        throw 'The installed client endpoint did not pass final verification.'
    }
}
catch {
    Copy-Item -LiteralPath $configBackupPath -Destination $configPath -Force
    Copy-Item -LiteralPath $launcherBackupPath -Destination $launcherPath -Force
    throw "Client target update failed and both backups were restored: $($_.Exception.Message)"
}
finally {
    foreach ($temporaryFile in @(
            $configTemporaryPath,
            $configReplacementBackup,
            $launcherTemporaryPath,
            $launcherReplacementBackup)) {
        if (Test-Path -LiteralPath $temporaryFile) {
            Remove-Item -LiteralPath $temporaryFile -Force
        }
    }
}

[pscustomobject]@{
    Status = 'target_updated'
    Target = $Target
    Ip = $targetIp
    Port = $targetPort
    Config = $configPath
    Launcher = $launcherPath
    LauncherSha256 = $launcherHashes[$Target]
    BackupDirectory = $backupDirectory
}
if ($Launch) {
    Start-Process `
        -FilePath $launcherPath `
        -WorkingDirectory $resolvedClient
}
