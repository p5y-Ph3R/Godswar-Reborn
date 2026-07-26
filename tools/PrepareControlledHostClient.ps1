[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\RebornNetworkAcceptanceClient',

    [string]$EvidenceDirectory = (
        Join-Path $PSScriptRoot `
            '..\artifacts\controlled-host-acceptance\client-acl'),

    [switch]$AllowAclWrite
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedClientRoot = 'C:\RebornNetworkAcceptanceClient'
$originSha256 =
    '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
$stockNetSha256 =
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientInventoryReceipt.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientInventoryCore.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientInventoryEpoch.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientRootLease.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force

$writableOutputRelativePaths =
    @(Get-RebornControlledHostWritableOutputRelativePaths)

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-StockDisposableClient {
    param([Parameter(Mandatory)][string]$Root)

    if (-not $Root.Equals(
            $expectedClientRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'ACL preparation is restricted to the disposable acceptance ' +
            "client: $expectedClientRoot")
    }
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Disposable acceptance client not found: $Root"
    }
    $item = Get-Item -LiteralPath $Root -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Disposable acceptance client cannot be a reparse point.'
    }
    if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
        throw 'Origin.exe must be closed before client ACL preparation.'
    }

    $origin = Join-Path $Root 'Origin.exe'
    $net = Join-Path $Root 'Net.dll'
    if (
        -not (Test-Path -LiteralPath $origin -PathType Leaf) -or
        -not (Test-Path -LiteralPath $net -PathType Leaf) -or
        (Get-Sha256 $origin) -cne $originSha256 -or
        (Get-Sha256 $net) -cne $stockNetSha256 -or
        (Test-Path -LiteralPath (Join-Path $Root 'NetLegacy.dll')) -or
        (Test-Path -LiteralPath (Join-Path $Root 'RebornNetwork.gwem'))
    ) {
        throw 'Disposable acceptance client is not the exact stock baseline.'
    }
}

function Get-HardeningStatus {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][object]$Inventory
    )

    try {
        Assert-RebornProtectedDirectoryPath `
            $Root 'acceptance ClientRoot' `
            -ProtectContents `
            -RequireProtectedAcl | Out-Null
        Assert-RebornProtectedFileSet `
            @(
                (Join-Path $Root 'Origin.exe'),
                (Join-Path $Root 'Net.dll')
            ) `
            'acceptance managed file'
        Assert-AcceptanceOutputDirectoryAcls $Root
        $inventoryReceipt =
            Read-RebornControlledHostActiveClientInventoryReceipt
        Assert-RebornControlledHostInventoryEqual `
            $inventoryReceipt.Inventory $Inventory `
            'protected stock client inventory' | Out-Null
        return [pscustomobject]@{
            State = 'Hardened'
            Reason = $null
            InventoryReceiptPath =
                $inventoryReceipt.ReceiptPath
            InventoryReceiptSha256 =
                $inventoryReceipt.ReceiptSha256
            InventorySetSha256 =
                $inventoryReceipt.Inventory.SetSha256
        }
    }
    catch {
        return [pscustomobject]@{
            State = 'NeedsHardening'
            Reason = $_.Exception.Message
            InventoryReceiptPath = $null
            InventoryReceiptSha256 = $null
            InventorySetSha256 = $Inventory.SetSha256
        }
    }
}

function New-ClientRootSecurity {
    $administrators =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $users =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    $inheritance =
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $none = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administrators)
    foreach ($principal in @($administrators, $system)) {
        $security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $principal,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                $none,
                $allow))
    }
    $security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $users,
            [Security.AccessControl.FileSystemRights]::ReadAndExecute,
            $inheritance,
            $none,
            $allow))
    return $security
}

function New-ClientOutputSecurity {
    $administrators =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $currentUser =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    $inheritance =
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $none = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administrators)
    foreach ($principal in @($administrators, $system)) {
        $security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $principal,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                $none,
                $allow))
    }
    $security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $currentUser,
            [Security.AccessControl.FileSystemRights]::Modify,
            $inheritance,
            $none,
            $allow))
    return $security
}

function Assert-AcceptanceOutputDirectoryAcls {
    param([Parameter(Mandatory)][string]$Root)

    $currentUser =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    $administrators = 'S-1-5-32-544'
    foreach ($relativePath in $writableOutputRelativePaths) {
        $path = Join-Path $Root $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            throw "Required client output directory is missing: $path"
        }
        $acl = Get-Acl -LiteralPath $path
        $owner = $acl.GetOwner(
            [Security.Principal.SecurityIdentifier]).Value
        $rules = @($acl.GetAccessRules(
            $true,
            $true,
            [Security.Principal.SecurityIdentifier]))
        $currentRules = @($rules | Where-Object {
            $_.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            $_.IdentityReference.Value -eq $currentUser.Value -and
            ($_.FileSystemRights -band
                [Security.AccessControl.FileSystemRights]::Modify) -eq
                [Security.AccessControl.FileSystemRights]::Modify
        })
        $unexpectedWritable = @($rules | Where-Object {
            $_.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            $_.IdentityReference.Value -notin @(
                'S-1-5-18',
                $administrators,
                $currentUser.Value
            ) -and
            ($_.FileSystemRights -band
                [Security.AccessControl.FileSystemRights]::Modify) -ne 0
        })
        if ($owner -ne $administrators -or
            -not $acl.AreAccessRulesProtected -or
            $currentRules.Count -eq 0 -or
            $unexpectedWritable.Count -ne 0) {
            throw "Client output ACL is not narrowly writable: $path"
        }
        $unsafeContent = Get-ChildItem `
            -LiteralPath $path -File -Force -Recurse |
            Where-Object {
                $_.Extension -in @(
                    '.exe', '.dll', '.com', '.bat', '.cmd', '.ps1',
                    '.js', '.vbs', '.scr', '.cpl', '.ocx', '.sys',
                    '.msi', '.lnk', '.url'
                )
            } |
            Select-Object -First 1
        if ($null -ne $unsafeContent) {
            throw (
                'A writable client output directory contains executable ' +
                "content: $($unsafeContent.FullName)")
        }
    }
    foreach ($locale in @('en_us', 'zh_cn')) {
        $systemSettings = Join-Path $Root (
            "Localization\$locale\Settings\Sys")
        Assert-RebornProtectedDirectoryPath `
            $systemSettings `
            "protected $locale system settings" `
            -ProtectContents | Out-Null
    }
}

function Invoke-Icacls {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & icacls.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "icacls failed with exit code $LASTEXITCODE."
    }
}

$client = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
$clientRootLease =
    Enter-RebornControlledHostClientRootLease $client
try {
    Assert-StockDisposableClient $client
    Assert-RebornControlledHostClientRootLease `
        $clientRootLease | Out-Null
    $stockInventory =
        Get-RebornControlledHostClientInventory $client
    $status = Get-HardeningStatus $client $stockInventory

    if ($Mode -eq 'Status') {
        Assert-RebornControlledHostClientRootLease `
            $clientRootLease | Out-Null
        [pscustomobject]@{
            State = $status.State
            Reason = $status.Reason
            ClientRoot = $client
            OriginSha256 = Get-Sha256 (Join-Path $client 'Origin.exe')
            NetSha256 = Get-Sha256 (Join-Path $client 'Net.dll')
            InventoryReceiptPath = $status.InventoryReceiptPath
            InventoryReceiptSha256 = $status.InventoryReceiptSha256
            InventorySetSha256 = $status.InventorySetSha256
            Elevated = Test-IsAdministrator
        }
        return
    }

    if (-not $AllowAclWrite) {
        throw 'Apply requires explicit -AllowAclWrite.'
    }
    if (-not (Test-IsAdministrator)) {
        throw 'Client ACL Apply requires an elevated PowerShell process.'
    }
    if ($status.State -eq 'Hardened') {
        [pscustomobject]@{
            Result = 'AlreadyHardened'
            ClientRoot = $client
            InventoryReceiptPath = $status.InventoryReceiptPath
            InventoryReceiptSha256 = $status.InventoryReceiptSha256
            InventorySetSha256 = $status.InventorySetSha256
        }
        return
    }
    if (-not $PSCmdlet.ShouldProcess(
            $client,
            'Harden only the disposable acceptance-client ACL')) {
        return
    }

$pendingInventoryEpoch =
    Start-RebornControlledHostClientInventoryEpoch
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
if (-not (Test-Path -LiteralPath $evidence -PathType Container)) {
    New-Item -ItemType Directory -Path $evidence | Out-Null
}
$before = Get-Acl -LiteralPath $client
$receiptPath = Join-Path $evidence (
    "client-acl-$([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')).json")
if (Test-Path -LiteralPath $receiptPath) {
    throw "Refusing to overwrite ACL evidence: $receiptPath"
}

Set-Acl -LiteralPath $client -AclObject (New-ClientRootSecurity)
Invoke-Icacls @(
    $client,
    '/setowner',
    '*S-1-5-32-544',
    '/T',
    '/L',
    '/C',
    '/Q'
)
Invoke-Icacls @(
    (Join-Path $client '*'),
    '/reset',
    '/T',
    '/L',
    '/C',
    '/Q'
)
foreach ($relativePath in $writableOutputRelativePaths) {
    $output = Join-Path $client $relativePath
    Set-Acl -LiteralPath $output -AclObject (New-ClientOutputSecurity)
    Invoke-Icacls @(
        (Join-Path $output '*'),
        '/reset',
        '/T',
        '/L',
        '/C',
        '/Q'
    )
}
Assert-RebornControlledHostClientRootLease `
    $clientRootLease | Out-Null
$hardenedInventory =
    Get-RebornControlledHostClientInventory $client
Assert-RebornControlledHostInventoryEqual `
    $stockInventory $hardenedInventory `
    'post-ACL stock client inventory' | Out-Null
$inventoryReceipt =
    Protect-RebornControlledHostClientInventoryReceipt `
        $hardenedInventory `
        $pendingInventoryEpoch
Assert-RebornControlledHostClientRootLease `
    $clientRootLease | Out-Null

$hardened = Get-HardeningStatus $client $hardenedInventory
if ($hardened.State -ne 'Hardened') {
    throw "Client ACL verification failed: $($hardened.Reason)"
}
$after = Get-Acl -LiteralPath $client
$receipt = [ordered]@{
    schemaVersion = 1
    mode = 'DisposableClientAclPreparation'
    clientRoot = $client
    preparedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    originSha256 = Get-Sha256 (Join-Path $client 'Origin.exe')
    netSha256 = Get-Sha256 (Join-Path $client 'Net.dll')
    beforeSddl = $before.Sddl
    afterSddl = $after.Sddl
    recovery = 'Discard this disposable client after guarded bundle Restore.'
}
[IO.File]::WriteAllText(
    $receiptPath,
    ($receipt | ConvertTo-Json),
    [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    Result = 'Hardened'
    ClientRoot = $client
    ReceiptPath = $receiptPath
    InventoryReceiptPath = $inventoryReceipt.ReceiptPath
    InventoryReceiptSha256 = $inventoryReceipt.ReceiptSha256
    InventorySetSha256 = $inventoryReceipt.Inventory.SetSha256
    RebootRequiredBeforeBundleApply = $true
}
}
finally {
    Exit-RebornControlledHostClientRootLease $clientRootLease
}
