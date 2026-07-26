[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($moduleName in @(
    'ControlledHostDatabaseBackup.psm1',
    'ControlledHostReadOnlyArtifactAcl.psm1',
    'DevelopmentNetworkHostsAcl.psm1'
)) {
    Import-Module (Join-Path $PSScriptRoot $moduleName) -Force
}

function Assert-True {
    param([bool]$Condition, [string]$Label)
    if (-not $Condition) {
        throw "Assertion failed: $Label"
    }
}

function New-BackupFixture {
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$ReceiptReaderSid,
        [switch]$GrantReaderWrite
    )

    $scope = Join-Path $testRoot $Name
    $protected = Join-Path $scope 'controlled-host-database'
    [IO.Directory]::CreateDirectory($protected) | Out-Null
    $source = Join-Path $scope 'source.dump'
    $target = Join-Path $protected 'godswar-20260726-170000.dump'
    [IO.File]::WriteAllBytes(
        $source,
        [byte[]](1, 3, 3, 7, 9))
    [IO.File]::Copy($source, $target)
    $hash = (Get-FileHash $target -Algorithm SHA256).Hash
    $receipt = Join-Path $protected 'database-backup-fixture.json'
    $record =
        New-RebornControlledHostDatabaseBackupReceipt `
            $target $hash $source
    if (-not [string]::IsNullOrEmpty($ReceiptReaderSid)) {
        $record.readerSid = $ReceiptReaderSid
    }
    [IO.File]::WriteAllText(
        $receipt,
        ($record | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))

    $reader =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($file in @($target, $receipt)) {
        $fileSecurity =
            New-RebornControlledHostReadOnlyArtifactSecurity `
                -File -ReaderSid $reader -OwnerSid $reader
        if ($GrantReaderWrite -and $file -ceq $target) {
            $fileSecurity.AddAccessRule(
                [Security.AccessControl.FileSystemAccessRule]::new(
                    $reader,
                    [Security.AccessControl.FileSystemRights]::Write,
                    [Security.AccessControl.AccessControlType]::Allow))
        }
        Set-Acl -LiteralPath $file -AclObject $fileSecurity
    }
    Set-Acl -LiteralPath $protected -AclObject (
        New-RebornControlledHostReadOnlyArtifactSecurity `
            -ReaderSid $reader -OwnerSid $reader)
    [pscustomobject]@{
        ProtectedRoot = $protected
        Source = $source
        Target = $target
        Receipt = $receipt
        Hash = $hash
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw (
        'Database backup read-only test must run from a fresh ' +
        'non-elevated PowerShell token.')
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-database-backup-$([Guid]::NewGuid().ToString('N'))")
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$junctionPath = $null
try {
    $databaseAcl =
        New-RebornControlledHostReadOnlyArtifactSecurity
    $hostsAcl = New-RebornDevelopmentHostsArtifactSecurity
    Assert-True `
        ($databaseAcl.GetSecurityDescriptorSddlForm(
            [Security.AccessControl.AccessControlSections]::All) -ceq
         $hostsAcl.GetSecurityDescriptorSddlForm(
            [Security.AccessControl.AccessControlSections]::All)) `
        'database and hosts shared-parent ACL idempotence'

    $junctionTarget = Join-Path $testRoot 'junction-target'
    $junctionPath = Join-Path $testRoot 'junction-alias'
    [IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
    New-Item -ItemType Junction `
        -Path $junctionPath `
        -Target $junctionTarget | Out-Null
    $mutationCounter = [pscustomobject]@{ Count = 0 }
    $mutationHook = {
        param($Path, $Security)
        $mutationCounter.Count++
    }.GetNewClosure()
    $reparseRejected = $false
    try {
        Protect-RebornControlledHostReadOnlyArtifact `
            $junctionPath `
            -SetAclAction $mutationHook `
            -AllowTestHook | Out-Null
    }
    catch {
        $reparseRejected = $true
    }
    Assert-True `
        ($reparseRejected -and $mutationCounter.Count -eq 0) `
        'reparse target rejected before Set-Acl mutation'
    [IO.Directory]::Delete($junctionPath)
    $junctionPath = $null

    $valid = New-BackupFixture 'valid'
    $state =
        Get-RebornControlledHostDatabaseBackupState `
            $valid.Target $valid.Receipt $valid.Hash `
            -AllowTestOwner
    Assert-True `
        ($state.State -ceq 'Protected' -and
         $state.ReaderSid -ceq $identity.User.Value) `
        'ordinary-user protected backup Status'
    $moduleLiteral = (
        Join-Path $PSScriptRoot 'ControlledHostDatabaseBackup.psm1'
    ).Replace("'", "''")
    $targetLiteral = $valid.Target.Replace("'", "''")
    $receiptLiteral = $valid.Receipt.Replace("'", "''")
    $childScript = @"
`$ErrorActionPreference = 'Stop'
Import-Module '$moduleLiteral' -Force
`$result = Get-RebornControlledHostDatabaseBackupState '$targetLiteral' '$receiptLiteral' '$($valid.Hash)' -AllowTestOwner
if (`$result.State -cne 'Protected') { exit 9 }
'FRESH_STATUS_OK'
"@
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($childScript))
    $childOutput = @(
        & (Join-Path $PSHOME 'powershell.exe') `
            -NoLogo -NoProfile -NonInteractive `
            -EncodedCommand $encoded 2>&1
    )
    Assert-True `
        ($LASTEXITCODE -eq 0 -and
         $childOutput -contains 'FRESH_STATUS_OK') `
        'fresh non-elevated PowerShell backup Status'

    $read = [IO.File]::Open(
        $valid.Target,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        Assert-True ($read.Length -eq 5) 'ordinary-user dump read'
    }
    finally {
        $read.Dispose()
    }
    $writeRejected = $false
    try {
        $write = [IO.File]::Open(
            $valid.Target,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $write.Dispose()
    }
    catch [UnauthorizedAccessException] {
        $writeRejected = $true
    }
    Assert-True $writeRejected 'ordinary-user dump write denial'

    $wrongSid = New-BackupFixture `
        'wrong-sid' 'S-1-5-21-1-2-3-1001'
    $wrongSidState =
        Get-RebornControlledHostDatabaseBackupState `
            $wrongSid.Target $wrongSid.Receipt $wrongSid.Hash `
            -AllowTestOwner
    Assert-True `
        ($wrongSidState.State -ceq 'Conflict') `
        'wrong receipt reader SID rejection'

    $writable = New-BackupFixture 'writable' -GrantReaderWrite
    $writableState =
        Get-RebornControlledHostDatabaseBackupState `
            $writable.Target $writable.Receipt $writable.Hash `
            -AllowTestOwner
    Assert-True `
        ($writableState.State -ceq 'Conflict') `
        'reader write permission rejection'

    Write-Host 'Controlled-host database backup checks passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temporary = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith(
            $temporary + 'reborn-database-backup-',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unsafe database-backup test removal: $resolved"
    }
    if ($null -ne $junctionPath -and
        (Test-Path -LiteralPath $junctionPath)) {
        $attributes = [IO.File]::GetAttributes($junctionPath)
        if (($attributes -band
                [IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw 'Database-backup test junction changed before cleanup.'
        }
        [IO.Directory]::Delete($junctionPath)
    }
    if (Test-Path -LiteralPath $resolved) {
        # Restore inherited test ACLs so the temporary fixture is removable.
        & icacls.exe $resolved '/reset' '/T' '/C' '/Q' | Out-Null
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
