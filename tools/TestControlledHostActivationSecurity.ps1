[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientActivation.psm1'
) -Force
foreach ($moduleName in @(
    'ControlledHostProcessEnvironment.psm1',
    'ControlledHostClientInventoryReceipt.psm1',
    'ControlledHostClientRootLease.psm1',
    'ControlledHostRunnerIdentity.psm1',
    'ControlledHostRuntimeLock.psm1',
    'ControlledHostServerRuntime.psm1',
    'SecureNetworkOperationLock.psm1',
    'ControlledHostClientInventoryEpoch.psm1',
    'ControlledHostClientInventoryCore.psm1'
)) {
    Import-Module (Join-Path $PSScriptRoot $moduleName) -Force
}

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Label
    )
    if (-not $Condition) {
        throw "Assertion failed: $Label"
    }
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Label
    )
    $accepted = $true
    try {
        & $Action | Out-Null
    }
    catch {
        $accepted = $false
    }
    if ($accepted) {
        throw "Unsafe controlled-host case was accepted: $Label"
    }
}

$stockOriginHash = 'A' * 64
$candidateOriginHash = 'B' * 64
$otherOriginHash = 'C' * 64
$originReceipt = [pscustomobject]@{
    Inventory = [pscustomobject]@{
        Files = @([pscustomobject]@{
            RelativePath = 'Origin.exe'
            Sha256 = $stockOriginHash
        })
    }
}
$originBinding =
    Assert-RebornControlledHostActivationOriginBinding `
        $originReceipt `
        $candidateOriginHash `
        $candidateOriginHash `
        $stockOriginHash
Assert-True `
    ($originBinding.Paired -and
        $originBinding.StockOriginSha256 -ceq $stockOriginHash -and
        $originBinding.LiveOriginSha256 -ceq $candidateOriginHash) `
    'paired activation binds stock inventory and live Origin separately'
Assert-Rejected {
    Assert-RebornControlledHostActivationOriginBinding `
        $originReceipt $candidateOriginHash `
        $candidateOriginHash $otherOriginHash
} 'paired activation with a wrong stock Origin'
Assert-Rejected {
    Assert-RebornControlledHostActivationOriginBinding `
        $originReceipt $candidateOriginHash `
        $otherOriginHash $stockOriginHash
} 'paired activation with a wrong live Origin'

function ConvertTo-EncodedCommand {
    param([Parameter(Mandatory)][string]$Script)
    return [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($Script))
}

function ConvertTo-SingleQuotedLiteral {
    param([Parameter(Mandatory)][string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-ChildPowerShell {
    param([Parameter(Mandatory)][string]$Script)

    $powershell = Join-Path $PSHOME 'powershell.exe'
    $output = @(
        & $powershell `
            -NoLogo `
            -NoProfile `
            -NonInteractive `
            -EncodedCommand (ConvertTo-EncodedCommand $Script) 2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        throw (
            "Child PowerShell failed with $LASTEXITCODE`: " +
            ($output -join [Environment]::NewLine))
    }
    return @($output | ForEach-Object { [string]$_ })
}

$currentSid = Assert-RebornControlledHostRunnerIdentity
Assert-True `
    ($currentSid -ceq
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) `
    'current non-elevated runner identity'
Assert-Rejected {
    Assert-RebornControlledHostRunnerIdentityState `
        $true $false $currentSid
} 'elevated secure runner'
Assert-Rejected {
    Assert-RebornControlledHostRunnerIdentityState `
        $false $true 'S-1-5-18'
} 'SYSTEM secure runner'

$runtimeSecurity = New-RebornControlledHostRuntimeSecurity
$runtimeRules = @(
    $runtimeSecurity.GetAccessRules(
        $true,
        $false,
        [Security.Principal.SecurityIdentifier]) |
        Where-Object {
            $_.IdentityReference.Value -ceq $currentSid
        }
)
Assert-True `
    ($runtimeRules.Count -eq 1 -and
     $runtimeRules[0].AccessControlType -eq
        [Security.AccessControl.AccessControlType]::Allow -and
     ($runtimeRules[0].FileSystemRights -band
        [Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq
        [Security.AccessControl.FileSystemRights]::ReadAndExecute -and
     ($runtimeRules[0].FileSystemRights -band
        ([Security.AccessControl.FileSystemRights]::Write -bor
         [Security.AccessControl.FileSystemRights]::Delete -bor
         [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
         [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
         [Security.AccessControl.FileSystemRights]::TakeOwnership)) -eq 0) `
    'runtime ACL grants the issued user read/execute without mutation'

$unsafeNames = @(
    'CORECLR_ENABLE_PROFILING',
    'cor_profiler',
    'COMPlus_ReadyToRun',
    'DOTNET_STARTUP_HOOKS',
    'dotnet_additional_deps',
    'DOTNET_SHARED_STORE',
    'DOTNET_DiagnosticPorts',
    'DOTNET_DefaultDiagnosticPortSuspend',
    'DOTNET_EnableDiagnostics_Profiler',
    'DOTNET_ROOT',
    'DOTNET_ROOT_X64',
    'DOTNET_ROLL_FORWARD_TO_PRERELEASE',
    'DOTNET_ALTJIT',
    'DOTNET_JITNAME_0'
)
foreach ($name in $unsafeNames) {
    Assert-True `
        (Test-RebornControlledHostUnsafeEnvironmentName $name) `
        "unsafe environment family $name"
}
Assert-True `
    (-not (Test-RebornControlledHostUnsafeEnvironmentName `
        'DOTNET_CLI_TELEMETRY_OPTOUT')) `
    'benign DOTNET CLI environment'

$start = [Diagnostics.ProcessStartInfo]::new()
$start.EnvironmentVariables['CORECLR_ENABLE_PROFILING'] = '1'
$start.EnvironmentVariables['DOTNET_STARTUP_HOOKS'] = 'unsafe.dll'
$start.EnvironmentVariables['REBORN_SAFE_MARKER'] = 'keep'
Set-RebornControlledHostSanitizedChildEnvironment $start |
    Out-Null
Assert-True `
    (-not $start.EnvironmentVariables.ContainsKey(
        'CORECLR_ENABLE_PROFILING')) `
    'CORECLR child sanitization'
Assert-True `
    (-not $start.EnvironmentVariables.ContainsKey(
        'DOTNET_STARTUP_HOOKS')) `
    'DOTNET child sanitization'
Assert-True `
    ($start.EnvironmentVariables['DOTNET_EnableDiagnostics'] -ceq '0') `
    'DOTNET diagnostics disabled'
Assert-True `
    ($start.EnvironmentVariables['COMPlus_EnableDiagnostics'] -ceq '0') `
    'COMPlus diagnostics disabled'
Assert-True `
    ($start.EnvironmentVariables['REBORN_SAFE_MARKER'] -ceq 'keep') `
    'unrelated child environment preserved'

$unknownGodswar = 'GODSWAR_UNREVIEWED_ACTIVATION_TEST'
$previousGodswar = [Environment]::GetEnvironmentVariable(
    $unknownGodswar,
    [EnvironmentVariableTarget]::Process)
try {
    [Environment]::SetEnvironmentVariable(
        $unknownGodswar,
        'unsafe',
        [EnvironmentVariableTarget]::Process)
    Assert-Rejected {
        Assert-RebornControlledHostNoUnreviewedGodswarEnvironment @(
            'GODSWAR_SECURE_ENABLED'
        )
    } 'unreviewed GODSWAR environment'
}
finally {
    [Environment]::SetEnvironmentVariable(
        $unknownGodswar,
        $previousGodswar,
        [EnvironmentVariableTarget]::Process)
}

$issued = [DateTimeOffset]::new(
    2026, 7, 26, 12, 0, 0, [TimeSpan]::Zero)
$newer = $issued.AddTicks(1)
$acceptedReboot = Test-RebornControlledHostPostInventoryReboot `
    $issued.ToString('O') { $newer }
Assert-True `
    ($acceptedReboot.LastBootUpTimeUtc -gt
        $acceptedReboot.InventoryCreatedUtc) `
    'strictly newer reboot'
Assert-Rejected {
    Test-RebornControlledHostPostInventoryReboot `
        $issued.ToString('O') { $issued }
} 'boot equal to inventory issue time'
Assert-Rejected {
    Test-RebornControlledHostPostInventoryReboot `
        $issued.ToString('O') { $issued.AddTicks(-1) }
} 'boot older than inventory issue time'
Assert-Rejected {
    Test-RebornControlledHostPostInventoryReboot `
        'not-a-timestamp' { $newer }
} 'malformed inventory issue time'
Assert-Rejected {
    Test-RebornControlledHostPostInventoryReboot `
        '2026-07-26T12:00:00.0000000+12:00' { $newer }
} 'non-UTC inventory issue time'
$ambiguous = [DateTime]::SpecifyKind(
    $newer.UtcDateTime,
    [DateTimeKind]::Unspecified)
Assert-Rejected {
    Test-RebornControlledHostPostInventoryReboot `
        $issued.ToString('O') { $ambiguous }
} 'ambiguous operating-system boot time'
Assert-Rejected {
    Test-RebornControlledHostPostInventoryReboot `
        $issued.ToString('O') {}
} 'missing operating-system boot time'
Assert-Rejected {
    Test-RebornControlledHostPostInventoryReboot `
        $issued.ToString('O') { $newer; $newer.AddTicks(1) }
} 'multiple operating-system boot times'

$oldEpochId = 'a' * 32
$newEpochId = 'b' * 32
$inventorySet = 'C' * 64
$oldReceiptSha256 = 'D' * 64
$newReceiptSha256 = 'E' * 64
$oldReceiptPath =
    "C:\ProgramData\client-stock-inventory-$inventorySet-$oldEpochId.json"
$newReceiptPath =
    "C:\ProgramData\client-stock-inventory-$inventorySet-$newEpochId.json"
$oldEpoch = [pscustomobject]@{
    Record = [pscustomobject]@{
        state = 'Active'
        epochId = $oldEpochId
        receiptFile = [IO.Path]::GetFileName($oldReceiptPath)
        receiptSha256 = $oldReceiptSha256
        inventorySetSha256 = $inventorySet
    }
}
Assert-RebornControlledHostInventoryEpochBinding `
    $oldEpoch `
    $oldReceiptPath `
    $oldReceiptSha256 `
    $inventorySet `
    $oldEpochId | Out-Null
$newEpoch = [pscustomobject]@{
    Record = [pscustomobject]@{
        state = 'Active'
        epochId = $newEpochId
        receiptFile = [IO.Path]::GetFileName($newReceiptPath)
        receiptSha256 = $newReceiptSha256
        inventorySetSha256 = $inventorySet
    }
}
Assert-Rejected {
    Assert-RebornControlledHostInventoryEpochBinding `
        $newEpoch `
        $oldReceiptPath `
        $oldReceiptSha256 `
        $inventorySet `
        $oldEpochId
} 'stale receipt replay after same-content re-hardening'
Assert-RebornControlledHostInventoryEpochBinding `
    $newEpoch `
    $newReceiptPath `
    $newReceiptSha256 `
    $inventorySet `
    $newEpochId | Out-Null
$pendingEpoch = [pscustomobject]@{
    Record = [pscustomobject]@{
        state = 'PendingHardening'
        epochId = $newEpochId
        receiptFile = $null
        receiptSha256 = $null
        inventorySetSha256 = $null
    }
}
Assert-Rejected {
    Assert-RebornControlledHostInventoryEpochBinding `
        $pendingEpoch `
        $newReceiptPath `
        $newReceiptSha256 `
        $inventorySet `
        $newEpochId
} 'pending hardening epoch activation'

$shaA = 'A' * 64
$shaB = 'B' * 64
$boundary = Get-RebornControlledHostInventorySetSha256 @(
    [pscustomobject]@{
        RelativePath = 'one.bin'
        Length = 10
        Sha256 = $shaA
    },
    [pscustomobject]@{
        RelativePath = 'two.bin'
        Length = 10
        Sha256 = $shaB
    }
) -MaximumProtectedBytes 20 -MaximumProtectedFileBytes 10
Assert-True `
    ($boundary.ProtectedBytes -eq 20) `
    'exact protected-byte and per-file boundary'
Assert-Rejected {
    Get-RebornControlledHostInventorySetSha256 @(
        [pscustomobject]@{
            RelativePath = 'one.bin'
            Length = 10
            Sha256 = $shaA
        },
        [pscustomobject]@{
            RelativePath = 'two.bin'
            Length = 11
            Sha256 = $shaB
        }
    ) -MaximumProtectedBytes 20 -MaximumProtectedFileBytes 11
} 'protected total bytes above bound'
Assert-Rejected {
    Get-RebornControlledHostInventorySetSha256 @(
        [pscustomobject]@{
            RelativePath = 'one.bin'
            Length = 11
            Sha256 = $shaA
        }
    ) -MaximumProtectedBytes 20 -MaximumProtectedFileBytes 10
} 'protected file bytes above bound'

$installerSource = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'InstallSecureNetworkBundle.ps1'
) -Raw
$statusIndex = $installerSource.IndexOf(
    'if ($Mode -eq ''Status'')',
    [StringComparison]::Ordinal)
$applyIndex = $installerSource.IndexOf(
    'if ($Mode -eq ''Apply'')',
    [StringComparison]::Ordinal)
$firstShouldProcess = $installerSource.IndexOf(
    'if (-not $PSCmdlet.ShouldProcess(',
    $applyIndex,
    [StringComparison]::Ordinal)
$firstOperationLock = $installerSource.IndexOf(
    'Enter-RebornSecureNetworkOperationLock -Name ''secure-bundle''',
    [StringComparison]::Ordinal)
$restoreIndex = $installerSource.IndexOf(
    'Restore requires -ApplyBackupPath',
    [StringComparison]::Ordinal)
$restoreShouldProcess = $installerSource.IndexOf(
    'if (-not $PSCmdlet.ShouldProcess(',
    $restoreIndex,
    [StringComparison]::Ordinal)
$secondOperationLock = $installerSource.IndexOf(
    'Enter-RebornSecureNetworkOperationLock -Name ''secure-bundle''',
    $firstOperationLock + 1,
    [StringComparison]::Ordinal)
Assert-True `
    ($statusIndex -ge 0 -and
        $statusIndex -lt $firstOperationLock) `
    'Status remains before every mutating operation-lock acquisition'
Assert-True `
    ($applyIndex -ge 0 -and
        $firstShouldProcess -gt $applyIndex -and
        $firstOperationLock -gt $firstShouldProcess) `
    'Apply WhatIf returns before operation-lock mutation'
Assert-True `
    ($restoreIndex -ge 0 -and
        $restoreShouldProcess -gt $restoreIndex -and
        $secondOperationLock -gt $restoreShouldProcess) `
    'Restore WhatIf returns before operation-lock mutation'

$runnerSource = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'RunControlledHostSecureServer.ps1'
) -Raw
$identityIndex = $runnerSource.IndexOf(
    'Assert-RebornControlledHostRunnerIdentity | Out-Null',
    [StringComparison]::Ordinal)
$hostsLeaseIndex = $runnerSource.IndexOf(
    'Enter-RebornDevelopmentHostsRuntimeLease',
    [StringComparison]::Ordinal)
Assert-True `
    ($identityIndex -ge 0 -and
     $hostsLeaseIndex -gt $identityIndex -and
     $runnerSource.IndexOf(
        'Enter-RebornSecureNetworkOperationLock',
        [StringComparison]::Ordinal) -eq -1) `
    'runner refuses elevation before read-only leases and never writes locks'

$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) "reborn-activation-security-$([guid]::NewGuid().ToString('N'))"
$clientFixture = Join-Path $temporaryRoot 'client'
$leasedDirectory = Join-Path $temporaryRoot 'leased'
$renamedDirectory = Join-Path $temporaryRoot 'renamed'
$operationLockRoot = Join-Path $temporaryRoot 'locks'
try {
    [IO.Directory]::CreateDirectory($clientFixture) | Out-Null
    foreach ($relative in (
        Get-RebornControlledHostWritableOutputRelativePaths
    )) {
        [IO.Directory]::CreateDirectory(
            (Join-Path $clientFixture $relative)) | Out-Null
    }
    $sparse = Join-Path $clientFixture 'oversized-sparse.bin'
    $stream = [IO.File]::Open(
        $sparse,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $stream.SetLength(64MB + 1)
    }
    finally {
        $stream.Dispose()
    }
    Assert-Rejected {
        Get-RebornControlledHostClientInventory $clientFixture
    } 'sparse protected file over default per-file bound'
    [IO.File]::Delete($sparse)

    $originFixture = Join-Path $clientFixture 'Origin.exe'
    [IO.File]::WriteAllBytes(
        $originFixture,
        [byte[]](0x4D, 0x5A, 0x01, 0x02))
    $expectedInventory =
        Get-RebornControlledHostClientInventory $clientFixture
    $originLock = [IO.File]::Open(
        $originFixture,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    try {
        Assert-Rejected {
            Get-RebornControlledHostClientInventory $clientFixture 2>$null
        } 'inventory reopening an exclusively locked Origin'
        $lockedInventory =
            Get-RebornControlledHostClientInventory `
                $clientFixture `
                -LockedOriginStream $originLock
        Assert-True `
            ($lockedInventory.SetSha256 -ceq
                $expectedInventory.SetSha256) `
            'inventory hashes Origin through its exclusive mutation lock'
    }
    finally {
        $originLock.Dispose()
    }
    [IO.File]::Delete($originFixture)

    [IO.Directory]::CreateDirectory($leasedDirectory) | Out-Null
    $directoryLease =
        Enter-RebornControlledHostDirectoryLease $leasedDirectory
    try {
        Assert-Rejected {
            [IO.Directory]::Move(
                $leasedDirectory,
                $renamedDirectory)
        } 'leased directory rename'
        Assert-RebornControlledHostDirectoryLease $directoryLease |
            Out-Null
    }
    finally {
        Exit-RebornControlledHostDirectoryLease $directoryLease
    }
    [IO.Directory]::Move($leasedDirectory, $renamedDirectory)
    [IO.Directory]::Move($renamedDirectory, $leasedDirectory)

    $runtimeScope = Join-Path $temporaryRoot 'runtime'
    $runtimeLock = Enter-RebornControlledHostRuntimeLock `
        $runtimeScope 'parent contention test'
    try {
        $runtimeModule = ConvertTo-SingleQuotedLiteral (
            Join-Path $PSScriptRoot 'ControlledHostRuntimeLock.psm1')
        $runtimeLiteral =
            ConvertTo-SingleQuotedLiteral $runtimeScope
        $child = @"
`$ErrorActionPreference='Stop'
Import-Module $runtimeModule -Force
try {
    `$lock=Enter-RebornControlledHostRuntimeLock $runtimeLiteral 'child'
    try { 'ACQUIRED' } finally {
        Exit-RebornControlledHostRuntimeLock `$lock
    }
} catch { 'REJECTED' }
"@
        $output = Invoke-ChildPowerShell $child
        Assert-True `
            ($output -contains 'REJECTED') `
            'cross-process runtime-lock contention'
    }
    finally {
        Exit-RebornControlledHostRuntimeLock $runtimeLock
    }

    [IO.Directory]::CreateDirectory($operationLockRoot) |
        Out-Null
    $operationLock = Enter-RebornSecureNetworkOperationLock `
        -Name 'secure-bundle' `
        -LockRoot $operationLockRoot `
        -AllowTestPath
    try {
        $operationModule = ConvertTo-SingleQuotedLiteral (
            Join-Path $PSScriptRoot 'SecureNetworkOperationLock.psm1')
        $operationLiteral =
            ConvertTo-SingleQuotedLiteral $operationLockRoot
        $child = @"
`$ErrorActionPreference='Stop'
Import-Module $operationModule -Force
try {
    `$lock=Enter-RebornSecureNetworkOperationLock -Name 'secure-bundle' -LockRoot $operationLiteral -AllowTestPath
    try { 'ACQUIRED' } finally {
        Exit-RebornSecureNetworkOperationLock `$lock
    }
} catch { 'REJECTED' }
"@
        $output = Invoke-ChildPowerShell $child
        Assert-True `
            ($output -contains 'REJECTED') `
            'cross-process operation-lock contention'
    }
    finally {
        Exit-RebornSecureNetworkOperationLock $operationLock
    }
    $secondOperationLock =
        Enter-RebornSecureNetworkOperationLock `
            -Name 'secure-bundle' `
            -LockRoot $operationLockRoot `
            -AllowTestPath
    Exit-RebornSecureNetworkOperationLock $secondOperationLock
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        $resolved = [IO.Path]::GetFullPath($temporaryRoot)
        $temp = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\')
        if (-not $resolved.StartsWith(
                $temp + '\reborn-activation-security-',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing unsafe security-test cleanup: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

Write-Host 'Controlled-host activation security checks passed.'
