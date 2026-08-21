$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientPetOwnerMergeEffectColor.ps1'
$installed = 'C:\Godswar Origin'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'reborn-owner-merge-color-' + [guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path $testRoot 'backups'
$assertions = 0

function Assert-True([bool]$Condition, [string]$Label) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Label" }
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    $script:assertions++
    if ($Actual -ne $Expected) {
        throw "Assertion failed: $Label; expected=$Expected actual=$Actual"
    }
}

function Get-Sha256([byte[]]$Data) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($Data)).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-RleSampleOffsets([byte[]]$Data) {
    $textureOffset = 6947
    $footerOffset = 42621
    $cursor = $textureOffset + 18
    $pixels = 0
    $result = [Collections.Generic.List[int]]::new()
    while ($pixels -lt 128 * 128) {
        $packet = $Data[$cursor]
        $cursor++
        $count = ($packet -band 0x7F) + 1
        $samples = if (($packet -band 0x80) -ne 0) { 1 } else { $count }
        for ($index = 0; $index -lt $samples; $index++) {
            $result.Add($cursor)
            $cursor += 4
        }
        $pixels += $count
    }
    Assert-Equal $cursor $footerOffset 'RLE stream ends at footer'
    Assert-Equal $pixels (128 * 128) 'RLE stream decodes the full atlas'
    return ,([int[]]$result.ToArray())
}

try {
    $effectDirectory = Join-Path $testRoot 'Characters\PetUniteEffect'
    [IO.Directory]::CreateDirectory($effectDirectory) | Out-Null
    Copy-Item -LiteralPath (Join-Path $installed 'Origin.exe') `
        -Destination (Join-Path $testRoot 'Origin.exe')
    foreach ($asset in @(
            'e_he_0001_all.gwm',
            'e_he_0002_all.gwm',
            'e_he_0003_all.gwm')) {
        Copy-Item -LiteralPath (Join-Path $installed (
                "Characters\PetUniteEffect\$asset")) `
            -Destination (Join-Path $effectDirectory $asset)
    }

    $initial = & $patcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    if ($initial.Status -eq 'Purple') {
        & $patcher -Mode Revert -ClientRoot $testRoot `
            -BackupRoot $backupRoot | Out-Null
    }
    $sourceStatus = & $patcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $sourceStatus.Status 'Stock' 'fixture starts with stock cyan'
    Assert-Equal $sourceStatus.Hash `
        '89B98361733C4D127CEE984EACD58D7EE1DA098728672B11CB673AA5BA70A2F2' `
        'stock GWM hash'
    Assert-True $sourceStatus.GeometryAnimationMaterialPreserved `
        'stock structure is audited'
    Assert-True $sourceStatus.AlphaAndRlePreserved `
        'stock alpha and RLE are audited'
    Assert-Equal $sourceStatus.EncodedSamples 8706 'encoded sample count'

    $paths = @(
        'e_he_0001_all.gwm',
        'e_he_0002_all.gwm',
        'e_he_0003_all.gwm') | ForEach-Object {
        Join-Path $effectDirectory $_
    }
    [byte[]]$stock0001 = [IO.File]::ReadAllBytes($paths[0])
    [byte[]]$stock0002 = [IO.File]::ReadAllBytes($paths[1])
    [byte[]]$stock0003 = [IO.File]::ReadAllBytes($paths[2])
    $samples = Get-RleSampleOffsets $stock0002

    $applied = & $patcher -Mode Apply -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $applied.Status 'Purple' 'Apply reports Purple'
    Assert-Equal $applied.Hash `
        '7947392068C9FF1ED3C76973C80D37CA6B214493A8EBB90CD1329D4B5DCA7BE9' `
        'purple GWM hash'
    Assert-True (Test-Path -LiteralPath $applied.BackupDirectory) `
        'Apply creates a durable backup'
    [byte[]]$applyBackup = [IO.File]::ReadAllBytes((Join-Path (
            $applied.BackupDirectory) 'e_he_0002_all.gwm'))
    Assert-True ([Linq.Enumerable]::SequenceEqual(
            $stock0002, $applyBackup)) 'Apply backup is exact stock GWM'

    $purpleStatus = & $patcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $purpleStatus.Status 'Purple' 'Status sees Purple'
    Assert-True ($purpleStatus.Color -match 'purple') `
        'Status describes the purple palette'
    [byte[]]$purple0002 = [IO.File]::ReadAllBytes($paths[1])
    Assert-Equal $purple0002.Length $stock0002.Length `
        'palette transform preserves GWM length'
    Assert-True ([Linq.Enumerable]::SequenceEqual(
            $stock0001, [byte[]][IO.File]::ReadAllBytes($paths[0]))) `
        'effect 0001 remains exact'
    Assert-True ([Linq.Enumerable]::SequenceEqual(
            $stock0003, [byte[]][IO.File]::ReadAllBytes($paths[2]))) `
        'effect 0003 remains exact'

    $eligible = [Collections.Generic.HashSet[int]]::new()
    $sampleMismatch = $false
    foreach ($offset in $samples) {
        [void]$eligible.Add($offset + 1)
        [void]$eligible.Add($offset + 2)
        if ($purple0002[$offset] -ne $stock0002[$offset] -or
            $purple0002[$offset + 1] -ne $stock0002[$offset + 2] -or
            $purple0002[$offset + 2] -ne $stock0002[$offset + 1] -or
            $purple0002[$offset + 3] -ne $stock0002[$offset + 3]) {
            $sampleMismatch = $true
            break
        }
    }
    Assert-True (-not $sampleMismatch) `
        'every encoded BGRA sample changes only by an R/G swap'
    $changed = 0
    $unexpected = 0
    for ($offset = 0; $offset -lt $stock0002.Length; $offset++) {
        if ($stock0002[$offset] -eq $purple0002[$offset]) { continue }
        $changed++
        if (-not $eligible.Contains($offset)) { $unexpected++ }
    }
    Assert-Equal $changed 15706 'exact changed-byte count'
    Assert-Equal $unexpected 0 `
        'no GWM bytes outside encoded red/green channels change'

    $stockRed = [uint64]0
    $stockGreen = [uint64]0
    $purpleRed = [uint64]0
    $purpleGreen = [uint64]0
    $purpleBlue = [uint64]0
    foreach ($offset in $samples) {
        $stockRed += $stock0002[$offset + 2]
        $stockGreen += $stock0002[$offset + 1]
        $purpleRed += $purple0002[$offset + 2]
        $purpleGreen += $purple0002[$offset + 1]
        $purpleBlue += $purple0002[$offset]
    }
    Assert-Equal $purpleRed $stockGreen 'green energy becomes purple red'
    Assert-Equal $purpleGreen $stockRed 'stock red becomes purple green'
    Assert-True ($purpleRed -gt $purpleGreen * 2) `
        'purple atlas has much more red than green'
    Assert-True ($purpleBlue -gt $purpleRed) `
        'purple atlas remains blue-led violet'

    $idempotent = & $patcher -Mode Apply -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $idempotent.Status 'Already Purple' 'Apply is idempotent'

    $reverted = & $patcher -Mode Revert -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $reverted.Status 'Stock' 'Revert reports Stock'
    [byte[]]$roundTrip = [IO.File]::ReadAllBytes($paths[1])
    Assert-True ([Linq.Enumerable]::SequenceEqual(
            $stock0002, $roundTrip)) 'Revert restores byte-exact stock GWM'

    [byte[]]$corrupt = $stock0002.Clone()
    $corrupt[6947] = 1
    [IO.File]::WriteAllBytes($paths[1], $corrupt)
    $rejected = $false
    try {
        & $patcher -Mode Status -ClientRoot $testRoot `
            -BackupRoot $backupRoot | Out-Null
    }
    catch { $rejected = $true }
    Assert-True $rejected 'malformed TGA layout is rejected'

    $corrupt = $stock0002.Clone()
    $corrupt[$samples[0] + 1] = $corrupt[$samples[0] + 1] -bxor 1
    [IO.File]::WriteAllBytes($paths[1], $corrupt)
    $rejected = $false
    try {
        & $patcher -Mode Status -ClientRoot $testRoot `
            -BackupRoot $backupRoot | Out-Null
    }
    catch { $rejected = $true }
    Assert-True $rejected 'partial palette state is rejected'

    Write-Host "Owner-Merge effect color patch passed: $assertions assertions."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolved.StartsWith(
                $temp,
                [StringComparison]::OrdinalIgnoreCase) -or
            $resolved.Length -le $temp.Length + 10) {
            throw "Refusing to remove unexpected test path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
