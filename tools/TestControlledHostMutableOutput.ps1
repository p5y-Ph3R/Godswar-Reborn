$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientMutableOutput.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientInventoryCore.psm1'
) -Force

$passed = 0

function Invoke-Check {
    param(
        [Parameter(Mandatory)][scriptblock]$Body,
        [Parameter(Mandatory)][string]$Name
    )

    & $Body
    $script:passed++
    Write-Host "PASS $Name"
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Body,
        [Parameter(Mandatory)][string]$Name
    )

    try {
        & $Body
    }
    catch {
        $script:passed++
        Write-Host "PASS $Name"
        return
    }
    throw "Expected failure did not occur: $Name"
}

function New-TestInventory {
    param([Parameter(Mandatory)][object[]]$Files)

    $set = Get-RebornControlledHostInventorySetSha256 $Files
    return [pscustomobject]@{
        SetSha256 = $set.SetSha256
        Files = $set.Files
    }
}

$expected = New-TestInventory @(
    [pscustomobject]@{
        RelativePath = 'Origin.exe'
        Length = 10
        Sha256 = ('A' * 64)
    },
    [pscustomobject]@{
        RelativePath = 'patcher\patcher.log'
        Length = 20
        Sha256 = ('B' * 64)
    })
$changedLog = New-TestInventory @(
    [pscustomobject]@{
        RelativePath = 'Origin.exe'
        Length = 10
        Sha256 = ('A' * 64)
    },
    [pscustomobject]@{
        RelativePath = 'patcher\patcher.log'
        Length = 30
        Sha256 = ('C' * 64)
    })

Invoke-Check {
    $paths = @(Get-RebornControlledHostWritableOutputFileRelativePaths)
    if ($paths.Count -ne 1 -or
        $paths[0] -cne 'patcher\patcher.log' -or
        (Get-RebornControlledHostMaximumWritableOutputFileBytes) -ne
            16MB) {
        throw 'Exact mutable-output policy changed.'
    }
} 'one exact bounded patcher-log policy'

Invoke-Check {
    Assert-RebornControlledHostInventoryEqual `
        $expected $changedLog 'append-only data output' | Out-Null
} 'patcher-log length and content drift is accepted'

Assert-Throws {
    $changedExecutable = New-TestInventory @(
        [pscustomobject]@{
            RelativePath = 'Origin.exe'
            Length = 11
            Sha256 = ('D' * 64)
        },
        [pscustomobject]@{
            RelativePath = 'patcher\patcher.log'
            Length = 30
            Sha256 = ('C' * 64)
        })
    Assert-RebornControlledHostInventoryEqual `
        $expected $changedExecutable 'executable drift' | Out-Null
} 'non-log drift remains rejected'

Assert-Throws {
    $missingLog = New-TestInventory @(
        [pscustomobject]@{
            RelativePath = 'Origin.exe'
            Length = 10
            Sha256 = ('A' * 64)
        })
    Assert-RebornControlledHostInventoryEqual `
        $expected $missingLog 'missing log' | Out-Null
} 'missing exact output remains rejected'

Assert-Throws {
    $oversizedLog = New-TestInventory @(
        [pscustomobject]@{
            RelativePath = 'Origin.exe'
            Length = 10
            Sha256 = ('A' * 64)
        },
        [pscustomobject]@{
            RelativePath = 'patcher\patcher.log'
            Length = 16MB + 1
            Sha256 = ('C' * 64)
        })
    Assert-RebornControlledHostInventoryEqual `
        $expected $oversizedLog 'oversized log' | Out-Null
} 'oversized exact output remains rejected'

$currentUser =
    [Security.Principal.WindowsIdentity]::GetCurrent().User
Invoke-Check {
    $security = New-RebornControlledHostWritableOutputFileSecurity
    Assert-RebornControlledHostWritableOutputFileSecurity `
        $security $currentUser | Out-Null
} 'narrow issued-user file ACL is accepted'

Assert-Throws {
    $security = New-RebornControlledHostWritableOutputFileSecurity
    $security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $currentUser,
            [Security.AccessControl.FileSystemRights]::Delete,
            [Security.AccessControl.AccessControlType]::Allow))
    Assert-RebornControlledHostWritableOutputFileSecurity `
        $security $currentUser | Out-Null
} 'issued-user delete authority remains rejected'

if ($passed -ne 7) {
    throw "Expected 7 mutable-output checks, got $passed."
}
Write-Host "Controlled-host mutable-output checks passed: $passed"
