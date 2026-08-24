[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply', 'Restore')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$ShimPath,

    [string]$BackupRoot,

    [string]$ApplyBackupPath
)

$ErrorActionPreference = 'Stop'

$supportedOriginHashes = @(
    'E0F5BC951C6E37550F4D9CC1E25BFDCB4F020466ADD854DC2E7EA04E0D22F81C',
    'C80FC15418BC1865731105AE05CE96DA3015FEC9E8E51337263D1C475301EEEE'
)
$supportedLegacyHash = `
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
$installerVersion = '1.2.0'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ShimPath) {
    $ShimPath = Join-Path $repoRoot `
        'client\network-shim\bin\Release\Win32\Net.dll'
}
if (-not $BackupRoot) {
    $BackupRoot = Join-Path $repoRoot 'backups'
}

function Resolve-SafeDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($resolved).TrimEnd('\')
    if (-not $resolved -or
        $resolved.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label cannot be a filesystem root: $resolved"
    }

    return $resolved
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-JsonAtomically {
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Path
    )

    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $Value | ConvertTo-Json -Depth 8
        $utf8WithoutBom = New-Object Text.UTF8Encoding($false)
        [IO.File]::WriteAllText($temporary, $json, $utf8WithoutBom)
        [IO.File]::Move($temporary, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Copy-And-Verify {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$ExpectedHash
    )

    Copy-Item -LiteralPath $Source -Destination $Destination
    $actualHash = Get-Sha256 $Destination
    if ($actualHash -ne $ExpectedHash) {
        Remove-Item -LiteralPath $Destination -Force
        throw "Staged hash mismatch for $Destination"
    }
}

function Get-InstallState {
    param(
        [Parameter(Mandatory)][string]$NetPath,
        [Parameter(Mandatory)][string]$LegacyPath,
        [Parameter(Mandatory)][string]$ExpectedShimHash
    )

    $netHash = if (Test-Path -LiteralPath $NetPath -PathType Leaf) {
        Get-Sha256 $NetPath
    } else {
        $null
    }
    $legacyHash = if (Test-Path -LiteralPath $LegacyPath -PathType Leaf) {
        Get-Sha256 $LegacyPath
    } else {
        $null
    }

    $state = if ($netHash -eq $supportedLegacyHash -and -not $legacyHash) {
        'Stock'
    } elseif (
        $netHash -eq $supportedLegacyHash -and
        $legacyHash -eq $supportedLegacyHash
    ) {
        'RecoverablePartial'
    } elseif (
        $netHash -eq $ExpectedShimHash -and
        $legacyHash -eq $supportedLegacyHash
    ) {
        'InstalledExact'
    } else {
        'UnknownRefused'
    }

    return [pscustomobject]@{
        State = $state
        NetSha256 = $netHash
        LegacySha256 = $legacyHash
    }
}

function Assert-ClientClosed {
    $running = Get-Process -Name Origin -ErrorAction SilentlyContinue
    if ($running) {
        throw 'Origin.exe must be closed before applying or restoring the shim.'
    }
}

$clientDirectory = Resolve-SafeDirectory $ClientRoot 'ClientRoot'
if (-not (Test-Path -LiteralPath $clientDirectory -PathType Container)) {
    throw "Client directory not found: $clientDirectory"
}

$originPath = Join-Path $clientDirectory 'Origin.exe'
$netPath = Join-Path $clientDirectory 'Net.dll'
$legacyPath = Join-Path $clientDirectory 'NetLegacy.dll'
foreach ($requiredPath in @($originPath, $netPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required client file not found: $requiredPath"
    }
}

$originHash = Get-Sha256 $originPath
if ($originHash -notin $supportedOriginHashes) {
    throw "Unsupported Origin.exe hash: $originHash"
}

$applyBackup = $null
$applyManifest = $null
$applyNetPath = $null
$resolvedShimPath = $null

if ($Mode -eq 'Restore') {
    if (-not $ApplyBackupPath) {
        throw 'Restore requires the explicit -ApplyBackupPath created by Apply.'
    }

    $applyBackup = Resolve-SafeDirectory `
        $ApplyBackupPath `
        'ApplyBackupPath'
    $applyManifestPath = Join-Path $applyBackup 'manifest.json'
    $applyNetPath = Join-Path $applyBackup 'Net.dll'
    foreach ($requiredPath in @($applyManifestPath, $applyNetPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Apply backup is incomplete: $requiredPath"
        }
    }

    $applyManifest = Get-Content -LiteralPath $applyManifestPath -Raw |
        ConvertFrom-Json
    $expectedShimHash = [string]$applyManifest.after.netSha256
    if ($applyManifest.mode -ne 'Apply' -or
        $applyManifest.originSha256 -ne $originHash -or
        $expectedShimHash -notmatch '^[0-9A-F]{64}$' -or
        (Get-Sha256 $applyNetPath) -ne $supportedLegacyHash) {
        throw 'Apply backup manifest or stock Net.dll hash is invalid.'
    }
} else {
    $resolvedShimPath = [IO.Path]::GetFullPath($ShimPath)
    if (-not (Test-Path -LiteralPath $resolvedShimPath -PathType Leaf)) {
        if ($Mode -eq 'Status') {
            $expectedShimHash = '<not-built>'
        } else {
            throw "Built shim not found: $resolvedShimPath"
        }
    } else {
        $expectedShimHash = Get-Sha256 $resolvedShimPath
    }
}

$state = Get-InstallState $netPath $legacyPath $expectedShimHash
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        State = $state.State
        ClientRoot = $clientDirectory
        OriginSha256 = $originHash
        NetSha256 = $state.NetSha256
        NetLegacySha256 = $state.LegacySha256
        ExpectedShimSha256 = $expectedShimHash
    }
    return
}

Assert-ClientClosed

if ($Mode -eq 'Apply') {
    if ($state.State -eq 'InstalledExact') {
        Write-Host 'Network shim is already installed exactly; no files changed.'
        return
    }
    if ($state.State -notin @('Stock', 'RecoverablePartial')) {
        throw "Apply refused from state: $($state.State)"
    }

    & (Join-Path $PSScriptRoot 'TestClientNetworkShim.ps1') `
        -LegacyDllPath $netPath `
        -CandidateShimPath $resolvedShimPath `
        -SkipBuild | Out-Host

    $backupDirectory = Join-Path (
        Resolve-SafeDirectory $BackupRoot 'BackupRoot'
    ) (
        'client-network-shim-v1-Apply-' +
        (Get-Date -Format 'yyyyMMdd-HHmmssfff')
    )

    if (-not $PSCmdlet.ShouldProcess(
            $clientDirectory,
            "Install verified x86 Net.dll shim; backup: $backupDirectory")) {
        return
    }

    Assert-ClientClosed
    New-Item -ItemType Directory -Path $backupDirectory | Out-Null
    $backupNet = Join-Path $backupDirectory 'Net.dll'
    Copy-And-Verify $netPath $backupNet $supportedLegacyHash

    $manifest = [ordered]@{
        schemaVersion = 1
        installerVersion = $installerVersion
        mode = 'Apply'
        createdUtc = [DateTime]::UtcNow.ToString('O')
        clientRoot = $clientDirectory
        originSha256 = $originHash
        before = [ordered]@{
            state = $state.State
            netSha256 = $state.NetSha256
            netLegacySha256 = $state.LegacySha256
        }
        after = [ordered]@{
            netSha256 = $expectedShimHash
            netLegacySha256 = $supportedLegacyHash
        }
        files = @(
            [ordered]@{
                path = 'Net.dll'
                backup = 'Net.dll'
                length = (Get-Item -LiteralPath $backupNet).Length
                sha256 = $supportedLegacyHash
            }
        )
    }
    Write-JsonAtomically $manifest (
        Join-Path $backupDirectory 'manifest.json'
    )

    $createdLegacy = $false
    $legacyStage = "$legacyPath.$([guid]::NewGuid().ToString('N')).tmp"
    $shimStage = "$netPath.$([guid]::NewGuid().ToString('N')).tmp"
    $replaceBackup = "$netPath.replace.$([guid]::NewGuid().ToString('N')).tmp"
    $rollbackSwap = "$netPath.rollback.$([guid]::NewGuid().ToString('N')).tmp"
    $rollbackRestoreStage = `
        "$netPath.restore.$([guid]::NewGuid().ToString('N')).tmp"

    try {
        if ($state.State -eq 'Stock') {
            Copy-And-Verify $netPath $legacyStage $supportedLegacyHash
            [IO.File]::Move($legacyStage, $legacyPath)
            $createdLegacy = $true
        }

        Copy-And-Verify $resolvedShimPath $shimStage $expectedShimHash
        Assert-ClientClosed
        [IO.File]::Replace(
            $shimStage,
            $netPath,
            $replaceBackup,
            $true)

        $postState = Get-InstallState `
            $netPath `
            $legacyPath `
            $expectedShimHash
        if ($postState.State -ne 'InstalledExact') {
            throw "Post-install state is $($postState.State), not InstalledExact."
        }
    }
    catch {
        if ((Test-Path -LiteralPath $netPath -PathType Leaf) -and
            (Get-Sha256 $netPath) -ne $supportedLegacyHash) {
            Copy-And-Verify `
                $backupNet `
                $rollbackRestoreStage `
                $supportedLegacyHash
            [IO.File]::Replace(
                $rollbackRestoreStage,
                $netPath,
                $rollbackSwap,
                $true)
        }
        if ($createdLegacy -and
            (Test-Path -LiteralPath $legacyPath -PathType Leaf)) {
            Remove-Item -LiteralPath $legacyPath -Force
        }
        throw
    }
    finally {
        foreach ($temporary in @(
            $legacyStage,
            $shimStage,
            $replaceBackup,
            $rollbackSwap,
            $rollbackRestoreStage
        )) {
            if (Test-Path -LiteralPath $temporary -PathType Leaf) {
                Remove-Item -LiteralPath $temporary -Force
            }
        }
    }

    Write-Host "InstalledExact. Apply backup: $backupDirectory"
    return
}

if ($state.State -eq 'Stock') {
    Write-Host 'Stock Net.dll is already restored; no files changed.'
    return
}
if ($state.State -notin @('InstalledExact', 'RecoverablePartial')) {
    throw "Restore refused from state: $($state.State)"
}

$revertDirectory = Join-Path (
    Resolve-SafeDirectory $BackupRoot 'BackupRoot'
) (
    'client-network-shim-v1-Revert-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff')
)

if (-not $PSCmdlet.ShouldProcess(
        $clientDirectory,
        "Restore stock Net.dll from $applyBackup; preserve current files in $revertDirectory")) {
    return
}

Assert-ClientClosed
New-Item -ItemType Directory -Path $revertDirectory | Out-Null
$revertNet = Join-Path $revertDirectory 'Net.dll'
$revertLegacy = Join-Path $revertDirectory 'NetLegacy.dll'
$preservedNetHash = if ($state.State -eq 'InstalledExact') {
    $expectedShimHash
} else {
    $supportedLegacyHash
}
Copy-And-Verify $netPath $revertNet $preservedNetHash
Copy-And-Verify $legacyPath $revertLegacy $supportedLegacyHash

Write-JsonAtomically ([ordered]@{
    schemaVersion = 1
    installerVersion = $installerVersion
    mode = 'Restore'
    createdUtc = [DateTime]::UtcNow.ToString('O')
    clientRoot = $clientDirectory
    originSha256 = $originHash
    applyBackup = $applyBackup
    interruptedState = $state.State
    preservedNetSha256 = $preservedNetHash
    preservedLegacySha256 = $supportedLegacyHash
}) (Join-Path $revertDirectory 'manifest.json')

$restoreStage = "$netPath.$([guid]::NewGuid().ToString('N')).tmp"
$restoreReplaceBackup = `
    "$netPath.replace.$([guid]::NewGuid().ToString('N')).tmp"
try {
    if ($state.State -eq 'InstalledExact') {
        Copy-And-Verify $applyNetPath $restoreStage $supportedLegacyHash
        Assert-ClientClosed
        [IO.File]::Replace(
            $restoreStage,
            $netPath,
            $restoreReplaceBackup,
            $true)
    }

    # The exact legacy copy is already preserved in the Revert backup.
    Assert-ClientClosed
    Remove-Item -LiteralPath $legacyPath -Force

    $postState = Get-InstallState `
        $netPath `
        $legacyPath `
        $expectedShimHash
    if ($postState.State -ne 'Stock') {
        throw "Post-restore state is $($postState.State), not Stock."
    }
}
finally {
    foreach ($temporary in @($restoreStage, $restoreReplaceBackup)) {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

Write-Host "Stock state restored. Revert backup: $revertDirectory"
