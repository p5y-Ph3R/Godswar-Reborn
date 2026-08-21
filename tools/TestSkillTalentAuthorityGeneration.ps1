param(
    [string]$ClientRoot = "C:\Godswar Origin"
)

$ErrorActionPreference = "Stop"

$authorityPath = Join-Path $PSScriptRoot (
    "template-generators\skill-talent\TalentScalarAuthority.ps1")
. $authorityPath

$expectedTalentCount = 73
$relativeInputs = @(
    "Localization\en_us\Settings\Sys\Skill.ini",
    "Localization\en_us\Settings\Sys\Magic.ini",
    "Localization\en_us\Settings\Sys\ItemBaseAttribute.xml",
    "Localization\en_us\Text\EquipName.dat",
    "Localization\en_us\Text\SkillInfo.dat"
)
$generator = Join-Path $PSScriptRoot "GenerateSkillTalentTemplates.ps1"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = Join-Path (
    [System.IO.Path]::GetTempPath()
) ("godswar-skill-authority-" + [guid]::NewGuid().ToString("N"))

function Copy-MinimalClient([string]$destination) {
    foreach ($relative in $relativeInputs) {
        $source = Join-Path $ClientRoot $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required client source is missing: $source"
        }

        $target = Join-Path $destination $relative
        [void](New-Item -ItemType Directory -Force (
            Split-Path -Parent $target))
        Copy-Item -LiteralPath $source -Destination $target
    }
}

function Get-TalentEffectKeys([string]$path) {
    $keys = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $insideEffectSection = $false
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[(.+)\]$') {
            $insideEffectSection = $Matches[1] -eq "Effect"
            continue
        }
        if ($insideEffectSection -and
            $trimmed -match '^Effect\d+=(.+)$') {
            [void]$keys.Add($Matches[1].Trim())
        }
    }
    return $keys
}

function Set-TalentVector(
    [string]$client,
    [hashtable]$classModes,
    [hashtable]$valueOverrides = @{}
) {
    $path = Join-Path $client $relativeInputs[0]
    $effectKeys = Get-TalentEffectKeys $path
    $currentId = -1
    $seen = [System.Collections.Generic.HashSet[int]]::new()
    $formatIntegers = [System.Collections.Generic.HashSet[int]]::new(
        [int[]]@(0, 50, 100, 150))
    $formatFractions = [System.Collections.Generic.HashSet[int]]::new(
        [int[]]@(5, 55, 112, 161))
    $lines = foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        if ($line -match '^\[(\d+)\]$') {
            $currentId = [int]$Matches[1]
        }

        if ($talentAuthorityDefinitions.ContainsKey($currentId) -and
            $line -match '^([^=]+)=(.*)$' -and
            $effectKeys.Contains($Matches[1])) {
            $definition = $talentAuthorityDefinitions[$currentId]
            $classId = [int]$definition.ClassId
            if (-not $classModes.ContainsKey($classId)) {
                throw "Fixture has no mode for talent class $classId."
            }

            $effectPair = $Matches[2].Split(',', 2)
            if ($effectPair.Count -ne 2) {
                throw "Talent $currentId has a malformed effect pair."
            }
            $value = if ($valueOverrides.ContainsKey($currentId)) {
                [decimal]$valueOverrides[$currentId]
            } elseif ($classModes[$classId] -eq "Stock") {
                [decimal]$definition.Value
            } elseif ($classModes[$classId] -eq "Tooltip") {
                [decimal]$definition.Value * $talentTooltipScale
            } else {
                throw "Fixture class $classId has an unknown mode."
            }

            [void]$seen.Add($currentId)
            $valueText = Format-TalentScalar $value
            if ($formatIntegers.Contains($currentId) -and
                -not $valueText.Contains('.')) {
                $valueText += '.0'
            } elseif ($formatFractions.Contains($currentId)) {
                $valueText += '0'
            }
            "$($Matches[1])=$($effectPair[0]),$valueText"
            continue
        }

        $line
    }
    if ($seen.Count -ne $expectedTalentCount) {
        throw (
            "Fixture rewrote $($seen.Count) talents; expected " +
            "$expectedTalentCount.")
    }
    [System.IO.File]::WriteAllLines($path, $lines)
}

function Invoke-FixtureGeneration(
    [string]$name,
    [hashtable]$classModes,
    [hashtable]$valueOverrides = @{}
) {
    $client = Join-Path $temporaryRoot "$name-client"
    $output = Join-Path $temporaryRoot "$name-output"
    Copy-MinimalClient $client
    Set-TalentVector $client $classModes $valueOverrides
    [void](New-Item -ItemType Directory -Force $output)
    & $generator `
        -ClientRoot $client `
        -CSharpOutputPath (Join-Path $output "SkillTalentSeed.Generated.cs") `
        -SqlOutputPath (Join-Path $output "006_skills_and_talents.sql")
    return $output
}

function Assert-OutputTreesEqual([string]$left, [string]$right) {
    $leftFiles = @(Get-ChildItem -LiteralPath $left -File | Sort-Object Name)
    $rightFiles = @(Get-ChildItem -LiteralPath $right -File | Sort-Object Name)
    if (($leftFiles.Name -join "|") -ne ($rightFiles.Name -join "|")) {
        throw "Talent authority variants emitted different file sets."
    }
    for ($index = 0; $index -lt $leftFiles.Count; $index++) {
        $leftHash = (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $leftFiles[$index].FullName).Hash
        $rightHash = (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $rightFiles[$index].FullName).Hash
        if ($leftHash -ne $rightHash) {
            throw "Talent authority outputs differ: $($leftFiles[$index].Name)"
        }
    }
}

function Assert-GenerationRejected(
    [string]$name,
    [hashtable]$classModes,
    [hashtable]$valueOverrides,
    [string]$messagePattern
) {
    $client = Join-Path $temporaryRoot "$name-client"
    $output = Join-Path $temporaryRoot "$name-output"
    Copy-MinimalClient $client
    Set-TalentVector $client $classModes $valueOverrides
    [void](New-Item -ItemType Directory -Force $output)
    $rejected = $false
    try {
        & $generator `
            -ClientRoot $client `
            -CSharpOutputPath (Join-Path $output "SkillTalentSeed.Generated.cs") `
            -SqlOutputPath (Join-Path $output "006_skills_and_talents.sql")
    } catch {
        $rejected = $_.Exception.Message -match $messagePattern
    }
    if (-not $rejected) {
        throw "Fixture $name did not fail closed as expected."
    }
}

$stockModes = @{ 0 = "Stock"; 1 = "Stock"; 2 = "Stock"; 3 = "Stock" }
$tooltipModes = @{
    0 = "Tooltip"; 1 = "Tooltip"; 2 = "Tooltip"; 3 = "Tooltip"
}
$reviewedMixedModes = @{
    0 = "Stock"; 1 = "Tooltip"; 2 = "Stock"; 3 = "Stock"
}

try {
    [void](New-Item -ItemType Directory -Force $temporaryRoot)
    $stockOutput = Invoke-FixtureGeneration "stock" $stockModes
    $tooltipOutput = Invoke-FixtureGeneration "tooltip" $tooltipModes
    $reviewedMixedOutput = Invoke-FixtureGeneration `
        "reviewed-mixed" `
        $reviewedMixedModes
    Assert-OutputTreesEqual $stockOutput $tooltipOutput
    Assert-OutputTreesEqual $stockOutput $reviewedMixedOutput

    $sqlPath = Join-Path $stockOutput "006_skills_and_talents.sql"
    $sql = [System.IO.File]::ReadAllText($sqlPath)
    if ($sql -notmatch "(?m)^\s*\(55, 1, 5, .* 11, 0\.013, true,") {
        throw "Legacy SQL does not preserve ID 55 tooltip scalar 0.013."
    }
    $committedSql = Join-Path $repositoryRoot `
        "database\postgres\006_skills_and_talents.sql"
    if ((Get-FileHash $sqlPath).Hash -ne (Get-FileHash $committedSql).Hash) {
        throw "Generated legacy SQL differs from committed migration 006."
    }

    foreach ($generated in Get-ChildItem -LiteralPath $stockOutput `
                 -Filter "SkillTalentSeed.Generated*.cs") {
        $committed = Join-Path $repositoryRoot (
            "src\Godswar.Server\State\" + $generated.Name)
        if (-not (Test-Path -LiteralPath $committed -PathType Leaf) -or
            (Get-FileHash $generated.FullName).Hash -ne
                (Get-FileHash $committed).Hash) {
            throw "Generated C# differs from committed $($generated.Name)."
        }
    }

    $talentText = @(
        Get-ChildItem -LiteralPath $stockOutput `
            -Filter "SkillTalentSeed.Generated.Talents.Chunk*.cs" |
            ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }
    ) -join "`n"
    $stockPatterns = @(
        'new\(5, 0, \d+, "Dueling".* 11, 0\.004m, true,',
        'new\(55, 1, \d+, "Archaian Spearplay".* 11, 0\.005m, true,',
        'new\(161, 2, \d+, "Elder Priest".* 17, 0\.02m, true,',
        'new\(112, 3, \d+, "Sorceror''s Strength".* 12, 0\.006m, true,'
    )
    foreach ($pattern in $stockPatterns) {
        if ($talentText -notmatch $pattern) {
            throw "Generated C# does not seal every class to stock authority."
        }
    }

    foreach ($mixed in @(
        @{ ClassId = 0; TalentId = 5 },
        @{ ClassId = 1; TalentId = 55 },
        @{ ClassId = 2; TalentId = 161 },
        @{ ClassId = 3; TalentId = 112 }
    )) {
        $talentId = [int]$mixed.TalentId
        $tooltip = [decimal]$talentAuthorityDefinitions[$talentId].Value *
            $talentTooltipScale
        Assert-GenerationRejected `
            "mixed-class-$($mixed.ClassId)" `
            $stockModes `
            @{ $talentId = $tooltip } `
            '(?i)mixes stock and tooltip'
    }
    Assert-GenerationRejected `
        "unknown-warrior" `
        $stockModes `
        @{ 5 = [decimal]0.777 } `
        '(?i)unexpected scalar'

    Write-Host (
        "PASS stock, tooltip, and reviewed per-class vectors generate " +
        "identical legacy SQL and all-class server authority.")
} finally {
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith(
            $resolvedSystemTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
