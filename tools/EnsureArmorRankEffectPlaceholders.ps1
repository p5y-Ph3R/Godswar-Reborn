param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [int[]]$Ranks = @(11, 12, 13),
    [int]$SourceRank = 10
)

$ErrorActionPreference = "Stop"

function Copy-EffectSet([string]$effectRoot, [string]$gender, [int]$sourceRank, [int]$targetRank) {
    $sourceToken = $sourceRank.ToString("0000")
    $targetToken = $targetRank.ToString("0000")
    $sourcePattern = "${gender}_body_effect_${sourceToken}*"
    $sources = @(Get-ChildItem -LiteralPath $effectRoot -Filter $sourcePattern -File -ErrorAction Stop)

    if ($sources.Count -eq 0) {
        throw "Missing source body effect set: $effectRoot\$sourcePattern"
    }

    foreach ($source in $sources) {
        $targetName = $source.Name.Replace("_$sourceToken", "_$targetToken")
        $targetPath = Join-Path $effectRoot $targetName
        if (Test-Path -LiteralPath $targetPath) {
            continue
        }

        Copy-Item -LiteralPath $source.FullName -Destination $targetPath
    }
}

$effectRoots = @(
    (Join-Path $ClientRoot "Characters\effect"),
    (Join-Path $ClientRoot "Characters_New\effect")
)

foreach ($effectRoot in $effectRoots) {
    if (-not (Test-Path -LiteralPath $effectRoot)) {
        throw "Effect directory not found: $effectRoot"
    }

    foreach ($rank in $Ranks) {
        Copy-EffectSet $effectRoot "male" $SourceRank $rank
        Copy-EffectSet $effectRoot "female" $SourceRank $rank
    }
}

Get-ChildItem -Path $effectRoots -Filter '*body_effect_001[1-3]*' -File |
    Select-Object DirectoryName, Name, Length |
    Sort-Object DirectoryName, Name
