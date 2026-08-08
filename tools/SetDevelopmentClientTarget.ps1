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
    throw 'Client config.ini has no unique SERVER IP value.'
}
$currentIp = $currentIpMatch.Groups[1].Value.Trim()
$currentPort = if ($currentPortMatch.Success) {
    $currentPortMatch.Groups[1].Value.Trim()
} else {
    '5998'
}

if ($Target -eq 'Status') {
    [pscustomobject]@{
        Target = if ($currentIp -ceq '127.1.1.111') {
            'Development'
        } elseif ($currentIp -ceq '127.1.1.110') {
            'B20H'
        } else {
            'Unknown'
        }
        Ip = $currentIp
        Port = $currentPort
        Config = $configPath
        Launcher = $launcherPath
    }
    return
}

if (-not $AllowMutation) {
    throw 'Changing the installed client target requires -AllowMutation.'
}
$targetIp = if ($Target -eq 'Development') {
    '127.1.1.111'
} else {
    '127.1.1.110'
}
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
$backupPath = Join-Path $backupDirectory 'config.ini'
Copy-Item -LiteralPath $configPath -Destination $backupPath
if ((Get-FileHash $backupPath -Algorithm SHA256).Hash -cne
    (Get-FileHash $configPath -Algorithm SHA256).Hash) {
    throw 'Client endpoint backup verification failed.'
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
$temporaryPath = "$configPath.$([Guid]::NewGuid().ToString('N')).tmp"
$replacementBackup =
    "$configPath.$([Guid]::NewGuid().ToString('N')).replace.bak"
try {
    [IO.File]::WriteAllText(
        $temporaryPath,
        $updated,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::Replace($temporaryPath, $configPath, $replacementBackup)
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
    if (Test-Path -LiteralPath $replacementBackup) {
        Remove-Item -LiteralPath $replacementBackup -Force
    }
}

$verify = Get-Content -LiteralPath $configPath -Raw
if ($verify -notmatch "(?im)^\s*IP\s*=\s*$([regex]::Escape($targetIp))\s*$" -or
    $verify -notmatch "(?im)^\s*PORT\s*=\s*$targetPort\s*$") {
    Copy-Item -LiteralPath $backupPath -Destination $configPath -Force
    throw 'Client target verification failed; the backup was restored.'
}

$result = [pscustomobject]@{
    Status = 'target_updated'
    Target = $Target
    Ip = $targetIp
    Port = $targetPort
    Config = $configPath
    Backup = $backupPath
    Launcher = $launcherPath
}
$result
if ($Launch) {
    Start-Process `
        -FilePath $launcherPath `
        -WorkingDirectory $resolvedClient
}
