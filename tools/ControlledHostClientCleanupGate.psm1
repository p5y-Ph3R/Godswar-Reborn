Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'ControlledHostClientInventoryReceipt.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'ControlledHostClientRootLease.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkActivationState.psm1'
) -Force

$script:ExpectedClientRoot = 'C:\RebornNetworkAcceptanceClient'

function Assert-RebornControlledHostClientStockAndDisabled {
    param(
        [Parameter(Mandatory)][string]$InventoryReceiptPath,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedInventoryReceiptSha256,
        [Parameter(Mandatory)]
        [ValidateRange(1, 3)]
        [UInt64]$ExpectedActivationEnvironment,
        [Parameter(Mandatory)]
        [UInt64]$ExpectedActivationSequenceFloor
    )

    if ($ExpectedActivationSequenceFloor -eq 0) {
        throw 'Cleanup activation sequence floor must be nonzero.'
    }
    if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
        throw 'Origin.exe must be closed before controlled-host cleanup.'
    }
    $lease =
        Enter-RebornControlledHostClientRootLease `
            $script:ExpectedClientRoot
    try {
        $receipt =
            Read-RebornControlledHostClientInventoryReceipt `
                $InventoryReceiptPath `
                $ExpectedInventoryReceiptSha256
        $inventory =
            Assert-RebornControlledHostClientInventoryReceipt `
                $receipt `
                $script:ExpectedClientRoot `
                Stock
        $activation =
            Assert-RebornProtectedHklmActivationState
        if (-not $activation.Exists -or
            -not $activation.Complete -or
            [UInt64]$activation.Mode -ne 0 -or
            [UInt64]$activation.Environment -ne
                $ExpectedActivationEnvironment -or
            [UInt64]$activation.SequenceFloor -ne
                $ExpectedActivationSequenceFloor) {
            throw (
                'The protected client activation state is not the exact ' +
                'manifest-produced disabled state.')
        }
        Assert-RebornControlledHostClientRootLease $lease |
            Out-Null
        return [pscustomobject]@{
            ClientRoot = $script:ExpectedClientRoot
            InventoryReceiptPath = $inventory.ReceiptPath
            InventoryReceiptSha256 = $inventory.ReceiptSha256
            StockInventorySetSha256 =
                $inventory.CurrentInventorySetSha256
            ActivationExists = [bool]$activation.Exists
            ActivationMode = [UInt64]$activation.Mode
            ActivationEnvironment =
                [UInt64]$activation.Environment
            ActivationSequenceFloor =
                [UInt64]$activation.SequenceFloor
        }
    }
    finally {
        Exit-RebornControlledHostClientRootLease $lease
    }
}

Export-ModuleMember -Function @(
    'Assert-RebornControlledHostClientStockAndDisabled'
)
