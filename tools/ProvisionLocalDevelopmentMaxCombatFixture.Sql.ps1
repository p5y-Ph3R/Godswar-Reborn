Set-StrictMode -Version Latest

function Get-MaxFixtureSqlText([string]$FixtureDirectory) {
    $names = @(
        '01_identity.sql',
        '02_equipment.sql',
        '03_progression.sql',
        '04_pets.sql',
        '05_verify.sql'
    )
    $parts = foreach ($name in $names) {
        $path = Join-Path $FixtureDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing max-combat SQL fragment '$path'."
        }
        Get-Content -Raw -LiteralPath $path
    }
    return $parts -join "`n"
}

function Get-MaxFixtureRegularValues([string]$FixtureDirectory) {
    $path = Join-Path $FixtureDirectory '02_equipment.sql'
    $sql = Get-Content -Raw -LiteralPath $path
    $pattern = '(?s)INSERT INTO fixture_regular VALUES\s*' +
        '(?<values>.*?)\s*;\s*CREATE TEMP TABLE fixture_mount'
    $match = [regex]::Match($sql, $pattern)
    if (-not $match.Success) {
        throw 'Could not extract the canonical regular-equipment manifest.'
    }
    $values = $match.Groups['values'].Value.Trim()
    if ([regex]::Matches(
            $values,
            "\('(warrior|champion_dodge|champion_glass)',").Count -ne 34) {
        throw 'Canonical regular-equipment manifest must contain 34 rows.'
    }
    return $values
}
