Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkBundleFiles.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

$script:ActiveEpochFile = 'active-client-stock-inventory.json'
$script:MaximumEpochBytes = 8192

function Get-RebornControlledHostClientInventoryRoot {
    return [IO.Path]::GetFullPath(
        (Join-Path (
            [Environment]::GetFolderPath('CommonApplicationData')
        ) 'RebornSecureNetworkClientInventory')).TrimEnd('\')
}

function New-RebornControlledHostClientInventorySecurity {
    param([switch]$File)

    $administrators =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $currentUser =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $inheritance = if ($File) {
        [Security.AccessControl.InheritanceFlags]::None
    } else {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    $security = if ($File) {
        [Security.AccessControl.FileSecurity]::new()
    } else {
        [Security.AccessControl.DirectorySecurity]::new()
    }
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administrators)
    foreach ($principal in @($administrators, $system)) {
        $security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $principal,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                [Security.AccessControl.PropagationFlags]::None,
                $allow))
    }
    $security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $currentUser,
            [Security.AccessControl.FileSystemRights]::ReadAndExecute,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            $allow))
    return $security
}

function Initialize-RebornControlledHostClientInventoryRoot {
    $root = Get-RebornControlledHostClientInventoryRoot
    $parent = Split-Path -Parent $root
    Assert-RebornDirectoryPath `
        $parent 'controlled-host client inventory parent' | Out-Null
    if (-not (Test-Path -LiteralPath $root)) {
        [IO.Directory]::CreateDirectory(
            $root,
            (New-RebornControlledHostClientInventorySecurity)) |
            Out-Null
    }
    Assert-RebornProtectedDirectoryPath `
        $root 'controlled-host client inventory root' `
        -ProtectContents `
        -RequireProtectedAcl | Out-Null
    return $root
}

function ConvertTo-RebornInventoryEpochUtc {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Label
    )

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact(
            $Value,
            'O',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero) {
        throw "Client inventory epoch has an invalid $Label."
    }
    return $parsed
}

function Read-RebornControlledHostClientInventoryEpoch {
    $root = Get-RebornControlledHostClientInventoryRoot
    Assert-RebornProtectedDirectoryPath `
        $root 'controlled-host client inventory root' `
        -ProtectContents `
        -RequireProtectedAcl | Out-Null
    $path = Join-Path $root $script:ActiveEpochFile
    Assert-RebornSingleLinkRegularFilePath `
        $path 'active client inventory epoch' | Out-Null
    Assert-RebornProtectedRegularFilePath `
        $path 'active client inventory epoch' | Out-Null
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Length -le 0 -or
        $item.Length -gt $script:MaximumEpochBytes) {
        throw 'Client inventory epoch state exceeds its bound.'
    }
    $record = Get-Content -LiteralPath $path -Raw |
        ConvertFrom-Json
    $properties = @($record.PSObject.Properties.Name)
    $expectedProperties = @(
        'schemaVersion',
        'state',
        'epochId',
        'startedUtc',
        'activatedUtc',
        'receiptFile',
        'receiptSha256',
        'inventorySetSha256'
    )
    if ($properties.Count -ne $expectedProperties.Count -or
        @($properties | Where-Object {
            $expectedProperties -cnotcontains $_
        }).Count -ne 0 -or
        $record.schemaVersion -ne 1 -or
        $record.state -notin @('PendingHardening', 'Active') -or
        [string]$record.epochId -cnotmatch '^[0-9a-f]{32}$') {
        throw 'Client inventory epoch state is malformed.'
    }
    $started = ConvertTo-RebornInventoryEpochUtc `
        ([string]$record.startedUtc) 'start time'
    if ($record.state -eq 'PendingHardening') {
        if ($null -ne $record.activatedUtc -or
            $null -ne $record.receiptFile -or
            $null -ne $record.receiptSha256 -or
            $null -ne $record.inventorySetSha256) {
            throw 'Pending client inventory epoch has active bindings.'
        }
        return [pscustomobject]@{
            Path = $path
            Record = $record
            StartedUtc = $started
        }
    }
    $activated = ConvertTo-RebornInventoryEpochUtc `
        ([string]$record.activatedUtc) 'activation time'
    if ($activated -lt $started -or
        [string]$record.receiptSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$record.inventorySetSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$record.receiptFile -cne (
            'client-stock-inventory-' +
            "$($record.inventorySetSha256)-" +
            "$($record.epochId).json")) {
        throw 'Active client inventory epoch bindings are malformed.'
    }
    return [pscustomobject]@{
        Path = $path
        Record = $record
        StartedUtc = $started
        ActivatedUtc = $activated
    }
}

function Write-RebornControlledHostClientInventoryEpoch {
    param([Parameter(Mandatory)][object]$Record)

    $root = Initialize-RebornControlledHostClientInventoryRoot
    $path = Join-Path $root $script:ActiveEpochFile
    Write-RebornJsonAtomic $Record $path
    Set-Acl -LiteralPath $path -AclObject (
        New-RebornControlledHostClientInventorySecurity -File)
    Assert-RebornProtectedRegularFilePath `
        $path 'active client inventory epoch' | Out-Null
    return Read-RebornControlledHostClientInventoryEpoch
}

function Start-RebornControlledHostClientInventoryEpoch {
    $epochId = [guid]::NewGuid().ToString('N')
    return Write-RebornControlledHostClientInventoryEpoch (
        [ordered]@{
            schemaVersion = 1
            state = 'PendingHardening'
            epochId = $epochId
            startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            activatedUtc = $null
            receiptFile = $null
            receiptSha256 = $null
            inventorySetSha256 = $null
        })
}

function Assert-RebornControlledHostInventoryEpochBinding {
    param(
        [Parameter(Mandatory)][object]$Epoch,
        [Parameter(Mandatory)][string]$ReceiptPath,
        [Parameter(Mandatory)][string]$ReceiptSha256,
        [Parameter(Mandatory)][string]$InventorySetSha256,
        [Parameter(Mandatory)][string]$PreparationEpochId
    )

    $record = $Epoch.Record
    if ($record.state -cne 'Active' -or
        [string]$record.epochId -cne $PreparationEpochId -or
        [string]$record.receiptFile -cne
            [IO.Path]::GetFileName($ReceiptPath) -or
        [string]$record.receiptSha256 -cne
            $ReceiptSha256.ToUpperInvariant() -or
        [string]$record.inventorySetSha256 -cne
            $InventorySetSha256.ToUpperInvariant()) {
        throw 'Client inventory receipt is not the active preparation epoch.'
    }
    return $true
}

function Publish-RebornControlledHostClientInventoryReceipt {
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][object]$PendingEpoch
    )

    $current =
        Read-RebornControlledHostClientInventoryEpoch
    if ($current.Record.state -cne 'PendingHardening' -or
        [string]$current.Record.epochId -cne
            [string]$PendingEpoch.Record.epochId -or
        [string]$Record.preparationEpochId -cne
            [string]$current.Record.epochId) {
        throw 'Client inventory preparation epoch changed before publish.'
    }
    $root = Get-RebornControlledHostClientInventoryRoot
    $fileName =
        'client-stock-inventory-' +
        "$($Record.inventorySetSha256)-" +
        "$($Record.preparationEpochId).json"
    $receipt = Join-Path $root $fileName
    if (Test-Path -LiteralPath $receipt) {
        throw 'Client inventory epoch receipt already exists.'
    }
    Write-RebornJsonAtomic $Record $receipt
    Set-Acl -LiteralPath $receipt -AclObject (
        New-RebornControlledHostClientInventorySecurity -File)
    $receiptSha256 =
        (Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash
    $active = Write-RebornControlledHostClientInventoryEpoch (
        [ordered]@{
            schemaVersion = 1
            state = 'Active'
            epochId = [string]$Record.preparationEpochId
            startedUtc = [string]$current.Record.startedUtc
            activatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            receiptFile = $fileName
            receiptSha256 = $receiptSha256
            inventorySetSha256 =
                ([string]$Record.inventorySetSha256).ToUpperInvariant()
        })
    Assert-RebornControlledHostInventoryEpochBinding `
        $active `
        $receipt `
        $receiptSha256 `
        $Record.inventorySetSha256 `
        $Record.preparationEpochId | Out-Null
    return [pscustomobject]@{
        ReceiptPath = $receipt
        ReceiptSha256 = $receiptSha256
        Epoch = $active
    }
}

Export-ModuleMember -Function @(
    'Get-RebornControlledHostClientInventoryRoot',
    'New-RebornControlledHostClientInventorySecurity',
    'Initialize-RebornControlledHostClientInventoryRoot',
    'Read-RebornControlledHostClientInventoryEpoch',
    'Start-RebornControlledHostClientInventoryEpoch',
    'Assert-RebornControlledHostInventoryEpochBinding',
    'Publish-RebornControlledHostClientInventoryReceipt'
)
