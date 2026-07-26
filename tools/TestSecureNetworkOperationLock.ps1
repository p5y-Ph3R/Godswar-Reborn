[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$modulePath = Join-Path $PSScriptRoot 'SecureNetworkOperationLock.psm1'
Import-Module $modulePath -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

$root = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-operation-lock-test-$([guid]::NewGuid().ToString('N'))")
[IO.Directory]::CreateDirectory($root) | Out-Null
$ready = Join-Path $root 'ready'
$release = Join-Path $root 'release'
$child = $null
try {
    $escapedModule = $modulePath.Replace("'", "''")
    $escapedRoot = $root.Replace("'", "''")
    $escapedReady = $ready.Replace("'", "''")
    $escapedRelease = $release.Replace("'", "''")
    $childScript = @"
`$ErrorActionPreference = 'Stop'
Import-Module '$escapedModule' -Force
`$held = Enter-RebornSecureNetworkOperationLock -Name 'contention' -LockRoot '$escapedRoot' -AllowTestPath
try {
    [IO.File]::WriteAllText('$escapedReady', 'ready')
    `$deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath '$escapedRelease')) {
        if ([DateTime]::UtcNow -ge `$deadline) { throw 'release timeout' }
        [Threading.Thread]::Sleep(25)
    }
}
finally {
    Exit-RebornSecureNetworkOperationLock `$held
}
"@
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($childScript))
    $hostExecutable = (Get-Process -Id $PID).Path
    $child = Start-Process `
        -FilePath $hostExecutable `
        -ArgumentList @(
            '-NoProfile',
            '-NonInteractive',
            '-EncodedCommand',
            $encoded) `
        -WindowStyle Hidden `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path -LiteralPath $ready)) {
        if ($child.HasExited) {
            throw "Operation-lock child exited early: $($child.ExitCode)"
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw 'Operation-lock child did not become ready.'
        }
        [Threading.Thread]::Sleep(25)
    }

    $contentionRejected = $false
    try {
        Enter-RebornSecureNetworkOperationLock `
            -Name 'contention' `
            -LockRoot $root `
            -AllowTestPath | Out-Null
    }
    catch {
        $contentionRejected =
            $_.Exception.Message -match 'already held'
    }
    Assert-True $contentionRejected (
        'a separate process acquired the held operation lock')

    [IO.File]::WriteAllText($release, 'release')
    Assert-True (
        $child.WaitForExit(5000) -and $child.ExitCode -eq 0
    ) 'operation-lock child did not release cleanly'

    $reacquired = Enter-RebornSecureNetworkOperationLock `
        -Name 'contention' -LockRoot $root -AllowTestPath
    Exit-RebornSecureNetworkOperationLock $reacquired

    $lockPath = Join-Path $root 'contention.lock'
    $lockHash =
        (Get-FileHash $lockPath -Algorithm SHA256).Hash
    $readLease = Enter-RebornSecureNetworkOperationReadLease `
        -Name 'contention' -LockRoot $root -AllowTestPath
    try {
        Assert-True (
            $readLease.ReadOnly -and
            $readLease.Stream.CanRead -and
            -not $readLease.Stream.CanWrite
        ) 'ordinary runtime lease was not opened read-only'
        $mutationBlocked = $false
        try {
            Enter-RebornSecureNetworkOperationLock `
                -Name 'contention' `
                -LockRoot $root `
                -AllowTestPath | Out-Null
        }
        catch {
            $mutationBlocked =
                $_.Exception.Message -match 'already held'
        }
        $writeBlocked = $false
        try {
            [IO.File]::WriteAllText($lockPath, 'mutated')
        }
        catch [IO.IOException] {
            $writeBlocked = $true
        }
        $deleteBlocked = $false
        try {
            [IO.File]::Delete($lockPath)
        }
        catch [IO.IOException] {
            $deleteBlocked = $true
        }
        Assert-True (
            $mutationBlocked -and
            $writeBlocked -and
            $deleteBlocked -and
            (Get-FileHash $lockPath -Algorithm SHA256).Hash -ceq
                $lockHash
        ) 'read-only runtime lease permitted lock mutation or deletion'
    }
    finally {
        Exit-RebornSecureNetworkOperationLock $readLease
    }

    $missingRoot = Join-Path $root 'missing-read-lease-root'
    $missingRejected = $false
    try {
        Enter-RebornSecureNetworkOperationReadLease `
            -Name 'missing' `
            -LockRoot $missingRoot `
            -AllowTestPath | Out-Null
    }
    catch {
        $missingRejected = $_.Exception.Message -match (
            'existing lock root')
    }
    Assert-True (
        $missingRejected -and
        -not (Test-Path -LiteralPath $missingRoot)
    ) 'read-only runtime lease created ProgramData-like operation state'

    $issuedFileSecurity =
        New-RebornSecureNetworkLockSecurity -File
    $currentSid =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $currentRule = @(
        $issuedFileSecurity.GetAccessRules(
            $true,
            $true,
            [Security.Principal.SecurityIdentifier]) |
            Where-Object {
                $_.IdentityReference.Value -ceq $currentSid
            }
    )
    $writeRights =
        [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions
    Assert-True (
        $issuedFileSecurity.AreAccessRulesProtected -and
        $currentRule.Count -eq 1 -and
        ($currentRule[0].FileSystemRights -band $writeRights) -eq 0 -and
        ($currentRule[0].FileSystemRights -band
            [Security.AccessControl.FileSystemRights]::Read) -eq
            [Security.AccessControl.FileSystemRights]::Read
    ) 'issued operation-lock ACL grants current SID mutation rights'

    $productionOverrideRejected = $false
    try {
        Enter-RebornSecureNetworkOperationLock `
            -Name 'refusal' -LockRoot $root | Out-Null
    }
    catch {
        $productionOverrideRejected =
            $_.Exception.Message -match 'issued path'
    }
    Assert-True $productionOverrideRejected (
        'production operation lock accepted a caller-selected root')

    [pscustomobject]@{
        Result = 'Passed'
        CrossProcessContention = $true
        ReacquireAfterRelease = $true
        ReadOnlyLeaseContention = $true
        ReadLeaseCreatesNothing = $true
        CurrentSidReadOnlyAcl = $true
        ProductionRootBinding = $true
    }
}
finally {
    if ($null -ne $child -and -not $child.HasExited) {
        [IO.File]::WriteAllText($release, 'release')
        $child.WaitForExit(2000) | Out-Null
        if (-not $child.HasExited) {
            $child.Kill()
        }
    }
    if ($null -ne $child) {
        $child.Dispose()
    }
    $resolved = [IO.Path]::GetFullPath($root)
    $temporary = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith(
            $temporary,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unexpected test cleanup path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
