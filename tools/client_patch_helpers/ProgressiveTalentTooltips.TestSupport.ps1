Set-StrictMode -Version Latest

$script:ProgressiveTalentAssertions = 0

function Assert-ProgressiveTalentTrue([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "Assertion failed: $Label" }
    $script:ProgressiveTalentAssertions++
}

function Assert-ProgressiveTalentEqual($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:ProgressiveTalentAssertions++
}

function Assert-ProgressiveTalentThrows(
    [scriptblock]$Operation,
    [string]$Fragment,
    [string]$Label
) {
    try { & $Operation }
    catch {
        Assert-ProgressiveTalentTrue (
            $_.Exception.Message -like "*$Fragment*") $Label
        return
    }
    throw "Expected '$Label' to throw '$Fragment'."
}

function Test-ProgressiveTalentSameBytes([byte[]]$Left, [byte[]]$Right) {
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) { return $false }
    }
    return $true
}

function Test-ProgressiveTalentAllowedOffset(
    [int]$Offset,
    [object[]]$Ranges
) {
    foreach ($range in $Ranges) {
        if ($Offset -ge $range.Offset -and
            $Offset -lt $range.Offset + $range.Length) {
            return $true
        }
    }
    return $false
}

function Get-ProgressiveTalentBinaryDifferences(
    [byte[]]$Before,
    [byte[]]$After
) {
    if ($Before.Length -ne $After.Length) {
        throw 'Binary comparison requires equal lengths.'
    }
    $result = @()
    for ($index = 0; $index -lt $Before.Length; $index++) {
        if ($Before[$index] -ne $After[$index]) { $result += $index }
    }
    return $result
}

function Get-ProgressiveTalentAbsoluteRangeReferences(
    [byte[]]$Data,
    [uint32]$StartVa,
    [uint32]$EndVa
) {
    $result = @()
    for ($offset = 0; $offset -le $Data.Length - 4; $offset++) {
        $value = [BitConverter]::ToUInt32($Data, $offset)
        if ($value -ge $StartVa -and $value -lt $EndVa) {
            $result += [pscustomobject]@{ Offset = $offset; Value = $value }
        }
    }
    return $result
}

function Get-ProgressiveTalentExpectedRank([int]$Rank) {
    $value = [Math]::Min([Math]::Max($Rank, 0), 100)
    if ($value -le 40) { return $value }
    if ($value -le 60) { return (2 * $value) - 40 }
    if ($value -le 80) { return (3 * $value) - 100 }
    if ($value -le 90) { return (5 * $value) - 260 }
    return (7 * $value) - 440
}

function Get-ProgressiveTalentSignedByte([byte]$Value) {
    if ($Value -lt 128) { return [int]$Value }
    return [int]$Value - 256
}

function Invoke-ProgressiveTalentHelperModel(
    [byte[]]$Code,
    [int]$EntryOffset,
    [byte]$RawRank
) {
    $pc = $EntryOffset
    $ecx = 0
    $carry = $false
    $zero = $false
    $written = $null
    for ($steps = 0; $steps -lt 50; $steps++) {
        if ($pc -lt 0 -or $pc -ge $Code.Length) {
            throw "Helper execution escaped at offset $pc."
        }
        if ($pc + 3 -lt $Code.Length -and $Code[$pc] -eq 0x0F -and
            $Code[$pc + 1] -eq 0xB6 -and $Code[$pc + 2] -eq 0x48 -and
            $Code[$pc + 3] -eq 0x25) {
            $ecx = [int]$RawRank
            $pc += 4
            continue
        }
        if ($pc + 2 -lt $Code.Length -and $Code[$pc] -eq 0x83 -and
            $Code[$pc + 1] -eq 0xF9) {
            $operand = [int]$Code[$pc + 2]
            $carry = $ecx -lt $operand
            $zero = $ecx -eq $operand
            $pc += 3
            continue
        }
        if ($pc + 2 -lt $Code.Length -and $Code[$pc] -eq 0x83 -and
            $Code[$pc + 1] -eq 0xD1 -and $Code[$pc + 2] -eq 0) {
            $ecx += if ($carry) { 1 } else { 0 }
            $pc += 3
            continue
        }
        if ($pc + 1 -lt $Code.Length -and $Code[$pc] -eq 0x76) {
            $displacement = Get-ProgressiveTalentSignedByte $Code[$pc + 1]
            $pc = if ($carry -or $zero) {
                $pc + 2 + $displacement
            } else { $pc + 2 }
            continue
        }
        if ($pc + 1 -lt $Code.Length -and $Code[$pc] -eq 0xEB) {
            $pc += 2 + (Get-ProgressiveTalentSignedByte $Code[$pc + 1])
            continue
        }
        if ($pc + 4 -lt $Code.Length -and $Code[$pc] -eq 0xB9) {
            $ecx = [BitConverter]::ToInt32($Code, $pc + 1)
            $pc += 5
            continue
        }
        if ($pc + 2 -lt $Code.Length -and $Code[$pc] -eq 0x6B -and
            $Code[$pc + 1] -eq 0xC9) {
            $ecx *= [int]$Code[$pc + 2]
            $pc += 3
            continue
        }
        if ($pc + 5 -lt $Code.Length -and $Code[$pc] -eq 0x81 -and
            $Code[$pc + 1] -eq 0xE9) {
            $ecx -= [BitConverter]::ToInt32($Code, $pc + 2)
            $pc += 6
            continue
        }
        if ($pc + 2 -lt $Code.Length -and $Code[$pc] -eq 0x83 -and
            $Code[$pc + 1] -eq 0xE9) {
            $ecx -= [int]$Code[$pc + 2]
            $pc += 3
            continue
        }
        if ($pc + 1 -lt $Code.Length -and $Code[$pc] -eq 0x01 -and
            $Code[$pc + 1] -eq 0xC9) {
            $ecx += $ecx
            $pc += 2
            continue
        }
        if ($pc + 3 -lt $Code.Length -and $Code[$pc] -eq 0x89 -and
            $Code[$pc + 1] -eq 0x4C -and $Code[$pc + 2] -eq 0x24 -and
            $Code[$pc + 3] -eq 0x18) {
            $written = $ecx
            $pc += 4
            continue
        }
        if ($Code[$pc] -eq 0xC3) {
            if ($null -eq $written) { throw 'Helper returned before output.' }
            return $written
        }
        throw ('Unsupported helper opcode at +0x{0:X}: 0x{1:X2}.' -f
            $pc, $Code[$pc])
    }
    throw 'Helper execution exceeded its step bound.'
}

function New-ProgressiveTalentTestClient(
    [string]$FixtureRoot,
    [string]$Destination
) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'Origin.exe') `
        -Destination (Join-Path $Destination 'Origin.exe')
    foreach ($locale in @('en_us', 'zh_cn')) {
        $directory = Join-Path $Destination (
            "Localization\$locale\Settings\Sys")
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        Copy-Item -LiteralPath (Join-Path $FixtureRoot (
                "Localization\$locale\Settings\Sys\Skill.ini")) `
            -Destination (Join-Path $directory 'Skill.ini')
    }
    $binaryProfile = Get-ProgressiveTalentBinaryProfile
    $originPath = Join-Path $Destination 'Origin.exe'
    [byte[]]$origin = [IO.File]::ReadAllBytes($originPath)
    [byte[]]$source = Convert-ProgressiveTalentBinary (
        $origin) $binaryProfile 'Original'
    [IO.File]::WriteAllBytes($originPath, $source)

    $enPath = Join-Path $Destination (
        'Localization\en_us\Settings\Sys\Skill.ini')
    $enProfile = Get-ProgressiveTalentSkillProfile 'en_us'
    [byte[]]$en = [IO.File]::ReadAllBytes($enPath)
    [byte[]]$tooltip = Convert-ProgressiveTalentSkillBytes (
        $en) $enProfile 'ChampionTooltip'
    [IO.File]::WriteAllBytes($enPath, $tooltip)

    $zhPath = Join-Path $Destination (
        'Localization\zh_cn\Settings\Sys\Skill.ini')
    $zhProfile = Get-ProgressiveTalentSkillProfile 'zh_cn'
    [byte[]]$zh = [IO.File]::ReadAllBytes($zhPath)
    [byte[]]$stockZh = Convert-ProgressiveTalentSkillBytes (
        $zh) $zhProfile 'Stock'
    [IO.File]::WriteAllBytes($zhPath, $stockZh)
}

function Get-ProgressiveTalentTestFileMap([string]$ClientRoot) {
    return @{
        Origin = Join-Path $ClientRoot 'Origin.exe'
        En = Join-Path $ClientRoot (
            'Localization\en_us\Settings\Sys\Skill.ini')
        Zh = Join-Path $ClientRoot (
            'Localization\zh_cn\Settings\Sys\Skill.ini')
    }
}

function Get-ProgressiveTalentTestBytes([hashtable]$Files) {
    $result = @{}
    foreach ($key in $Files.Keys) {
        $result[$key] = [IO.File]::ReadAllBytes($Files[$key])
    }
    return $result
}

function Assert-ProgressiveTalentTestBytes(
    [hashtable]$Files,
    [hashtable]$Expected,
    [string]$Label
) {
    foreach ($key in $Files.Keys) {
        Assert-ProgressiveTalentTrue (
            Test-ProgressiveTalentSameBytes $Expected[$key] (
                [IO.File]::ReadAllBytes($Files[$key]))) "$Label $key bytes"
    }
}
