[CmdletBinding()]
param(
    [string]$FixtureExe = 'C:\Godswar Origin\Origin.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$patcher = Join-Path $PSScriptRoot 'PatchClientPetSavvyGrowthRefresh.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) (
    'godswar-pet-savvy-growth-' + [guid]::NewGuid().ToString('N'))
$client = Join-Path $root 'Origin.exe'
$sourceClient = Join-Path $root 'Origin-source.exe'
$detailOnlyClient = Join-Path $root 'Origin-detail-only.exe'
$visibleOnlyClient = Join-Path $root 'Origin-visible-only.exe'
$backups = Join-Path $root 'backups'
$sourceHash =
    '9354BDB00376E16F5C2D1E682637790D90C3930B8F3655456F8F49F3314C6728'
$modelOnlyHash =
    '31B4CE0E0445958C7814BCD2572381F9115DE194E0E13CB3ED7502F02C9FB9B2'
$detailOnlyHash =
    'C642C3F9F4F3458BC4DBAD126E06C1661C7F1C418FB63BD037543CA1892D5656'
$visibleOnlyPatchedHash =
    '7B837397F5387186001B7CB155FBADD2B3AA2CA425B7568A21F9C66EDA90A8DA'
$patchedHash =
    '39CC2ECEF6F7428A5870AABB1F16567BC31B9AC671CC5189DD9F790D8FBFF89B'
$assertions = 0

function Convert-HexBytes([string]$Hex) {
    $compact = $Hex -replace '[^0-9A-Fa-f]', ''
    [byte[]]$result = for ($offset = 0; $offset -lt $compact.Length; $offset += 2) {
        [Convert]::ToByte($compact.Substring($offset, 2), 16)
    }
    return ,$result
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Assert-True([bool]$Condition, [string]$Label) {
    if (-not $Condition) {
        throw "$Label failed."
    }
    $script:assertions++
}

function Test-Bytes([byte[]]$Data, [int]$Offset, [byte[]]$Expected) {
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) {
            return $false
        }
    }
    return $true
}

function Get-RelativeTarget(
    [byte[]]$Code,
    [int]$Offset,
    [uint64]$Va
) {
    $Va + $Offset + 5 + [BitConverter]::ToInt32($Code, $Offset + 1)
}

function Assert-OnlyAllowedDifferences(
    [byte[]]$Before,
    [byte[]]$After,
    [int]$ExpectedCount,
    [string]$Label
) {
    $count = 0
    for ($offset = 0; $offset -lt $Before.Length; $offset++) {
        if ($Before[$offset] -eq $After[$offset]) {
            continue
        }
        $count++
        $allowed =
            ($offset -ge 0x5C3480 -and $offset -lt 0x5C34B0) -or
            ($offset -ge 0x5C3658 -and $offset -lt 0x5C36A0)
        if (-not $allowed) {
            throw "$Label changed unexpected offset 0x$($offset.ToString('X'))."
        }
    }
    Assert-Equal $count $ExpectedCount "$Label mutation count"
}

function Normalize-ToModelOnly([string]$Path) {
    $state = & $patcher -Mode Status -ClientExe $Path
    if ($state.PetDetailRedraw -or $state.PetMergeRedraw) {
        & $patcher -Mode Revert -ClientExe $Path -BackupRoot $backups |
            Out-Null
    }
    elseif ($state.Status -eq 'Ready') {
        & $patcher -Mode Apply -ClientExe $Path -BackupRoot $backups |
            Out-Null
        & $patcher -Mode Revert -ClientExe $Path -BackupRoot $backups |
            Out-Null
    }
    Assert-Equal (Get-FileHash $Path -Algorithm SHA256).Hash $modelOnlyHash `
        'normalized model-only hash'
}

try {
    [IO.Directory]::CreateDirectory($root) | Out-Null
    Copy-Item -LiteralPath $FixtureExe -Destination $client
    Normalize-ToModelOnly $client

    $ready = & $patcher -Mode Status -ClientExe $client
    Assert-Equal $ready.Status 'Model refresh only' 'model-only status'
    Assert-Equal $ready.PacketLength 68 'model-only packet length'
    Assert-Equal $ready.PetDetailRedraw $false 'model-only redraw state'
    Assert-Equal $ready.PetMergeRedraw $false `
        'model-only merge redraw state'
    Assert-Equal $ready.HiddenPetMergeRefresh $false `
        'model-only hidden Merge refresh state'

    $before = [IO.File]::ReadAllBytes($client)
    $applied = & $patcher -Mode Apply -ClientExe $client `
        -BackupRoot $backups
    Assert-Equal $applied.Status 'Patched' 'apply status'
    Assert-Equal $applied.PacketLength 68 'patched packet length'
    Assert-Equal $applied.PetDetailRedraw $true 'patched redraw state'
    Assert-Equal $applied.PetMergeRedraw $true `
        'patched merge redraw state'
    Assert-Equal $applied.HiddenPetMergeRefresh $true `
        'patched hidden Merge refresh state'
    Assert-Equal (Get-FileHash $client -Algorithm SHA256).Hash $patchedHash `
        'patched hash'

    $after = [IO.File]::ReadAllBytes($client)
    Assert-OnlyAllowedDifferences $before $after 59 'model-only apply'
    Assert-True (
        Test-Bytes $after 0x5C3484 (Convert-HexBytes '44')
    ) '68-byte exact-length gate preserved'
    Assert-True (
        Test-Bytes $after 0x5C3494 (Convert-HexBytes '0C')
    ) 'twelve-dword copy preserved'

    $oldCaveBranch = Convert-HexBytes 'E9 AA 01 00 00'
    $redrawCode = Convert-HexBytes @'
9C 50 51 52 8B 0D 84 D0 5A 01 85 C9 74 05 E8 F5
17 C0 FF A1 98 D0 5A 01 85 C0 74 15 8B 48 04 85
C9 74 0E 80 B9 0D 01 00 00 00 74 05 E8 D7 A3 BA
FF 5A 59 58 9D E9 D5 E2 CD FF
'@
    $redrawCode[42] = 0x90
    $redrawCode[43] = 0x90
    Assert-True (
        Test-Bytes $redrawCode 35 (
            Convert-HexBytes '80 B9 0D 01 00 00 00 90 90 E8')
    ) 'hidden Merge state falls through to redraw'
    Assert-True (
        -not (Test-Bytes $redrawCode 42 (Convert-HexBytes '74 05'))
    ) 'hidden Merge state has no redraw-skip branch'
    Assert-True (
        Test-Bytes $after 0x5C34A9 $oldCaveBranch
    ) 'copy cave redirects to redraw stub'
    Assert-True (
        Test-Bytes $after 0x5C3658 $redrawCode
    ) 'exact redraw stub'
    Assert-True (
        Test-Bytes $after (0x5C3658 + $redrawCode.Length) (
            [byte[]]::new(72 - $redrawCode.Length))
    ) 'unused redraw reserve remains zero'
    Assert-True (
        Test-Bytes $after 0x5C34B0 (
            Convert-HexBytes '0F B7 46 3A 3D E0 01 00 00 0F 82 6C 00 00 00 3D')
    ) 'adjacent native code remains intact'
    Assert-True (
        Test-Bytes $after 0x1C4E60 (
            Convert-HexBytes @'
8B 41 04 85 C0 74 0F 80 B8 0D 01 00 00 00 74 06
51 E8 4A F8 FF FF C3
'@)
    ) 'visible Pet Detail redraw wrapper pinned'
    Assert-True (
        Test-Bytes $after 0x16DA60 (
            Convert-HexBytes @'
55 8B EC 83 E4 F8 51 57 8B F8 E8 21 00 00 00 57
E8 2B 05 00 00 57 E8 C5 08 00 00 8B C7 E8 AE 0B
00 00
'@)
    ) 'native Pet Merge full redraw routine pinned'

    Assert-Equal (
        Get-RelativeTarget $oldCaveBranch 0 0x009C34A9
    ) 0x009C3658 'copy cave redraw target'
    Assert-Equal (
        Get-RelativeTarget $redrawCode 14 0x009C3658
    ) 0x005C4E60 'redraw wrapper call target'
    Assert-Equal (
        Get-RelativeTarget $redrawCode 44 0x009C3658
    ) 0x0056DA60 'Pet Merge redraw call target'
    Assert-Equal (
        Get-RelativeTarget $redrawCode 53 0x009C3658
    ) 0x006A1967 'handler continuation target'
    Assert-Equal (
        0x009C3658 + 14 + [int][sbyte]$redrawCode[13]
    ) 0x009C366B 'null-controller restore target'
    Assert-True (
        Test-Bytes $redrawCode 0 (
            Convert-HexBytes '9C 50 51 52')
    ) 'flags and volatile registers saved'
    Assert-Equal (
        0x009C3658 + 28 + [int][sbyte]$redrawCode[27]
    ) 0x009C3689 'null Merge controller skips redraw'
    Assert-Equal (
        0x009C3658 + 35 + [int][sbyte]$redrawCode[34]
    ) 0x009C3689 'null Merge window skips redraw'
    Assert-True (
        Test-Bytes $redrawCode 19 (
            Convert-HexBytes @'
A1 98 D0 5A 01 85 C0 74 15 8B 48 04 85 C9 74 0E
80 B9 0D 01 00 00 00 90 90
'@)
    ) 'Merge redraw is null-gated and refreshes hidden cache'
    Assert-True (
        Test-Bytes $redrawCode 49 (
            Convert-HexBytes '5A 59 58 9D')
    ) 'volatile registers and flags restored'

    $idempotent = & $patcher -Mode Apply -ClientExe $client `
        -BackupRoot $backups
    Assert-Equal $idempotent.Status 'Already patched' 'idempotent apply'

    $reverted = & $patcher -Mode Revert -ClientExe $client `
        -BackupRoot $backups
    Assert-Equal $reverted.Status 'Reverted' 'revert status'
    Assert-Equal (Get-FileHash $client -Algorithm SHA256).Hash $modelOnlyHash `
        'byte-exact model-only revert'

    Copy-Item -LiteralPath $client -Destination $sourceClient
    $sourceBytes = [IO.File]::ReadAllBytes($sourceClient)
    $sourceBytes[0x5C3484] = 0x2C
    $sourceBytes[0x5C3494] = 0x06
    [IO.File]::WriteAllBytes($sourceClient, $sourceBytes)
    Assert-Equal (Get-FileHash $sourceClient -Algorithm SHA256).Hash $sourceHash `
        'derived audited 44-byte source hash'
    $sourceBefore = [IO.File]::ReadAllBytes($sourceClient)
    & $patcher -Mode Apply -ClientExe $sourceClient -BackupRoot $backups |
        Out-Null
    $sourceAfter = [IO.File]::ReadAllBytes($sourceClient)
    Assert-OnlyAllowedDifferences $sourceBefore $sourceAfter 61 `
        '44-byte source apply'
    Assert-Equal (Get-FileHash $sourceClient -Algorithm SHA256).Hash `
        $patchedHash '44-byte source converges to patched hash'
    & $patcher -Mode Revert -ClientExe $sourceClient -BackupRoot $backups |
        Out-Null
    Assert-Equal (Get-FileHash $sourceClient -Algorithm SHA256).Hash `
        $modelOnlyHash 'revert retains safe 68-byte model refresh'

    Copy-Item -LiteralPath $sourceClient -Destination $detailOnlyClient
    $detailOnlyBytes = [IO.File]::ReadAllBytes($detailOnlyClient)
    [Array]::Copy(
        $oldCaveBranch,
        0,
        $detailOnlyBytes,
        0x5C34A9,
        $oldCaveBranch.Length)
    $detailOnlyRedraw = Convert-HexBytes @'
9C 50 51 52 8B 0D 84 D0 5A 01 85 C9 74 05 E8 F5
17 C0 FF 5A 59 58 9D E9 F3 E2 CD FF
'@
    [Array]::Copy(
        $detailOnlyRedraw,
        0,
        $detailOnlyBytes,
        0x5C3658,
        $detailOnlyRedraw.Length)
    [IO.File]::WriteAllBytes($detailOnlyClient, $detailOnlyBytes)
    Assert-Equal (Get-FileHash $detailOnlyClient -Algorithm SHA256).Hash `
        $detailOnlyHash 'derived Pet Detail-only predecessor hash'
    $detailOnlyBefore = [IO.File]::ReadAllBytes($detailOnlyClient)
    $upgrade = & $patcher -Mode Apply -ClientExe $detailOnlyClient `
        -BackupRoot $backups
    $detailOnlyAfter = [IO.File]::ReadAllBytes($detailOnlyClient)
    Assert-Equal $upgrade.Status 'Patched' 'detail-only upgrade status'
    Assert-Equal $upgrade.PetMergeRedraw $true `
        'detail-only state upgrades Merge redraw'
    Assert-OnlyAllowedDifferences $detailOnlyBefore $detailOnlyAfter 36 `
        'detail-only upgrade'
    Assert-Equal (Get-FileHash $detailOnlyClient -Algorithm SHA256).Hash `
        $patchedHash 'detail-only predecessor converges to patched hash'

    Copy-Item -LiteralPath $detailOnlyClient -Destination $visibleOnlyClient
    $visibleOnlyBytes = [IO.File]::ReadAllBytes($visibleOnlyClient)
    $visibleOnlyBytes[0x5C3682] = 0x74
    $visibleOnlyBytes[0x5C3683] = 0x05
    [IO.File]::WriteAllBytes($visibleOnlyClient, $visibleOnlyBytes)
    Assert-Equal (
        Get-FileHash $visibleOnlyClient -Algorithm SHA256
    ).Hash $visibleOnlyPatchedHash 'derived visible-only predecessor hash'
    $visibleOnlyState = & $patcher -Mode Status `
        -ClientExe $visibleOnlyClient
    Assert-Equal $visibleOnlyState.PetMergeRedraw $true `
        'visible-only predecessor has Merge redraw'
    Assert-Equal $visibleOnlyState.HiddenPetMergeRefresh $false `
        'visible-only predecessor skips hidden Merge cache'
    $visibleOnlyBefore = [IO.File]::ReadAllBytes($visibleOnlyClient)
    $visibleOnlyUpgrade = & $patcher -Mode Apply `
        -ClientExe $visibleOnlyClient -BackupRoot $backups
    $visibleOnlyAfter = [IO.File]::ReadAllBytes($visibleOnlyClient)
    Assert-Equal $visibleOnlyUpgrade.Status 'Patched' `
        'visible-only successor upgrade status'
    Assert-Equal $visibleOnlyUpgrade.HiddenPetMergeRefresh $true `
        'visible-only successor refreshes hidden Merge cache'
    Assert-OnlyAllowedDifferences $visibleOnlyBefore $visibleOnlyAfter 2 `
        'visible-only successor upgrade'
    Assert-True (
        Test-Bytes $visibleOnlyAfter 0x5C3682 (
            Convert-HexBytes '90 90 E8 D7 A3 BA FF')
    ) 'visible-only successor removes hidden skip only'
    Assert-Equal (
        Get-FileHash $visibleOnlyClient -Algorithm SHA256
    ).Hash $patchedHash 'visible-only predecessor converges to patched hash'

    $bytes = [IO.File]::ReadAllBytes($sourceClient)
    $bytes[0x5C3658] = 0x90
    [IO.File]::WriteAllBytes($sourceClient, $bytes)
    $rejected = $false
    try {
        & $patcher -Mode Status -ClientExe $sourceClient | Out-Null
    }
    catch {
        $rejected = $_.Exception.Message.Contains(
            'Unsupported Origin.exe SHA-256/state')
    }
    Assert-Equal $rejected $true 'unknown build refusal'

    "PASS Pet Savvy Growth client patch: $assertions assertions"
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
