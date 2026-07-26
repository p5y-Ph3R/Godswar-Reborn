[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleTransactionCore.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkActivationState.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkActivationAcl.psm1'
) -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function New-SimulatedRegistry {
    param([AllowNull()][hashtable]$Values)

    [pscustomobject]@{
        Exists = $null -ne $Values
        Values = if ($null -eq $Values) { @{} } else { $Values.Clone() }
    }
}

function Get-SimulatedActivationState {
    param([Parameter(Mandatory)][object]$Registry)

    $modeName = 'ActivationMode'
    $environmentName = 'Environment'
    $floorName = 'HighestAcceptedSequence'
    $modeExists = $Registry.Values.ContainsKey($modeName)
    $environmentExists =
        $Registry.Values.ContainsKey($environmentName)
    $floorExists = $Registry.Values.ContainsKey($floorName)
    [pscustomobject]@{
        Exists = [bool]$Registry.Exists
        ModeExists = $modeExists
        EnvironmentExists = $environmentExists
        SequenceFloorExists = $floorExists
        Complete = (
            $modeExists -and $environmentExists -and $floorExists)
        Mode = if ($modeExists) {
            [UInt64]$Registry.Values[$modeName]
        } else {
            [UInt64]0
        }
        Environment = if ($environmentExists) {
            [UInt64]$Registry.Values[$environmentName]
        } else {
            [UInt64]0
        }
        SequenceFloor = if ($floorExists) {
            [UInt64]$Registry.Values[$floorName]
        } else {
            [UInt64]0
        }
    }
}

function Invoke-SimulatedRegistryCommit {
    param(
        [Parameter(Mandatory)][object]$Registry,
        [Parameter(Mandatory)][object]$Target,
        [Parameter(Mandatory)][string]$FailureStep
    )

    $Registry.Exists = $true
    if ($FailureStep -ceq 'AfterInitialization') {
        throw 'Simulated interruption after registry initialization.'
    }

    $activationModule = Get-Module SecureNetworkActivationState
    & $activationModule {
        param($RegistryState, $Desired, $StopAfter)

        $writer = {
            param($Entry)
            $RegistryState.Values[$Entry.Name] = $Entry.Value
        }
        $flush = {}
        $afterWrite = {
            param($Step)
            if ($Step -ceq $StopAfter) {
                throw "Simulated interruption at $Step."
            }
        }
        Invoke-RebornActivationOrderedValueWrites `
            $Desired $writer $flush $afterWrite
    } $Registry $Target $FailureStep
}

function Test-ReceiptBoundTransition {
    param(
        [Parameter(Mandatory)][object]$State,
        [Parameter(Mandatory)][object]$Receipt,
        [UInt64]$Environment,
        [UInt64]$Floor
    )

    $coreModule = Get-Module SecureNetworkBundleTransactionCore
    return & $coreModule {
        param($Observed, $BoundReceipt, $ExpectedEnvironment, $ExpectedFloor)
        Test-RebornReceiptBoundDisabledActivationTransition `
            $Observed $BoundReceipt $ExpectedEnvironment $ExpectedFloor
    } $State $Receipt $Environment $Floor
}

$receipt = [pscustomobject]@{
    stateBefore = [pscustomobject]@{
        existed = $false
        activationMode = '0'
        environment = '0'
        sequenceFloor = '0'
    }
}
$secureTarget = New-RebornActivationState 1 1 7
$enableFailureSteps = @(
    'AfterInitialization',
    'AfterInactiveMode',
    'AfterSequenceFloor',
    'AfterEnvironment',
    'AfterSecureMode'
)
$enableSnapshots = @{}
foreach ($failureStep in $enableFailureSteps) {
    $registry = New-SimulatedRegistry $null
    try {
        Invoke-SimulatedRegistryCommit `
            $registry $secureTarget $failureStep
    }
    catch {
        Assert-True (
            $_.Exception.Message -match 'Simulated interruption'
        ) "unexpected enable failure at $failureStep"
    }
    $observed = Get-SimulatedActivationState $registry
    $enableSnapshots[$failureStep] = $observed
    if ($failureStep -ceq 'AfterSecureMode') {
        Assert-True (
            $observed.Complete -and
            $observed.Mode -eq 1 -and
            $observed.Environment -eq 1 -and
            $observed.SequenceFloor -eq 7
        ) 'SecureRequired was not the final complete commit'
    } else {
        Assert-True (
            -not $observed.ModeExists -or $observed.Mode -eq 0
        ) "$failureStep accidentally enabled secure routing"
        Assert-True (
            Test-ReceiptBoundTransition $observed $receipt 1 7
        ) "$failureStep was not accepted as a receipt-bound safe transition"
    }
}

$expectedEnableShapes = @{
    AfterInitialization = '---'
    AfterInactiveMode = 'M--'
    AfterSequenceFloor = 'M-F'
    AfterEnvironment = 'MEF'
    AfterSecureMode = 'MEF'
}
foreach ($entry in $expectedEnableShapes.GetEnumerator()) {
    $state = $enableSnapshots[$entry.Key]
    $shape = (
        $(if ($state.ModeExists) { 'M' } else { '-' }) +
        $(if ($state.EnvironmentExists) { 'E' } else { '-' }) +
        $(if ($state.SequenceFloorExists) { 'F' } else { '-' }))
    Assert-True (
        $shape -ceq $entry.Value
    ) "unexpected registry write order at $($entry.Key): $shape"
}

$disabledTarget = New-RebornActivationState 0 1 7
$installed = @{
    ActivationMode = [Int32]1
    Environment = [Int32]1
    HighestAcceptedSequence = [Int64]7
}
foreach ($failureStep in @(
    'AfterInactiveMode',
    'AfterSequenceFloor',
    'AfterEnvironment'
)) {
    $registry = New-SimulatedRegistry $installed
    try {
        Invoke-SimulatedRegistryCommit `
            $registry $disabledTarget $failureStep
    }
    catch {
        Assert-True (
            $_.Exception.Message -match 'Simulated interruption'
        ) "unexpected disable failure at $failureStep"
    }
    $observed = Get-SimulatedActivationState $registry
    Assert-True (
        $observed.Mode -eq 0
    ) "$failureStep left SecureRequired enabled during disable"
    Assert-True (
        Test-ReceiptBoundTransition $observed $receipt 1 7
    ) "$failureStep was not recoverable through the bound receipt"
}

$invalidPartialSecure = [pscustomobject]@{
    Exists = $true
    ModeExists = $true
    EnvironmentExists = $false
    SequenceFloorExists = $false
    Complete = $false
    Mode = [UInt64]1
    Environment = [UInt64]0
    SequenceFloor = [UInt64]0
}
$invalidOutOfOrder = [pscustomobject]@{
    Exists = $true
    ModeExists = $true
    EnvironmentExists = $true
    SequenceFloorExists = $false
    Complete = $false
    Mode = [UInt64]0
    Environment = [UInt64]1
    SequenceFloor = [UInt64]0
}
Assert-True (
    -not (Test-ReceiptBoundTransition $invalidPartialSecure $receipt 1 7) -and
    -not (Test-ReceiptBoundTransition $invalidOutOfOrder $receipt 1 7)
) 'an unsafe or out-of-order activation state was accepted'

$activationModule = Get-Module SecureNetworkActivationAcl
$aclPolicy = {
    param($Owner, $Protected, $Rules)
    & $activationModule {
        param($PolicyOwner, $PolicyProtected, $PolicyRules)
        Test-RebornActivationRegistryAclPolicy `
            $PolicyOwner $PolicyProtected $PolicyRules
    } $Owner $Protected $Rules
}
$readRule = [pscustomobject]@{
    IdentitySid = 'S-1-5-32-545'
    Type = [Security.AccessControl.AccessControlType]::Allow
    Rights = [Security.AccessControl.RegistryRights]::ReadKey
}
$writeRule = [pscustomobject]@{
    IdentitySid = 'S-1-5-32-545'
    Type = [Security.AccessControl.AccessControlType]::Allow
    Rights = [Security.AccessControl.RegistryRights]::SetValue
}
$adminRule = [pscustomobject]@{
    IdentitySid = 'S-1-5-32-544'
    Type = [Security.AccessControl.AccessControlType]::Allow
    Rights = [Security.AccessControl.RegistryRights]::FullControl
}
Assert-True (
    (& $aclPolicy 'S-1-5-32-544' $true @($adminRule, $readRule)).Valid
) 'protected administrator-owned read-only-user ACL was rejected'
Assert-True (
    -not (& $aclPolicy 'S-1-5-21-1-2-3-1001' $true @($readRule)).Valid -and
    -not (& $aclPolicy 'S-1-5-32-544' $false @($readRule)).Valid -and
    -not (& $aclPolicy 'S-1-5-32-544' $true @($writeRule)).Valid
) 'unsafe activation owner, inheritance, or write ACE was accepted'

[pscustomobject]@{
    Result = 'Passed'
    InitializationInterruption = $true
    EnableCommitOrdering = $true
    DisableCommitOrdering = $true
    ReceiptBoundRecovery = $true
    UnsafeStateRefusal = $true
    RegistryAclPolicy = $true
}
