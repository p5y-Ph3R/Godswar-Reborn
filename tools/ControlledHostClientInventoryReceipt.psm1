Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'ControlledHostClientInventoryCore.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'ControlledHostClientInventoryEpoch.psm1'
)

$script:ExpectedClientRoot = 'C:\RebornNetworkAcceptanceClient'
$script:MaximumReceiptBytes = 32MB

function Get-RebornControlledHostClientInventoryRoot {
    [IO.Path]::GetFullPath(
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
            (New-RebornControlledHostClientInventorySecurity)) | Out-Null
    }
    Assert-RebornProtectedDirectoryPath `
        $root 'controlled-host client inventory root' `
        -ProtectContents `
        -RequireProtectedAcl | Out-Null
    return $root
}

function ConvertTo-RebornControlledHostClientInventoryRecord {
    param(
        [Parameter(Mandatory)][object]$Inventory,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9a-f]{32}$')]
        [string]$PreparationEpochId
    )

    $set =
        Get-RebornControlledHostInventorySetSha256 @($Inventory.Files)
    if ([string]$Inventory.SetSha256 -cne $set.SetSha256) {
        throw 'Client inventory set hash changed before receipt creation.'
    }
    $files = foreach ($file in $set.Files) {
        [ordered]@{
            relativePath = $file.RelativePath
            length = $file.Length.ToString(
                [Globalization.CultureInfo]::InvariantCulture)
            sha256 = $file.Sha256
        }
    }
    return [ordered]@{
        schemaVersion = 2
        mode = 'ControlledHostClientStockInventory'
        preparationEpochId = $PreparationEpochId
        clientRoot = [IO.Path]::GetFullPath(
            [string]$Inventory.ClientRoot).TrimEnd('\')
        readerSid =
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        inventorySetSha256 = $set.SetSha256
        writableOutputRelativePaths =
            @(Get-RebornControlledHostWritableOutputRelativePaths)
        files = @($files)
    }
}

function Read-RebornControlledHostClientInventoryReceipt {
    param(
        [Parameter(Mandatory)][string]$ReceiptPath,
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedReceiptSha256
    )

    $root = Get-RebornControlledHostClientInventoryRoot
    $receipt = [IO.Path]::GetFullPath($ReceiptPath)
    if (-not (Split-Path -Parent $receipt).Equals(
            $root,
            [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($receipt) -cnotmatch
            ('^client-stock-inventory-[0-9A-F]{64}-' +
             '[0-9a-f]{32}\.json$')) {
        throw 'Client inventory receipt is outside protected issued scope.'
    }
    Assert-RebornProtectedDirectoryPath `
        $root 'controlled-host client inventory root' `
        -ProtectContents `
        -RequireProtectedAcl | Out-Null
    Assert-RebornSingleLinkRegularFilePath `
        $receipt 'controlled-host client inventory receipt' | Out-Null
    Assert-RebornProtectedRegularFilePath `
        $receipt 'controlled-host client inventory receipt' | Out-Null
    $item = Get-Item -LiteralPath $receipt -Force
    if ($item.Length -le 0 -or $item.Length -gt $script:MaximumReceiptBytes) {
        throw 'Client inventory receipt exceeds its bounded size.'
    }
    $receiptSha256 =
        (Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash
    if (-not [string]::IsNullOrWhiteSpace($ExpectedReceiptSha256) -and
        $receiptSha256 -cne
            $ExpectedReceiptSha256.ToUpperInvariant()) {
        throw 'Client inventory receipt SHA-256 does not match its pin.'
    }

    $record = Get-Content -LiteralPath $receipt -Raw |
        ConvertFrom-Json
    $currentSid =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($record.schemaVersion -ne 2 -or
        $record.mode -cne 'ControlledHostClientStockInventory' -or
        [string]$record.preparationEpochId -cnotmatch
            '^[0-9a-f]{32}$' -or
        -not ([IO.Path]::GetFullPath(
            [string]$record.clientRoot)).TrimEnd('\').Equals(
                $script:ExpectedClientRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
        [string]$record.readerSid -cne $currentSid -or
        [string]$record.inventorySetSha256 -cnotmatch
            '^[0-9A-F]{64}$') {
        throw 'Client inventory receipt metadata is not applicable.'
    }
    $writable = @($record.writableOutputRelativePaths)
    $expectedWritable =
        @(Get-RebornControlledHostWritableOutputRelativePaths)
    if ($writable.Count -ne $expectedWritable.Count) {
        throw 'Client inventory writable-island policy changed.'
    }
    for ($index = 0; $index -lt $expectedWritable.Count; $index++) {
        if ([string]$writable[$index] -cne $expectedWritable[$index]) {
            throw 'Client inventory writable-island policy changed.'
        }
    }

    $files = foreach ($file in @($record.files)) {
        $length = [Int64]0
        if (-not [Int64]::TryParse(
                [string]$file.length,
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$length)) {
            throw 'Client inventory receipt has an invalid file length.'
        }
        [pscustomobject]@{
            RelativePath = [string]$file.relativePath
            Length = $length
            Sha256 = [string]$file.sha256
        }
    }
    $set = Get-RebornControlledHostInventorySetSha256 @($files)
    if ($set.SetSha256 -cne [string]$record.inventorySetSha256 -or
        [IO.Path]::GetFileName($receipt) -cne
            (
                "client-stock-inventory-$($set.SetSha256)-" +
                "$($record.preparationEpochId).json")) {
        throw 'Client inventory receipt canonical set verification failed.'
    }
    $epoch =
        Read-RebornControlledHostClientInventoryEpoch
    Assert-RebornControlledHostInventoryEpochBinding `
        $epoch `
        $receipt `
        $receiptSha256 `
        $set.SetSha256 `
        $record.preparationEpochId | Out-Null
    return [pscustomobject]@{
        ReceiptPath = $receipt
        ReceiptSha256 = $receiptSha256
        Inventory = [pscustomobject]@{
            ClientRoot = $script:ExpectedClientRoot
            SetSha256 = $set.SetSha256
            Files = $set.Files
            WritableOutputRelativePaths = $expectedWritable
        }
        Record = $record
        Epoch = $epoch
    }
}

function Protect-RebornControlledHostClientInventoryReceipt {
    param(
        [Parameter(Mandatory)][object]$Inventory,
        [Parameter(Mandatory)][object]$PendingEpoch
    )

    if (-not ([IO.Path]::GetFullPath(
            [string]$Inventory.ClientRoot)).TrimEnd('\').Equals(
                $script:ExpectedClientRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Only the disposable acceptance client can be inventoried.'
    }
    $record =
        ConvertTo-RebornControlledHostClientInventoryRecord `
            $Inventory `
            ([string]$PendingEpoch.Record.epochId)
    $published =
        Publish-RebornControlledHostClientInventoryReceipt `
            $record `
            $PendingEpoch
    return Read-RebornControlledHostClientInventoryReceipt `
        $published.ReceiptPath `
        $published.ReceiptSha256
}

function Read-RebornControlledHostActiveClientInventoryReceipt {
    $epoch =
        Read-RebornControlledHostClientInventoryEpoch
    if ($epoch.Record.state -cne 'Active') {
        throw 'No active client inventory preparation epoch exists.'
    }
    return Read-RebornControlledHostClientInventoryReceipt `
        (Join-Path (
            Get-RebornControlledHostClientInventoryRoot
        ) ([string]$epoch.Record.receiptFile)) `
        ([string]$epoch.Record.receiptSha256)
}

function Assert-RebornControlledHostClientInventoryReceipt {
    param(
        [Parameter(Mandatory)][object]$Receipt,
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)]
        [ValidateSet('Stock', 'InstalledExact')]
        [string]$Mode,
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$CandidateSha256,
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$LegacyNetSha256,
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ManifestSha256,
        [IO.FileStream]$LockedOriginStream,
        [switch]$AllowTestPath
    )

    $resolvedClient =
        [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
    if ($AllowTestPath) {
        $temporary = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedClient.StartsWith(
                $temporary,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFullPath(
                [string]$Receipt.Inventory.ClientRoot)).TrimEnd('\').Equals(
                    $resolvedClient,
                    [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Test client inventory root is outside its receipt scope.'
        }
    } elseif (-not $resolvedClient.Equals(
            $script:ExpectedClientRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFullPath(
            [string]$Receipt.Inventory.ClientRoot)).TrimEnd('\').Equals(
                $script:ExpectedClientRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'Client inventory validation is outside the exact disposable ' +
            'acceptance root.')
    }
    $current =
        Get-RebornControlledHostClientInventory `
            $resolvedClient `
            -LockedOriginStream $LockedOriginStream
    $expected = $Receipt.Inventory
    if ($Mode -eq 'InstalledExact') {
        foreach ($value in @(
            $CandidateSha256,
            $LegacyNetSha256,
            $ManifestSha256
        )) {
            if ([string]::IsNullOrWhiteSpace($value)) {
                throw 'Installed inventory validation requires delta hashes.'
            }
        }
        $candidate = Join-Path $resolvedClient 'Net.dll'
        $legacy = Join-Path $resolvedClient 'NetLegacy.dll'
        $manifest = Join-Path $resolvedClient 'RebornNetwork.gwem'
        foreach ($path in @($candidate, $legacy, $manifest)) {
            Assert-RebornSingleLinkRegularFilePath `
                $path 'installed client inventory delta' | Out-Null
        }
        $expected = New-RebornControlledHostInstalledInventory `
            $Receipt.Inventory `
            $CandidateSha256 `
            $LegacyNetSha256 `
            $ManifestSha256 `
            (Get-Item -LiteralPath $candidate -Force).Length `
            (Get-Item -LiteralPath $legacy -Force).Length `
            (Get-Item -LiteralPath $manifest -Force).Length
    }
    Assert-RebornControlledHostInventoryEqual `
        $expected $current "controlled-host $Mode client inventory" |
        Out-Null
    return [pscustomobject]@{
        Mode = $Mode
        ClientRoot = $current.ClientRoot
        StockInventorySetSha256 = $Receipt.Inventory.SetSha256
        CurrentInventorySetSha256 = $current.SetSha256
        ReceiptPath = $Receipt.ReceiptPath
        ReceiptSha256 = $Receipt.ReceiptSha256
    }
}

function ConvertTo-RebornControlledHostBootTime {
    param([Parameter(Mandatory)][object]$Value)

    if ($Value -is [DateTimeOffset]) {
        return [DateTimeOffset]$Value
    }
    if ($Value -is [DateTime]) {
        $dateTime = [DateTime]$Value
        if ($dateTime.Kind -eq [DateTimeKind]::Unspecified) {
            throw 'The operating-system boot time has ambiguous timezone data.'
        }
        return [DateTimeOffset]::new($dateTime)
    }
    throw 'The operating-system boot time has an unsupported representation.'
}

function Get-RebornControlledHostLastBootTime {
    $operatingSystems = @(
        Get-CimInstance `
            -ClassName Win32_OperatingSystem `
            -ErrorAction Stop
    )
    if ($operatingSystems.Count -ne 1 -or
        $null -eq $operatingSystems[0].LastBootUpTime) {
        throw 'Cannot obtain one authoritative operating-system boot time.'
    }
    return ConvertTo-RebornControlledHostBootTime `
        $operatingSystems[0].LastBootUpTime
}

function Test-RebornControlledHostPostInventoryReboot {
    param(
        [Parameter(Mandatory)][string]$InventoryCreatedUtc,
        [scriptblock]$LastBootTimeProvider = {
            Get-RebornControlledHostLastBootTime
        }
    )

    $issued = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact(
            $InventoryCreatedUtc,
            'O',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$issued) -or
        $issued.Offset -ne [TimeSpan]::Zero) {
        throw 'The protected client inventory issue time is invalid.'
    }
    $observed = @(& $LastBootTimeProvider)
    if ($observed.Count -ne 1 -or $null -eq $observed[0]) {
        throw 'Cannot obtain one authoritative operating-system boot time.'
    }
    $lastBoot = ConvertTo-RebornControlledHostBootTime $observed[0]
    if ($lastBoot.ToUniversalTime() -le $issued.ToUniversalTime()) {
        throw (
            'The controlled-host machine has not rebooted after the ' +
            'protected client inventory was issued.')
    }
    return [pscustomobject]@{
        InventoryCreatedUtc = $issued.ToUniversalTime()
        LastBootUpTimeUtc = $lastBoot.ToUniversalTime()
    }
}

function Assert-RebornControlledHostClientPostInventoryReboot {
    param(
        [Parameter(Mandatory)][string]$ReceiptPath,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedReceiptSha256,
        [scriptblock]$LastBootTimeProvider = {
            Get-RebornControlledHostLastBootTime
        }
    )

    $receipt = Read-RebornControlledHostClientInventoryReceipt `
        $ReceiptPath `
        $ExpectedReceiptSha256
    $gate = Test-RebornControlledHostPostInventoryReboot `
        ([string]$receipt.Record.createdUtc) `
        $LastBootTimeProvider
    return [pscustomobject]@{
        Receipt = $receipt
        InventoryCreatedUtc = $gate.InventoryCreatedUtc
        LastBootUpTimeUtc = $gate.LastBootUpTimeUtc
    }
}

Export-ModuleMember -Function @(
    'Get-RebornControlledHostClientInventoryRoot',
    'New-RebornControlledHostClientInventorySecurity',
    'Initialize-RebornControlledHostClientInventoryRoot',
    'ConvertTo-RebornControlledHostClientInventoryRecord',
    'Read-RebornControlledHostClientInventoryReceipt',
    'Read-RebornControlledHostActiveClientInventoryReceipt',
    'Protect-RebornControlledHostClientInventoryReceipt',
    'Assert-RebornControlledHostClientInventoryReceipt',
    'ConvertTo-RebornControlledHostBootTime',
    'Get-RebornControlledHostLastBootTime',
    'Test-RebornControlledHostPostInventoryReboot',
    'Assert-RebornControlledHostClientPostInventoryReboot'
)
