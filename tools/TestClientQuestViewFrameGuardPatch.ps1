[CmdletBinding()]
param(
    [string]$FixtureExe = 'C:\Godswar Origin\Origin.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientQuestViewFrameGuard.ps1'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot 'artifacts'
$testRoot = Join-Path $artifactRoot (
    'quest-view-frame-guard-test-' + [guid]::NewGuid().ToString('N'))
$assertionCount = 0

function Convert-HexBytes([string]$Hex) {
    $normalized = $Hex -replace '\s', ''
    [byte[]]$result = for ($index = 0; $index -lt $normalized.Length;
        $index += 2) {
        [Convert]::ToByte($normalized.Substring($index, 2), 16)
    }
    return $result
}

function Test-Bytes(
    [byte[]]$Data,
    [int]$Offset,
    [byte[]]$Expected
) {
    if ($Offset -lt 0 -or $Offset + $Expected.Length -gt $Data.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) {
            return $false
        }
    }
    return $true
}

function Copy-Bytes(
    [byte[]]$Source,
    [byte[]]$Destination,
    [int]$Offset
) {
    [Array]::Copy($Source, 0, $Destination, $Offset, $Source.Length)
}

function Test-AllowedDifference([int]$Offset, [object[]]$Ranges) {
    foreach ($range in $Ranges) {
        if ($Offset -ge $range.Offset -and
            $Offset -lt $range.Offset + $range.Length) {
            return $true
        }
    }
    return $false
}

function Assert-Value($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertionCount++
}

function Assert-True([bool]$Condition, [string]$Label) {
    if (-not $Condition) {
        throw "$Label failed."
    }
    $script:assertionCount++
}

function Assert-Throws(
    [scriptblock]$Operation,
    [string]$ExpectedMessage,
    [string]$Label
) {
    try {
        & $Operation
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "$Label threw an unexpected error: $($_.Exception.Message)"
        }
        $script:assertionCount++
        return
    }
    throw "Expected operation to be refused: $Label"
}

function Assert-OnlyAllowedDifferences(
    [byte[]]$Before,
    [byte[]]$After,
    [object[]]$AllowedRanges,
    [int]$ExpectedCount,
    [string]$Label
) {
    $count = 0
    for ($offset = 0; $offset -lt $After.Length; $offset++) {
        if ($Before[$offset] -eq $After[$offset]) {
            continue
        }
        $count++
        Assert-True (
            Test-AllowedDifference $offset $AllowedRanges
        ) "$Label allowlisted offset 0x$('{0:X}' -f $offset)"
    }
    Assert-Value $count $ExpectedCount "$Label changed-byte count"
}

function Assert-NearBranch(
    [byte[]]$Code,
    [int]$Offset,
    [uint64]$CodeVa,
    [uint64]$ExpectedTarget,
    [string]$Label
) {
    Assert-Value $Code[$Offset] 0xE9 "$Label opcode"
    $target = $CodeVa + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
    Assert-Value $target $ExpectedTarget "$Label target"
}

function Get-BackupCount([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return 0
    }
    return @(
        Get-ChildItem -LiteralPath $Path -Recurse -File
    ).Count
}

$hookOffset = 0x1DA4C0
$hookVa = 0x005DA4C0
$caveOffset = 0x5C3F00
$caveVa = 0x009C3F00
$caveReserveLength = 0x20
$originalHook = Convert-HexBytes '8B 4E 08 8B 01'
$patchedHook = Convert-HexBytes 'E9 3B 9A 3E 00'
$caveCode = Convert-HexBytes @'
85 F6 74 14 8B 4E 08 85 C9 74 0D 83 7E 0C 00
74 07 8B 01 E9 AD 65 C1 FF C3
'@
$patchedCave = [byte[]]::new($caveReserveLength)
Copy-Bytes $caveCode $patchedCave 0
$emptyCave = [byte[]]::new($caveReserveLength)
$allowedRanges = @(
    [pscustomobject]@{ Offset = $hookOffset; Length = $patchedHook.Length },
    [pscustomobject]@{ Offset = $caveOffset; Length = $caveReserveLength }
)

try {
    if (-not (Test-Path -LiteralPath $FixtureExe -PathType Leaf)) {
        throw "QuestView frame-guard fixture not found: $FixtureExe"
    }
    $fixturePath = (Resolve-Path -LiteralPath $FixtureExe).Path
    $fixtureHash = (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash

    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $clientRoot = Join-Path $testRoot 'client'
    $backupRoot = Join-Path $testRoot 'backups'
    [IO.Directory]::CreateDirectory($clientRoot) | Out-Null
    $copy = Join-Path $clientRoot 'Origin.exe'
    Copy-Item -LiteralPath $fixturePath -Destination $copy

    # Normalize only the isolated copy so the test supports either recognized
    # source state without ever changing the supplied fixture.
    $initialStatus = & $patcher -ClientExe $copy -Mode Status
    Assert-True (
        $initialStatus.State -in @('Original', 'Patched')
    ) 'Fixture has a recognized guard state'
    if ($initialStatus.State -eq 'Patched') {
        & $patcher -ClientExe $copy -Mode Revert `
            -BackupRoot $backupRoot | Out-Null
    }

    $statusBackupCount = Get-BackupCount $backupRoot
    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.State 'Original' 'Normalized initial state'
    Assert-Value $status.Changed $false 'Status is read-only'
    Assert-Value $status.CaveReserveBytes 32 'Pinned cave ownership size'
    Assert-Value $status.GuardedObject $true 'Guarded object pointer'
    Assert-Value $status.GuardedRoots 2 'Guarded root count'
    Assert-Value (Get-BackupCount $backupRoot) $statusBackupCount `
        'Status creates no backup'

    [byte[]]$before = [IO.File]::ReadAllBytes($copy)
    $beforeHash = (
        Get-FileHash -LiteralPath $copy -Algorithm SHA256
    ).Hash
    Assert-True (Test-Bytes $before $hookOffset $originalHook) `
        'Original hook bytes'
    Assert-True (Test-Bytes $before $caveOffset $emptyCave) `
        'Owned original cave range is zero'

    $apply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $apply.State 'Patched' 'Applied state'
    Assert-Value $apply.Changed $true 'Apply changed state'
    Assert-Value $apply.ChangedBytes 29 'Exact apply mutation count'
    Assert-Value (
        Get-FileHash -LiteralPath $apply.Backup -Algorithm SHA256
    ).Hash $beforeHash 'Verified apply backup hash'

    [byte[]]$after = [IO.File]::ReadAllBytes($copy)
    Assert-OnlyAllowedDifferences $before $after $allowedRanges 29 'Apply'
    Assert-True (Test-Bytes $after $hookOffset $patchedHook) `
        'Exact patched hook bytes'
    Assert-True (Test-Bytes $after $caveOffset $patchedCave) `
        'Exact owned patched cave range'
    Assert-True (
        Test-Bytes $after ($caveOffset + $caveCode.Length) (
            [byte[]]::new($caveReserveLength - $caveCode.Length))
    ) 'Unused cave reserve remains zero'

    Assert-NearBranch $patchedHook 0 $hookVa $caveVa 'Hook branch'
    Assert-NearBranch $caveCode 19 $caveVa 0x005DA4C5 `
        'Ready continuation branch'
    foreach ($branchOffset in @(2, 9, 15)) {
        Assert-Value $caveCode[$branchOffset] 0x74 `
            "Null branch at cave+$branchOffset opcode"
        Assert-Value (
            $branchOffset + 2 +
                [int][sbyte]$caveCode[$branchOffset + 1]
        ) 24 "Null branch at cave+$branchOffset return target"
    }
    Assert-True (
        Test-Bytes $caveCode 0 (Convert-HexBytes '85 F6')
    ) 'Owning object pointer validation'
    Assert-Value $caveCode[24] 0xC3 'Missing-pointer return opcode'
    Assert-True (
        Test-Bytes $caveCode 4 (Convert-HexBytes '8B 4E 08')
    ) 'First displaced instruction replay'
    Assert-True (
        Test-Bytes $caveCode 17 (Convert-HexBytes '8B 01')
    ) 'Second displaced instruction replay'
    Assert-True (
        Test-Bytes $after ($hookOffset + $originalHook.Length) (
            $before[($hookOffset + $originalHook.Length)..
                ($hookOffset + $originalHook.Length + 63)])
    ) 'Native function suffix remains byte-identical'

    $backupCount = Get-BackupCount $backupRoot
    $idempotentApply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $idempotentApply.Changed $false 'Idempotent Apply'
    Assert-Value (Get-BackupCount $backupRoot) $backupCount `
        'Idempotent Apply creates no backup'

    $revert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $revert.State 'Original' 'Reverted state'
    Assert-Value $revert.Changed $true 'Revert changed state'
    Assert-Value $revert.ChangedBytes 29 'Exact revert mutation count'
    Assert-True (
        [Linq.Enumerable]::SequenceEqual(
            [byte[]]$before,
            [byte[]][IO.File]::ReadAllBytes($copy))
    ) 'Apply/Revert exact byte roundtrip'

    $backupCount = Get-BackupCount $backupRoot
    $idempotentRevert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $idempotentRevert.Changed $false 'Idempotent Revert'
    Assert-Value (Get-BackupCount $backupRoot) $backupCount `
        'Idempotent Revert creates no backup'

    $hookOnly = Join-Path $testRoot 'HookOnly.exe'
    [byte[]]$partialBytes = $before.Clone()
    Copy-Bytes $patchedHook $partialBytes $hookOffset
    [IO.File]::WriteAllBytes($hookOnly, $partialBytes)
    Assert-Throws {
        & $patcher -ClientExe $hookOnly -Mode Status | Out-Null
    } 'unknown or partially applied' 'Hook-only partial state'

    $caveOnly = Join-Path $testRoot 'CaveOnly.exe'
    [byte[]]$partialBytes = $before.Clone()
    Copy-Bytes $patchedCave $partialBytes $caveOffset
    [IO.File]::WriteAllBytes($caveOnly, $partialBytes)
    Assert-Throws {
        & $patcher -ClientExe $caveOnly -Mode Status | Out-Null
    } 'unknown or partially applied' 'Cave-only partial state'

    $foreignTail = Join-Path $testRoot 'ForeignCaveTail.exe'
    [byte[]]$partialBytes = $after.Clone()
    $partialBytes[$caveOffset + $caveReserveLength - 1] = 0xCC
    [IO.File]::WriteAllBytes($foreignTail, $partialBytes)
    Assert-Throws {
        & $patcher -ClientExe $foreignTail -Mode Status | Out-Null
    } 'unknown or partially applied' 'Foreign cave-tail state'

    Assert-Value (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash $fixtureHash 'Source fixture remains untouched'

    Write-Host (
        "All $assertionCount QuestView frame-guard assertions passed.")
}
finally {
    $resolvedArtifactRoot = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith(
            "$resolvedArtifactRoot\",
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
