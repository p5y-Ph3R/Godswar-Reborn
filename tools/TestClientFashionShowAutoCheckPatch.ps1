[CmdletBinding()]
param(
    [string]$FixtureExe = 'C:\Godswar Origin\Origin.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedBaseHash =
    '92F6740BD0095F869C4FF54E7269CB4E21B8B43BB89A078AF711A5C1973AD181'
$expectedPatchedHash =
    '9354BDB00376E16F5C2D1E682637790D90C3930B8F3655456F8F49F3314C6728'
$patcher = Join-Path $PSScriptRoot 'PatchClientFashionShowAutoCheck.ps1'
$helperRoot = Join-Path $PSScriptRoot 'client_patch_helpers'
. (Join-Path $helperRoot 'AvatarPreviewGuard.Binary.ps1')
. (Join-Path $helperRoot 'FashionShowAutoCheck.Patch.ps1')

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$testRoot = Join-Path $artifactRoot (
    'fashion-show-auto-check-test-' + [guid]::NewGuid().ToString('N'))
$assertionCount = 0

function Assert-Value {
    param($Actual, $Expected, [string]$Label)

    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertionCount++
}

function Assert-True {
    param([bool]$Condition, [string]$Label)

    if (-not $Condition) {
        throw "$Label failed."
    }
    $script:assertionCount++
}

function Assert-Throws {
    param(
        [scriptblock]$Operation,
        [string]$ExpectedMessage,
        [string]$Label
    )

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

function Assert-RelativeBranch {
    param(
        [byte[]]$Code,
        [int]$Offset,
        [uint64]$CodeVa,
        [byte]$Opcode,
        [uint64]$Target,
        [string]$Label
    )

    Assert-Value $Code[$Offset] $Opcode "$Label opcode"
    $actual = $CodeVa + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
    Assert-Value $actual $Target "$Label target"
}

function Assert-OnlyAllowedDifferences {
    param(
        [byte[]]$Before,
        [byte[]]$After,
        [object[]]$AllowedRanges,
        [int]$ExpectedCount,
        [string]$Label
    )

    Assert-Value $After.Length $Before.Length "$Label file length"
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

try {
    if (-not (Test-Path -LiteralPath $FixtureExe -PathType Leaf)) {
        throw "Fixture not found: $FixtureExe"
    }
    $fixturePath = (Resolve-Path -LiteralPath $FixtureExe).Path
    Assert-Value (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash $expectedBaseHash 'source fixture SHA-256'

    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $clientRoot = Join-Path $testRoot 'client'
    $backupRoot = Join-Path $testRoot 'backups'
    [IO.Directory]::CreateDirectory($clientRoot) | Out-Null
    $copy = Join-Path $clientRoot 'Origin.exe'
    Copy-Item -LiteralPath $fixturePath -Destination $copy
    $before = [IO.File]::ReadAllBytes($copy)

    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.Status 'Ready to apply' 'initial status'
    Assert-Value $status.State 'AuditedFashionBase' 'initial state'
    Assert-Value $status.HookVa '0x004ADB4E' 'reported hook VA'
    Assert-Value $status.CaveVa '0x009C3FA0' 'reported cave VA'
    Assert-Value $status.CaveOwnership `
        '0x5C3FA0-0x5C3FFF (exclusive)' 'reported cave ownership'
    Assert-True (
        -not (Test-Path -LiteralPath $backupRoot)
    ) 'Status creates no backup'

    $apply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $apply.Status 'Patched' 'apply status'
    Assert-Value $apply.State 'FashionShowAutoCheckPatched' 'applied state'
    Assert-Value $apply.ChangedBytes 57 'apply mutation count'
    Assert-Value $apply.AfterSha256 $expectedPatchedHash 'apply hash'
    Assert-Value $apply.CaveBytes 67 'cave code length'
    Assert-Value $apply.CaveReserveBytes 96 'cave allocation length'
    Assert-Value (
        Get-FileHash -LiteralPath $apply.Backup -Algorithm SHA256
    ).Hash $expectedBaseHash 'apply backup hash'

    $after = [IO.File]::ReadAllBytes($copy)
    Assert-Value (
        Get-FileHash -LiteralPath $copy -Algorithm SHA256
    ).Hash $expectedPatchedHash 'installed patched hash'
    Assert-OnlyAllowedDifferences $before $after @(
        [pscustomobject]@{ Offset = 0x0ADB4E; Length = 5 },
        [pscustomobject]@{ Offset = 0x5C3FA0; Length = 96 }
    ) 57 'apply'

    $expectedHook = Convert-HexBytes 'E9 4D 64 51 00'
    $expectedCaveCode = Convert-HexBytes @'
9C 60 8D 95 38 74 00 00 3B C2 75 2B 83 B8 F4 00 00
00 00 75 22 E8 F6 F6 BA FF 85 C0 74 19 3B 68 08 75
14 8B 88 08 53 00 00 85 C9 74 0A 8B 11 6A 01 FF 92
DC 00 00 00 61 9D B9 3E 00 00 00 E9 70 9B AE FF
'@
    Assert-True (
        Test-Bytes $after 0x0ADB4E $expectedHook
    ) 'exact hook bytes'
    Assert-True (
        Test-Bytes $after 0x5C3FA0 $expectedCaveCode
    ) 'exact cave bytes'
    Assert-True (
        Test-Bytes $after (0x5C3FA0 + $expectedCaveCode.Length) (
            [byte[]]::new(96 - $expectedCaveCode.Length))
    ) 'unused owned cave tail remains zero'

    Assert-RelativeBranch $expectedHook 0 0x004ADB4E 0xE9 `
        0x009C3FA0 'hook'
    Assert-RelativeBranch $expectedCaveCode 21 0x009C3FA0 0xE8 `
        0x005736B0 'UI accessor call'
    Assert-RelativeBranch $expectedCaveCode 62 0x009C3FA0 0xE9 `
        0x004ADB53 'continuation'

    # Save first: even an early non-slot/replacement exit must restore the
    # original EDX and EFLAGS. EAX must equal actor+0x7438 (slot 12), and the
    # old occupancy marker at record+0xF4 must be zero. Both failures jump to
    # the shared POPAD/POPFD path before replay.
    Assert-True (
        Test-Bytes $expectedCaveCode 0 (
            Convert-HexBytes '9C 60 8D 95 38 74 00 00 3B C2 75 2B')
    ) 'entry preservation and slot-12-only gate'
    Assert-Value (
        10 + 2 + [int][sbyte]$expectedCaveCode[11]
    ) 55 'non-slot-12 restore target'
    Assert-True (
        Test-Bytes $expectedCaveCode 12 (
            Convert-HexBytes '83 B8 F4 00 00 00 00 75 22')
    ) 'empty-to-occupied gate'
    Assert-Value (
        19 + 2 + [int][sbyte]$expectedCaveCode[20]
    ) 55 'replacement restore target'

    # pushfd/pushad protect the native copy context. The UI singleton's actor
    # pointer at +8 must equal EBP, so remote-player equipment cannot change
    # the local checkbox. +0x5308 is Show; vtable+0xDC is its native setter.
    Assert-True (
        Test-Bytes $expectedCaveCode 26 (
            Convert-HexBytes '85 C0 74 19 3B 68 08 75 14')
    ) 'local-actor gate'
    Assert-True (
        Test-Bytes $expectedCaveCode 35 (
            Convert-HexBytes '8B 88 08 53 00 00 85 C9 74 0A')
    ) 'Show control lookup and null gate'
    Assert-True (
        Test-Bytes $expectedCaveCode 45 (
            Convert-HexBytes '8B 11 6A 01 FF 92 DC 00 00 00')
    ) 'native Show checked setter'
    Assert-True (
        Test-Bytes $expectedCaveCode 55 (
            Convert-HexBytes '61 9D B9 3E 00 00 00')
    ) 'context restore and displaced instruction replay'

    $pe = Get-PeMetadata $after
    $mapping = Resolve-ExecutableFileRange $pe 0x5C3FA0 96
    $rdata = @($pe.Sections | Where-Object Name -eq '.rdata')
    Assert-Value $mapping.Section '.rdata' 'cave executable section'
    Assert-Value $mapping.Va 0x009C3FA0 'cave PE VA'
    Assert-Value $rdata.Count 1 'single rdata section'
    Assert-Value (
        [uint64]0x5C3FA0 + 96
    ) (
        [uint64]$rdata[0].RawOffset + $rdata[0].RawSize
    ) 'cave exclusively owns terminal section allocation'
    Assert-Value 0x5C3FA0 (0x5C3F20 + 0x80) `
        'cave starts after character-speed allocation'

    # Native switch and continuation are untouched around the hook.
    Assert-True (
        Test-Bytes $after 0x0ADA48 $before[0x0ADA48..0x0ADB4D]
    ) 'slot-type dispatch remains native'
    Assert-True (
        Test-Bytes $after 0x0ADB53 $before[0x0ADB53..0x0ADB6C]
    ) 'item-copy continuation remains native'

    $backupCount = @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count
    $idempotentApply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $idempotentApply.Status 'Already patched' `
        'idempotent Apply'
    Assert-Value @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count $backupCount 'idempotent Apply creates no backup'
    $patchedStatus = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $patchedStatus.Status 'Patched' 'patched Status'

    $tampered = Join-Path $testRoot 'TamperedOrigin.exe'
    $tamperedBytes = [byte[]]$after.Clone()
    $tamperedBytes[0x5C3FC3] = $tamperedBytes[0x5C3FC3] -bxor 0x01
    [IO.File]::WriteAllBytes($tampered, $tamperedBytes)
    Assert-Throws {
        & $patcher -ClientExe $tampered -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'tampered cave conflict'

    $partial = Join-Path $testRoot 'PartialOrigin.exe'
    $partialBytes = [byte[]]$before.Clone()
    Copy-Bytes $expectedHook $partialBytes 0x0ADB4E
    [IO.File]::WriteAllBytes($partial, $partialBytes)
    Assert-Throws {
        & $patcher -ClientExe $partial -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'partial patch conflict'

    $foreign = Join-Path $testRoot 'ForeignOrigin.exe'
    $foreignBytes = [byte[]]$before.Clone()
    $foreignBytes[$foreignBytes.Length - 1] =
        $foreignBytes[$foreignBytes.Length - 1] -bxor 0xFF
    [IO.File]::WriteAllBytes($foreign, $foreignBytes)
    Assert-Throws {
        & $patcher -ClientExe $foreign -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'foreign hash conflict'

    # Exercise the mutation guard without launching Origin.exe: the current
    # PowerShell host necessarily holds its own exact executable path open.
    $currentProcessPath = (Get-Process -Id $PID).Path
    Assert-Throws {
        Assert-FashionShowAutoCheckProcessClosed $currentProcessPath
    } 'is running' 'running-process mutation guard'

    $revert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $revert.Status 'Reverted' 'revert status'
    Assert-Value $revert.State 'AuditedFashionBase' 'reverted state'
    Assert-Value $revert.ChangedBytes 57 'revert mutation count'
    Assert-Value $revert.AfterSha256 $expectedBaseHash 'revert hash'
    Assert-Value (
        Get-FileHash -LiteralPath $revert.Backup -Algorithm SHA256
    ).Hash $expectedPatchedHash 'revert backup hash'
    Assert-True (
        [Linq.Enumerable]::SequenceEqual(
            [byte[]]$before,
            [byte[]][IO.File]::ReadAllBytes($copy))
    ) 'exact apply/revert rollback roundtrip'

    $idempotentRevert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $idempotentRevert.Status 'Already reverted' `
        'idempotent Revert'
    Assert-Value (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash $expectedBaseHash 'source fixture remains untouched'

    Write-Host "All $assertionCount Fashion Show auto-check assertions passed."
}
finally {
    $resolvedArtifactRoot =
        [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith(
            "$resolvedArtifactRoot\",
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
