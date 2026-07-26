[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentEndpointManifestKeyReceipt.psm1'
) -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Message)

    $text = $null
    try {
        & $Action | Out-Null
    }
    catch {
        $text = $_.Exception.Message
    }
    Assert-True (
        $null -ne $text -and $text -match $Pattern
    ) "$Message; error was: $text"
}

$root = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-key-removal-test-$([guid]::NewGuid().ToString('N'))")
$provider =
    [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
$openOptions = [Security.Cryptography.CngKeyOpenOptions]::None
$productionKeyNames = @(
    'Reborn-Network-Manifest-Development-Current-v1',
    'Reborn-Network-Manifest-Development-Next-v1'
)
$productionBefore = @(
    foreach ($name in $productionKeyNames) {
        [Security.Cryptography.CngKey]::Exists(
            $name, $provider, $openOptions)
    }
)
$testKeyNames = @()

function Test-CngKey {
    param([string]$Name)
    [Security.Cryptography.CngKey]::Exists(
        $Name, $provider, $openOptions)
}

function Remove-TestCngKey {
    param([string]$Name)
    if (-not (Test-CngKey $Name)) {
        return
    }
    $key = [Security.Cryptography.CngKey]::Open(
        $Name, $provider, $openOptions)
    try {
        $key.Delete()
    }
    finally {
        $key.Dispose()
    }
}

[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $currentName =
        'Reborn-Network-Manifest-Development-Current-v1'
    $nextName =
        'Reborn-Network-Manifest-Development-Next-v1'
    $header = Join-Path $root 'keys.h'
    $trust = Join-Path $root 'current.json'
    $nextTrust = Join-Path $root 'next.json'
    $receipt = Join-Path $root 'receipt.json'
    [IO.File]::Copy(
        (Join-Path $PSScriptRoot (
            '..\client\network-shim\src\' +
            'SecureClientManifestDevelopmentKeys.generated.h')),
        $header)
    [IO.File]::Copy(
        (Join-Path $PSScriptRoot (
            '..\artifacts\secure-network\' +
            'development-manifest-trust.json')),
        $trust)
    [IO.File]::Copy(
        (Join-Path $PSScriptRoot (
            '..\artifacts\secure-network\' +
            'development-manifest-next-trust.json')),
        $nextTrust)

    $artifacts = Get-RebornManifestKeyArtifactBinding `
        $header $trust $nextTrust $currentName $nextName
    $current = [pscustomobject]@{
        Name = $currentName
        Algorithm = 'ECDSA_P256'
        KeyUsage = 'Signing'
        ExportPolicy = 'None'
        X = $artifacts.CurrentX
        Y = $artifacts.CurrentY
    }
    $next = [pscustomobject]@{
        Name = $nextName
        Algorithm = 'ECDSA_P256'
        KeyUsage = 'Signing'
        ExportPolicy = 'None'
        X = $artifacts.NextX
        Y = $artifacts.NextY
    }
    $record = New-RebornManifestKeyReceiptRecord `
        $artifacts $current $next
    Write-RebornManifestKeyReceiptAtomic `
        $record $receipt -NoOverwrite
    $loaded = Read-RebornManifestKeyReceipt `
        $receipt $artifacts $currentName $nextName
    Assert-True (
        $loaded.Record.state -eq 'Issued' -and
        -not $loaded.Record.current.removed -and
        -not $loaded.Record.next.removed
    ) 'valid issued receipt was not accepted'

    $wrongCoordinate = $current.PSObject.Copy()
    $wrongCoordinate.X = [Convert]::ToBase64String((New-Object byte[] 32))
    Assert-Throws {
        Assert-RebornManifestKeyDescriptor `
            $wrongCoordinate $currentName `
            $artifacts.CurrentX $artifacts.CurrentY
    } 'does not match' 'wrong public coordinates were accepted'

    $wrongAlgorithm = $current.PSObject.Copy()
    $wrongAlgorithm.Algorithm = 'RSA'
    Assert-Throws {
        Assert-RebornManifestKeyDescriptor `
            $wrongAlgorithm $currentName `
            $artifacts.CurrentX $artifacts.CurrentY
    } 'does not match' 'wrong key algorithm was accepted'

    $wrongExport = $current.PSObject.Copy()
    $wrongExport.ExportPolicy = 'AllowExport'
    Assert-Throws {
        Assert-RebornManifestKeyDescriptor `
            $wrongExport $currentName `
            $artifacts.CurrentX $artifacts.CurrentY
    } 'does not match' 'exportable key policy was accepted'

    Assert-Throws {
        & (Join-Path $PSScriptRoot (
            'ManageDevelopmentEndpointManifestKeys.ps1')) `
            -Mode Remove `
            -CurrentKeyName 'Wrong-Development-Key' `
            -AllowKeyRemoval `
            -Confirm:$false
    } 'two exact development manifest key names' (
        'production removal accepted a caller-selected key name')

    $tampered = Get-Content $receipt -Raw | ConvertFrom-Json
    $tampered.current.x =
        [Convert]::ToBase64String((New-Object byte[] 32))
    [IO.File]::WriteAllText(
        $receipt,
        ($tampered | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        Read-RebornManifestKeyReceipt `
            $receipt $artifacts $currentName $nextName
    } 'cryptographic binding' 'tampered key receipt was accepted'

    $lifecycle = Join-Path $root 'actual-cng-lifecycle'
    [IO.Directory]::CreateDirectory($lifecycle) | Out-Null
    $suffix = [Guid]::NewGuid().ToString('N')
    $testCurrent = "Reborn-Test-Manifest-Current-$suffix"
    $testNext = "Reborn-Test-Manifest-Next-$suffix"
    $testKeyNames = @($testCurrent, $testNext)
    $testHeader = Join-Path $lifecycle 'keys.h'
    $testTrust = Join-Path $lifecycle 'current.json'
    $testNextTrust = Join-Path $lifecycle 'next.json'
    $testReceipt = Join-Path $lifecycle 'receipt.json'
    $keyTool = Join-Path $PSScriptRoot (
        'ManageDevelopmentEndpointManifestKeys.ps1')
    $commonArguments = @{
        CurrentKeyName = $testCurrent
        NextKeyName = $testNext
        HeaderPath = $testHeader
        TrustPath = $testTrust
        NextTrustPath = $testNextTrust
        ReceiptPath = $testReceipt
        AllowTestPath = $true
        AllowTestKeyNames = $true
        Confirm = $false
    }
    & $keyTool -Mode Create @commonArguments | Out-Null
    Assert-True (
        (Test-CngKey $testCurrent) -and
        (Test-CngKey $testNext)
    ) 'ephemeral non-exportable CNG keys were not created'

    $whatIfHashes = @(
        $testHeader,
        $testTrust,
        $testNextTrust,
        $testReceipt
    ) | ForEach-Object {
        (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }
    & $keyTool -Mode Remove @commonArguments `
        -AllowKeyRemoval -WhatIf | Out-Null
    $whatIfHashesAfter = @(
        $testHeader,
        $testTrust,
        $testNextTrust,
        $testReceipt
    ) | ForEach-Object {
        (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }
    Assert-True (
        (Test-CngKey $testCurrent) -and
        (Test-CngKey $testNext) -and
        (($whatIfHashes -join '|') -ceq
            ($whatIfHashesAfter -join '|'))
    ) 'Remove -WhatIf mutated a key or receipt-bound artifact'

    $artifactHashes = @(
        $testHeader,
        $testTrust,
        $testNextTrust
    ) | ForEach-Object {
        (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }
    [IO.File]::Delete($testReceipt)
    & $keyTool -Mode IssueReceipt @commonArguments `
        -AllowReceiptIssue | Out-Null
    $artifactHashesAfter = @(
        $testHeader,
        $testTrust,
        $testNextTrust
    ) | ForEach-Object {
        (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }
    Assert-True (
        (Test-CngKey $testCurrent) -and
        (Test-CngKey $testNext) -and
        (($artifactHashes -join '|') -ceq
            ($artifactHashesAfter -join '|'))
    ) 'IssueReceipt mutated existing keys or public artifacts'
    $validated =
        & $keyTool -Mode ValidateReceipt @commonArguments
    Assert-True (
        $validated.Result -ceq 'Validated' -and
        $validated.ReceiptState -ceq 'Issued' -and
        $validated.PublicCoordinatesBound -and
        -not $validated.PrivateKeysExportable
    ) 'read-only receipt/key-coordinate validation failed'

    Assert-Throws {
        & $keyTool -Mode Remove @commonArguments `
            -AllowKeyRemoval `
            -TestFailurePoint AfterFirstKeyDelete
    } 'after first key deletion' (
        'first-key deletion interruption was not injected')
    $partial = Read-RebornManifestKeyReceipt `
        $testReceipt `
        (Get-RebornManifestKeyArtifactBinding `
            $testHeader $testTrust $testNextTrust `
            $testCurrent $testNext) `
        $testCurrent $testNext
    Assert-True (
        -not (Test-CngKey $testCurrent) -and
        (Test-CngKey $testNext) -and
        $partial.Record.state -ceq 'RemovalPending' -and
        -not $partial.Record.current.removed -and
        -not $partial.Record.next.removed
    ) 'hard crash boundary was not durably recoverable'

    & $keyTool -Mode Remove @commonArguments `
        -AllowKeyRemoval | Out-Null
    $complete = Read-RebornManifestKeyReceipt `
        $testReceipt `
        (Get-RebornManifestKeyArtifactBinding `
            $testHeader $testTrust $testNextTrust `
            $testCurrent $testNext) `
        $testCurrent $testNext
    Assert-True (
        -not (Test-CngKey $testCurrent) -and
        -not (Test-CngKey $testNext) -and
        $complete.Record.state -ceq 'Removed' -and
        $complete.Record.current.removed -and
        $complete.Record.next.removed
    ) 'interrupted key removal did not reconcile on retry'

    $productionAfter = @(
        foreach ($name in $productionKeyNames) {
            [Security.Cryptography.CngKey]::Exists(
                $name, $provider, $openOptions)
        }
    )
    Assert-True (
        ($productionBefore -join '|') -ceq
            ($productionAfter -join '|')
    ) 'ephemeral lifecycle test changed a production key name'

    [pscustomobject]@{
        Result = 'Passed'
        ExactNamePolicy = $true
        CoordinateBinding = $true
        AlgorithmBinding = $true
        NonExportabilityBinding = $true
        ReceiptTamperRefusal = $true
        IssueReceiptReadOnly = $true
        WhatIfReadOnly = $true
        ActualCngInterruptionRecovery = $true
        LiveKeysMutated = $false
    }
}
finally {
    $keyCleanupErrors = @()
    foreach ($name in $testKeyNames) {
        try {
            Remove-TestCngKey $name
        }
        catch {
            $keyCleanupErrors +=
                "$name`: $($_.Exception.Message)"
        }
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
    $remaining = @(
        $testKeyNames | Where-Object { Test-CngKey $_ }
    )
    if ($keyCleanupErrors.Count -ne 0 -or
        $remaining.Count -ne 0) {
        throw (
            'Ephemeral CNG fixture cleanup failed. Errors: ' +
            ($keyCleanupErrors -join '; ') +
            '; remaining: ' + ($remaining -join ', '))
    }
}
